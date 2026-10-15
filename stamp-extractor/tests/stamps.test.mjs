import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { PNG } from 'pngjs';
import { detectStampFrames, pointInStampFrame, extractStampMask } from '../lib/stamps.js';
import { prepareInk } from '../lib/processing.js';

function canvas(width = 720, height = 480) {
  const data = new Uint8ClampedArray(width * height * 4); data.fill(255);
  return { width, height, data, mask: new Uint8Array(width * height) };
}
function line(image, a, b, color = [50, 70, 160], radius = 1) {
  const count = Math.ceil(Math.hypot(b.x - a.x, b.y - a.y) * 2);
  for (let step = 0; step <= count; step++) {
    const x = a.x + (b.x - a.x) * step / count, y = a.y + (b.y - a.y) * step / count;
    for (let yy = Math.floor(y - radius); yy <= Math.ceil(y + radius); yy++) for (let xx = Math.floor(x - radius); xx <= Math.ceil(x + radius); xx++) {
      if (xx < 0 || yy < 0 || xx >= image.width || yy >= image.height || (xx - x) ** 2 + (yy - y) ** 2 > radius ** 2) continue;
      const i = yy * image.width + xx; image.mask[i] = 1; image.data.set([...color, 255], i * 4);
    }
  }
}
function frame(image, x, y, width = 380, height = 120, degrees = -4, color = [50, 70, 160]) {
  const a = degrees * Math.PI / 180, point = (u, v) => ({ x: x + u * Math.cos(a) - v * Math.sin(a), y: y + u * Math.sin(a) + v * Math.cos(a) });
  const quad = [point(0, 0), point(width, 0), point(width, height), point(0, height)];
  quad.forEach((p, i) => line(image, p, quad[(i + 1) % 4], color, 1.4));
  // Multiple rows of disconnected printed E glyphs, in the frame's ink colour.
  for (let row = 0; row < 3; row++) for (let col = 0; col < 17; col++) {
    const u = 22 + col * 19, v = 20 + row * 30;
    line(image, point(u, v), point(u, v + 15), color);
    for (const d of [0, 7, 15]) line(image, point(u, v + d), point(u + 10, v + d), color);
  }
  return { quad, point };
}

test('tilted approval frames survive handwriting connected across their borders', () => {
  const image = canvas(760, 560), first = frame(image, 160, 80), second = frame(image, 165, 300, 380, 130, -2);
  for (const y of [145, 345]) {
    let previous = { x: 20, y };
    for (let x = 25; x < 700; x += 5) {
      const point = { x, y: y + Math.sin(x / 13) * 15 }; line(image, previous, point, [15, 15, 95], 1.3); previous = point;
    }
  }
  const result = detectStampFrames(image.mask, image.width, image.height);
  assert.equal(result.length, 2);
  for (const [i, expected] of [first, second].entries()) {
    assert.ok(Math.abs(result[i].quad[0].x - expected.quad[0].x) < 5);
    assert.ok(Math.abs(result[i].quad[0].y - expected.quad[0].y) < 5);
    assert.ok(result[i].borderCoverage.every(n => n > .90));
    assert.equal(pointInStampFrame(result[i], 30, 145), false, 'external handwriting cannot enlarge the seal');
  }
});

test('text glyphs, handwriting and neutral table cells do not become rectangular seals', () => {
  const image = canvas();
  for (let row = 0; row < 5; row++) for (let col = 0; col < 30; col++) {
    const x = 30 + col * 19, y = 40 + row * 40;
    line(image, { x, y }, { x, y: y + 18 }); line(image, { x, y }, { x: x + 10, y });
    line(image, { x, y: y + 9 }, { x: x + 10, y: y + 9 });
  }
  assert.equal(detectStampFrames(image.mask, image.width, image.height).length, 0);
  const table = canvas(); frame(table, 80, 100, 420, 150, 0, [20, 20, 20]);
  assert.equal(detectStampFrames(table.mask, table.width, table.height, 'black').length, 0);
});

test('frame extraction preserves all original seal lettering and suppresses separately coloured handwriting', () => {
  const image = canvas(), shape = frame(image, 110, 120), clean = new Uint8Array(image.mask);
  // Different blue pen crosses the border and the gaps between the printed rows.
  const p1 = shape.point(-65, 44), p2 = shape.point(450, 44);
  line(image, p1, p2, [15, 15, 95], 1.2);
  const before = new Uint8Array(image.mask);
  const found = detectStampFrames(image.mask, image.width, image.height);
  assert.equal(found.length, 1);
  const result = extractStampMask(image, image.mask, found[0]);
  assert.ok(result.clippedPixels > 200);
  assert.ok(result.removedForeignPixels > 300);
  let preserved = 0, total = 0;
  for (let i = 0; i < clean.length; i++) if (clean[i]) { total++; if (result.mask[i]) preserved++; }
  assert.ok(preserved / total > .97, 'seal text and frame are protected independently of OCR');
  assert.deepEqual(image.mask, before, 'input mask is never changed');
  const ink = shape.point(42, 44), index = Math.round(ink.y) * image.width + Math.round(ink.x);
  assert.equal(result.mask[index], 0);
});

test('same-colour overlap is retained instead of inventing stamp text under a pen stroke', () => {
  const image = canvas(), shape = frame(image, 110, 120);
  line(image, shape.point(0, 44), shape.point(380, 44), [50, 70, 160], 1.2);
  const found = detectStampFrames(image.mask, image.width, image.height), before = new Uint8Array(image.mask);
  const result = extractStampMask(image, image.mask, found[0]);
  assert.equal(result.removedForeignPixels, 0);
  assert.deepEqual(image.mask, before);
});

test('authored Russian approval document: both invaded tilted frames are found, with own lettering retained', () => {
  // Authored regression layout inspired by the reported failures, not the user's
  // original scan. No OCR hints or expected boxes are provided to the detector.
  const image = PNG.sync.read(fs.readFileSync(new URL('../examples/failures-rectangular.png', import.meta.url)));
  const prepared = prepareInk(image, { mode: 'blue' });
  const frames = detectStampFrames(prepared.mask, image.width, image.height);
  assert.equal(frames.length, 2);
  for (const [index, id] of ['approval-top', 'approval-bottom'].entries()) {
    const truth = PNG.sync.read(fs.readFileSync(new URL(`./fixtures-v3/failures-rectangular.${id}.letter.mask.png`, import.meta.url)));
    const visibleTruth = PNG.sync.read(fs.readFileSync(new URL(`./fixtures-v3/failures-rectangular.${id}.visible.mask.png`, import.meta.url)));
    const result = extractStampMask(image, prepared.mask, frames[index]);
    let visible = 0, retained = 0;
    // Pixels physically covered by the overlaid pen are not visible stamp text.
    for (let i = 0; i < prepared.mask.length; i++) if (truth.data[i * 4] >= 128 && visibleTruth.data[i * 4] >= 128 && prepared.mask[i]) {
      visible++; if (result.mask[i]) retained++;
    }
    assert.ok(retained / visible > .99, `${id}: visible stamped text must not disappear during frame extraction`);
    assert.ok(frames[index].w > 570 && frames[index].w < 600);
  }
  const body = PNG.sync.read(fs.readFileSync(new URL('./fixtures-v3/failures-rectangular.handwritten-body.mask.png', import.meta.url)));
  const lower = extractStampMask(image, prepared.mask, frames[1]);
  let bodyInk = 0, removedBody = 0;
  for (let i = 0; i < prepared.mask.length; i++) if (body.data[i * 4] >= 128 && prepared.mask[i] && pointInStampFrame(frames[1], i % image.width, Math.floor(i / image.width))) {
    bodyInk++; if (!lower.mask[i]) removedBody++;
  }
  assert.ok(removedBody / bodyInk > .95, 'stable frame colour must distinguish the nearby blue pen, not just red from blue');
});

test('adaptive frame colour preserves faint lettering and backs off for shadows and JPEG-like chroma subsampling', () => {
  const original = PNG.sync.read(fs.readFileSync(new URL('../examples/failures-rectangular.png', import.meta.url)));
  const sourceMask = prepareInk(original, { mode: 'blue' }).mask;
  const frames = detectStampFrames(sourceMask, original.width, original.height);
  const truth = ['approval-top', 'approval-bottom'].map(id => ({
    letters: PNG.sync.read(fs.readFileSync(new URL(`./fixtures-v3/failures-rectangular.${id}.letter.mask.png`, import.meta.url))).data,
    visible: PNG.sync.read(fs.readFileSync(new URL(`./fixtures-v3/failures-rectangular.${id}.visible.mask.png`, import.meta.url))).data
  }));
  for (const variation of ['faint', 'shadow', 'chroma']) {
    const image = { width: original.width, height: original.height, data: new Uint8ClampedArray(original.data) };
    if (variation !== 'chroma') {
      for (let y = 0; y < image.height; y++) for (let x = 0; x < image.width; x++) for (let c = 0; c < 3; c++) {
        const i = (y * image.width + x) * 4 + c;
        image.data[i] = variation === 'faint' ? 255 - (255 - original.data[i]) * .38 : original.data[i] * (.72 + .28 * x / image.width);
      }
    } else {
      // JPEG 4:2:0 keeps detailed luminance but averages colour across 2x2 blocks.
      // Testing that operation directly avoids a platform-specific JPEG codec.
      for (let y = 0; y < image.height; y += 2) for (let x = 0; x < image.width; x += 2) {
        const pixels = []; let cb = 0, cr = 0;
        for (let dy = 0; dy < 2 && y + dy < image.height; dy++) for (let dx = 0; dx < 2 && x + dx < image.width; dx++) {
          const i = ((y + dy) * image.width + x + dx) * 4, r = original.data[i], g = original.data[i + 1], b = original.data[i + 2];
          const luminance = .299 * r + .587 * g + .114 * b;
          cb += (b - luminance) / 1.772; cr += (r - luminance) / 1.402;
          pixels.push({ i, luminance });
        }
        cb /= pixels.length; cr /= pixels.length;
        for (const { i, luminance } of pixels) {
          image.data[i] = luminance + 1.402 * cr;
          image.data[i + 1] = luminance - .344136 * cb - .714136 * cr;
          image.data[i + 2] = luminance + 1.772 * cb;
        }
      }
    }
    const prepared = prepareInk(image, { mode: 'blue' });
    for (const [index, frame] of frames.entries()) {
      const result = extractStampMask(image, prepared.mask, frame); let visible = 0, retained = 0;
      for (let i = 0; i < prepared.mask.length; i++) if (prepared.mask[i] && truth[index].letters[i * 4] >= 128 && truth[index].visible[i * 4] >= 128) {
        visible++; if (result.mask[i]) retained++;
      }
      assert.ok(retained / visible > .99, `${variation}, frame ${index}: preserve visible print despite colour noise`);
      if (variation === 'chroma') assert.equal(result.colourAmbiguous, true, 'uncertain colour separation must retain ink');
    }
  }
});
