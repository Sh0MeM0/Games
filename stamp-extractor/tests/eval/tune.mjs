#!/usr/bin/env node
// Grid search of post-processing constants on VALIDATION sets only.
// Usage: node tests/eval/tune.mjs <set dir>... [--multiscale] [--model path]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { PNG } from 'pngjs';
import * as ort from '../../vendor/ort/ort.wasm.min.mjs';
import { segmentImage, segmentMultiScale } from '../../lib/segmentation.js';
import { detectObjects, inkGrid, TUNING } from '../../lib/objects.js';
import { workingImage, regionsToSource } from '../../lib/pipeline.js';
import { score } from './score.mjs';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const args = process.argv.slice(2), sets = args.filter(a => !a.startsWith('--') && fs.existsSync(path.join(a, 'gt.json')));
const option = (name, value) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : value; };
ort.env.wasm.wasmPaths = path.join(root, 'vendor', 'ort') + '/'; ort.env.wasm.numThreads = 1; ort.env.logLevel = 'error';
const session = await ort.InferenceSession.create(fs.readFileSync(option('--model', path.join(root, 'models', 'ink-seg.onnx'))));

const grid = [];
for (const sensitivity of [0.4, 0.5, 0.6]) for (const stampMinMean of [0.55, 0.62, 0.7]) for (const signMinMean of [0.5, 0.58, 0.66])
  for (const signHandRatio of [0.4, 0.55]) for (const signInsideStamp of [0.5, 0.8]) for (const signRx of [7, 11])
    grid.push({ sensitivity, tuning: { stampMinMean, signMinMean, signHandRatio, signInsideStamp, signRx } });
const results = new Map(sets.map(set => [set, grid.map(() => [])]));
const gts = new Map(sets.map(set => [set, JSON.parse(fs.readFileSync(path.join(set, 'gt.json'), 'utf8'))]));
for (const set of sets) {
  for (const item of gts.get(set)) {
    const png = PNG.sync.read(fs.readFileSync(path.join(set, item.file)));
    const source = { width: png.width, height: png.height, data: new Uint8ClampedArray(png.data) };
    const { work, scale } = workingImage(source);
    const maps = await (args.includes('--multiscale') ? segmentMultiScale : segmentImage)(session, ort, work);
    const ink = inkGrid(work, {});
    grid.forEach((g, k) => {
      const found = detectObjects(work, maps, { sensitivity: g.sensitivity, tuning: g.tuning, ink });
      results.get(set)[k].push({ file: item.file, regions: regionsToSource(found.regions, scale, source) });
    });
  }
  console.error('done', path.basename(set));
}
const rows = grid.map((g, k) => {
  const per = sets.map(set => score(gts.get(set), results.get(set)[k]).stats);
  // Mean F1 over sets and kinds that have ground truth.
  const f1s = per.flatMap(stats => Object.values(stats).filter(s => s.tp + s.fn > 0).map(s => s.f1));
  return { k, g, mean: f1s.reduce((a, b) => a + b, 0) / f1s.length, per };
}).sort((a, b) => b.mean - a.mean);
const fmt = stats => Object.entries(stats).map(([kind, s]) => `${kind[0]} P${(100 * (s.precision ?? 0)).toFixed(0)} R${(100 * (s.recall ?? 0)).toFixed(0)}`).join(' ');
for (const row of rows.slice(0, 8)) console.log(row.mean.toFixed(3), JSON.stringify(row.g), row.per.map(fmt).join(' | '));
const base = rows.find(r => r.g.sensitivity === 0.5 && r.g.tuning.stampMinMean === TUNING.stampMinMean && r.g.tuning.signMinMean === TUNING.signMinMean &&
  r.g.tuning.signHandRatio === TUNING.signHandRatio && r.g.tuning.signInsideStamp === TUNING.signInsideStamp && r.g.tuning.signRx === TUNING.signRx);
if (base) console.log('current defaults', base.mean.toFixed(3), base.per.map(fmt).join(' | '));
