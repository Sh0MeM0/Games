#!/usr/bin/env node
// Writes a colour map of the model output: print=black, line=grey, hand=green,
// signature=orange, stamp=blue (strongest class above 0.5 on ink pixels).
// Usage: node tests/eval/visualize.mjs <in.png> <out.png> [--model path] [--multiscale]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { PNG } from 'pngjs';
import * as ort from '../../vendor/ort/ort.wasm.min.mjs';
import { segmentImage, segmentMultiScale } from '../../lib/segmentation.js';
import { inkGrid } from '../../lib/objects.js';
import { workingImage } from '../../lib/pipeline.js';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const args = process.argv.slice(2), option = (n, v) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : v; };
ort.env.wasm.wasmPaths = path.join(root, 'vendor', 'ort') + '/'; ort.env.wasm.numThreads = 1; ort.env.logLevel = 'error';
const session = await ort.InferenceSession.create(fs.readFileSync(option('--model', path.join(root, 'models', 'ink-seg.onnx'))));
const png = PNG.sync.read(fs.readFileSync(args[0]));
const { work } = workingImage({ width: png.width, height: png.height, data: new Uint8ClampedArray(png.data) });
const maps = await (args.includes('--multiscale') ? (s, o, w) => segmentMultiScale(s, o, w, { minSide: 600 }) : segmentImage)(session, ort, work);
const ink = inkGrid(work, {}), colors = [[0, 0, 0], [150, 150, 150], [0, 160, 0], [235, 120, 0], [30, 70, 230]];
const out = new PNG({ width: maps.width * 2, height: maps.height });
for (let y = 0; y < maps.height; y++) for (let x = 0; x < maps.width; x++) {
  const i = y * maps.width + x;
  let best = -1, value = 127; for (let c = 0; c < 5; c++) if (maps.planes[c][i] > value) { value = maps.planes[c][i]; best = c; }
  const left = (y * out.width + x) * 4, right = (y * out.width + x + maps.width) * 4;
  const wp = ((y * 2) * work.width + x * 2) * 4;
  out.data.set([work.data[wp], work.data[wp + 1], work.data[wp + 2], 255], left);
  out.data.set(ink.grid[i] && best >= 0 ? [...colors[best], 255] : ink.grid[i] ? [255, 0, 255, 255] : [255, 255, 255, 255], right);
}
fs.writeFileSync(args[1], PNG.sync.write(out));
