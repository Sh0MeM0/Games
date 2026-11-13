import test from 'node:test';
import assert from 'node:assert/strict';
import { renderObject, cropProbabilities, targetClasses } from '../lib/render.js';
import { CLASS_INDEX } from '../lib/segmentation.js';

// 200×100 page: a blue stamp stroke, a black printed bar crossing it, a blue signature stroke.
function fixture() {
  const width = 200, height = 100, data = new Uint8ClampedArray(width * height * 4).fill(255);
  const maps = { width: 100, height: 50, planes: Array.from({ length: 5 }, () => new Uint8Array(5000)) };
  const put = (x, y, rgb, classes) => {
    data.set([...rgb, 255], (y * width + x) * 4);
    for (const c of classes) maps.planes[c][(y >> 1) * 100 + (x >> 1)] = 240;
  };
  for (let x = 20; x < 120; x++) for (let y = 30; y < 34; y++) put(x, y, [40, 60, 180], [CLASS_INDEX.stamp]);
  for (let x = 60; x < 64; x++) for (let y = 10; y < 60; y++) put(x, y, [10, 10, 10], [CLASS_INDEX.print, CLASS_INDEX.stamp]);
  for (let x = 130; x < 190; x++) for (let y = 70; y < 73; y++) put(x, y, [30, 40, 150], [CLASS_INDEX.signature]);
  return { source: { width, height, data }, context: { maps, scale: 1 } };
}
const count = (r, test) => { let n = 0; for (let i = 0; i < r.width * r.height; i++) { const p = i * 4; if (test(r.data[p], r.data[p + 1], r.data[p + 2])) n++; } return n; };
const options = { kind: 'stamp', threshold: 16, speckle: 0, contrast: 1 };
const rect = { x: 0, y: 0, w: 200, h: 100 };

test('target classes follow the object type and stamp layer', () => {
  assert.deepEqual(targetClasses({ kind: 'stamp' }), [CLASS_INDEX.stamp]);
  assert.deepEqual(targetClasses({ kind: 'stamp', stampLayer: 'whole' }).sort(), [CLASS_INDEX.hand, CLASS_INDEX.signature, CLASS_INDEX.stamp].sort());
  assert.equal(targetClasses({ kind: 'candidate' }), null);
});

test('stamp output keeps stamp ink and drops the signature and the foreign black text', () => {
  const { source, context } = fixture();
  const r = renderObject(source, rect, options, [], context);
  assert.ok(count(r, (R, G, B) => B > 140 && R < 80) >= 380, 'stamp stroke kept');
  assert.equal(count(r, (R, G, B) => R < 40 && G < 40 && B < 40), 0, 'black printed pixels removed');
  assert.ok(r.uncertainPixels > 0);
  // The signature (different object class) is removed.
  for (let x = 130; x < 190; x++) assert.equal(r.data[(71 * 200 + x) * 4], 255);
  assert.equal(r.inferredPixels, 0, 'nothing invented without repair');
});

test('with repair enabled, overlapped pixels are recoloured with the stamp ink and disclosed', () => {
  const { source, context } = fixture();
  const r = renderObject(source, rect, { ...options, repair: true }, [], context);
  assert.ok(r.inferredPixels >= 16);
  assert.equal(count(r, (R, G, B) => R < 40 && G < 40 && B < 40), 0);
});

test('signature output keeps only the signature', () => {
  const { source, context } = fixture();
  const r = renderObject(source, rect, { ...options, kind: 'signature' }, [], context);
  assert.equal(r.inkPixels, 60 * 3);
});

test('manual area removes printed text but keeps other ink; isolation off keeps unclassified ink', () => {
  const { source, context } = fixture();
  const area = renderObject(source, rect, { ...options, kind: 'candidate' }, [], context);
  // Only crossing pixels where the stamp is at least as likely as print may stay (≤ the 4×4 crossing area at map borders).
  assert.ok(count(area, (R, G, B) => R < 40 && G < 40 && B < 40) <= 16);
  assert.ok(area.inkPixels >= 400 + 180 - 16);
  const off = renderObject(source, rect, { ...options, kind: 'stamp', removeText: false, isolate: false }, [], context);
  assert.ok(count(off, (R, G, B) => R < 40 && G < 40 && B < 40) > 0);
});

test('without maps the renderer falls back to the plain ink mask', () => {
  const { source } = fixture();
  const r = renderObject(source, rect, options, [], null);
  assert.equal(r.inkPixels, 400 + 4 * 50 - 16 + 180);
});

test('crop probabilities follow the requested rectangle', () => {
  const { context } = fixture();
  const p = cropProbabilities(context.maps, 1, { x: 130, y: 70, w: 10, h: 3 });
  assert.ok(p[CLASS_INDEX.signature][1 * 10 + 5] > 100);
});
