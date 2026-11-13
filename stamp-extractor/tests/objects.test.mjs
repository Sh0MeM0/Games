import test from 'node:test';
import assert from 'node:assert/strict';
import { components, detectObjects, inkClass } from '../lib/objects.js';
import { CLASS_INDEX } from '../lib/segmentation.js';

function scene(width, height) {
  const data = new Uint8ClampedArray(width * height * 4).fill(255);
  const maps = { width: width / 2, height: height / 2, planes: Array.from({ length: 5 }, () => new Uint8Array(width * height / 4)) };
  return { work: { width, height, data }, maps };
}
function ink(s, x0, y0, x1, y1, rgb, cls, p = 240, test = () => true) {
  for (let y = y0; y < y1; y++) for (let x = x0; x < x1; x++) if (test(x, y)) {
    s.work.data.set([...rgb, 255], (y * s.work.width + x) * 4);
    s.maps.planes[cls][(y >> 1) * s.maps.width + (x >> 1)] = p;
  }
}

test('connected components report boxes and areas', () => {
  const mask = new Uint8Array(25); mask[0] = mask[6] = 1; mask[24] = 1;
  const { boxes } = components(mask, 5, 5);
  assert.equal(boxes.length, 2); assert.deepEqual([boxes[0].w, boxes[0].h, boxes[0].area], [2, 2, 2]);
});

test('ink colour classes', () => {
  assert.equal(inkClass(30, 50, 170), 'blue'); assert.equal(inkClass(200, 40, 50), 'red'); assert.equal(inkClass(30, 30, 30), 'black');
});

test('a ring classified as stamp and a cursive stroke classified as signature become two objects', () => {
  const s = scene(800, 600);
  ink(s, 100, 100, 400, 400, [40, 60, 170], CLASS_INDEX.stamp, 240, (x, y) => Math.abs(Math.hypot(x - 250, y - 250) - 120) < 4);
  ink(s, 450, 300, 750, 380, [20, 40, 150], CLASS_INDEX.signature, 240, (x, y) => Math.abs(y - 340 - 30 * Math.sin(x / 15)) < 3);
  const { regions } = detectObjects(s.work, s.maps, {});
  assert.deepEqual(regions.map(r => r.kind).sort(), ['signature', 'stamp']);
  const stamp = regions.find(r => r.kind === 'stamp');
  assert.ok(stamp.x <= 130 && stamp.x + stamp.w >= 370 && stamp.mode === 'blue');
});

test('printed text and handwriting alone produce no stamp or signature', () => {
  const s = scene(800, 600);
  for (let row = 0; row < 8; row++) ink(s, 50, 50 + row * 30, 700, 60 + row * 30, [20, 20, 20], CLASS_INDEX.print, 240, x => x % 9 < 5);
  ink(s, 100, 400, 500, 430, [30, 50, 160], CLASS_INDEX.hand, 240, (x, y) => Math.abs(y - 415 - 8 * Math.sin(x / 6)) < 2);
  const { regions } = detectObjects(s.work, s.maps, {});
  assert.equal(regions.length, 0);
});

test('ink filter keeps only objects of the requested colour', () => {
  const s = scene(800, 600);
  ink(s, 100, 100, 400, 400, [200, 40, 50], CLASS_INDEX.stamp, 240, (x, y) => Math.abs(Math.hypot(x - 250, y - 250) - 120) < 4);
  assert.equal(detectObjects(s.work, s.maps, { mode: 'blue' }).regions.length, 0);
  assert.equal(detectObjects(s.work, s.maps, { mode: 'red' }).regions.length, 1);
});
