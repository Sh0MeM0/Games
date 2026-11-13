#!/usr/bin/env node
// Runs the v4 pipeline (same modules as the app worker) on an evaluation set.
// Usage: node tests/eval/run.mjs <set dir> <out.json> [--sensitivity 0.5] [--model models/ink-seg.onnx]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { PNG } from 'pngjs';
import * as ort from '../../vendor/ort/ort.wasm.min.mjs';
import { segmentImage, segmentMultiScale } from '../../lib/segmentation.js';
import { detectObjects } from '../../lib/objects.js';
import { workingImage, regionsToSource } from '../../lib/pipeline.js';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const args = process.argv.slice(2), option = (name, value) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : value; };
const [setDir, outFile] = args;
ort.env.wasm.wasmPaths = path.join(root, 'vendor', 'ort') + '/'; ort.env.wasm.numThreads = 1; ort.env.logLevel = 'error';
const session = await ort.InferenceSession.create(fs.readFileSync(option('--model', path.join(root, 'models', 'ink-seg.onnx'))));
const gt = JSON.parse(fs.readFileSync(path.join(setDir, 'gt.json'), 'utf8')).filter((_, i) => i % Number(option('--step', 1)) === 0);
const results = [];
for (const item of gt) {
  const png = PNG.sync.read(fs.readFileSync(path.join(setDir, item.file)));
  const source = { width: png.width, height: png.height, data: new Uint8ClampedArray(png.data) };
  const t0 = Date.now(), { work, scale } = workingImage(source);
  const maps = await (args.includes('--multiscale') ? (s, o, w) => segmentMultiScale(s, o, w, { minSide: Number(option('--ms-min', 1000)) }) : segmentImage)(session, ort, work), t1 = Date.now();
  const found = detectObjects(work, maps, { sensitivity: Number(option('--sensitivity', 0.5)) });
  results.push({ file: item.file, modelMs: t1 - t0, detectMs: Date.now() - t1,
    regions: regionsToSource(found.regions, scale, source).map(r => ({ kind: r.kind, x: r.x, y: r.y, w: r.w, h: r.h, confidence: r.confidence })) });
  if (args.includes('--verbose')) console.log(item.file, results.at(-1).regions.map(r => r.kind).join(','), `${t1 - t0}+${Date.now() - t1} ms`);
}
fs.writeFileSync(outFile, JSON.stringify(results, null, 1));
