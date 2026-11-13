import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import * as ort from '../vendor/ort/ort.wasm.min.mjs';
import { paperColor, modelInput, resizeImage, workingScale, segmentImage, sampleMap, CLASSES, WORK_SIDE } from '../lib/segmentation.js';

ort.env.wasm.wasmPaths = new URL('../vendor/ort/', import.meta.url).pathname; ort.env.wasm.numThreads = 1; ort.env.logLevel = 'error';
function page(width, height, paper = [240, 236, 228]) {
  const data = new Uint8ClampedArray(width * height * 4);
  for (let i = 0; i < width * height; i++) data.set([...paper, 255], i * 4);
  return { width, height, data };
}

test('working scale keeps small screenshots and reduces large pages to the working side', () => {
  assert.equal(workingScale(900, 600), 1);
  assert.equal(Math.round(4000 * workingScale(4000, 3000)), WORK_SIDE);
});

test('area resampling preserves the mean colour', () => {
  const image = page(10, 10); image.data[0] = 0;
  const small = resizeImage(image, 5, 5);
  assert.equal(small.width, 5); assert.ok(Math.abs(small.data[0] - (0 + 240 * 3) / 4) < 1);
});

test('paper colour is the 97th percentile and normalises paper to about +0.5', () => {
  const image = page(64, 64);
  for (let i = 0; i < 300; i++) image.data.set([20, 30, 160, 255], i * 4 * 7);
  assert.deepEqual(paperColor(image), [240, 236, 228]);
  const { input, width, height } = modelInput(image);
  assert.equal(width % 16, 0); assert.equal(height % 16, 0);
  assert.ok(Math.abs(input[64 * 64 - 1] - 0.5) < 1e-6);
});

test('transparent pixels are treated as white paper', () => {
  const image = page(32, 32, [0, 0, 0]); for (let i = 3; i < image.data.length; i += 4) image.data[i] = 0;
  assert.deepEqual(paperColor(image), [255, 255, 255]);
});

test('the bundled model runs and returns five probability planes at half resolution', async () => {
  const session = await ort.InferenceSession.create(fs.readFileSync(new URL('../models/ink-seg.onnx', import.meta.url)));
  const maps = await segmentImage(session, ort, page(100, 70));
  assert.equal(maps.planes.length, CLASSES.length); assert.equal(maps.width, 50); assert.equal(maps.height, 35);
  for (const plane of maps.planes) assert.equal(plane.length, 50 * 35);
  // An empty page has no confident ink classes.
  for (const plane of maps.planes) assert.ok(Math.max(...plane) < 128);
  const p = sampleMap(maps, 0, 10, 10); assert.ok(p >= 0 && p <= 1);
});
