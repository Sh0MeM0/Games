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
if (!option('--fixed')) args.push('--fixed', '{"tuning":{}}');
ort.env.wasm.wasmPaths = path.join(root, 'vendor', 'ort') + '/'; ort.env.wasm.numThreads = 1; ort.env.logLevel = 'error';
const session = await ort.InferenceSession.create(fs.readFileSync(option('--model', path.join(root, 'models', 'ink-seg.onnx'))));

// Two stages: stamp constants (scored on stamps), then signature constants
// (scored on signatures) with the chosen stamp constants.
const stage = Number(option('--stage', 1)), fixed = JSON.parse(option('--fixed', '{}'));
const grid = [];
if (stage === 1) {
  for (const sensitivity of [0.4, 0.5, 0.6]) for (const stampMinMean of [0.55, 0.62, 0.7]) for (const stampMerge of [1.2, 1.45])
    grid.push({ sensitivity, tuning: { stampMinMean, stampMerge } });
} else if (stage === 2) {
  for (const signMinMean of [0.5, 0.58, 0.66]) for (const signHandRatio of [0.4, 0.55]) for (const signRx of [7, 11]) for (const signInsideStamp of [0.5, 0.8])
    grid.push({ sensitivity: fixed.sensitivity ?? 0.5, tuning: { ...fixed.tuning, signMinMean, signHandRatio, signRx, signInsideStamp } });
}
const scoredKinds = stage === 2 ? ['signature'] : ['stamp'];
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
  const per = sets.map(set => score(gts.get(set), results.get(set)[k], scoredKinds).stats);
  // F1 of TP/FP/FN pooled over all sets, so false objects on photographs
  // without stamps still lower stamp precision.
  const f1s = scoredKinds.map(kind => {
    const t = per.reduce((acc, stats) => ({ tp: acc.tp + stats[kind].tp, fp: acc.fp + stats[kind].fp, fn: acc.fn + stats[kind].fn }), { tp: 0, fp: 0, fn: 0 });
    return t.tp ? 2 * t.tp / (2 * t.tp + t.fp + t.fn) : 0;
  });
  return { k, g, mean: f1s.reduce((a, b) => a + b, 0) / f1s.length, per };
}).sort((a, b) => b.mean - a.mean);
const fmt = stats => Object.entries(stats).map(([kind, s]) => `${kind[0]} TP${s.tp} FP${s.fp} FN${s.fn}`).join(' ');
for (const row of rows.slice(0, 8)) console.log(row.mean.toFixed(3), JSON.stringify(row.g), row.per.map(fmt).join(' | '));
const base = rows.find(r => (stage === 1 || r.g.sensitivity === (fixed.sensitivity ?? 0.5)) && (stage !== 1 || r.g.sensitivity === 0.5) &&
  Object.entries(r.g.tuning).every(([key, value]) => key in fixed.tuning || TUNING[key] === value || (stage === 1 && !(key in TUNING))));
if (base) console.log('current defaults', base.mean.toFixed(3), JSON.stringify(base.g), base.per.map(fmt).join(' | '));
