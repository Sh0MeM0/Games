/** Object proposals from the segmentation maps.
 *
 * Detection runs on the half-resolution probability grid. Ink is measured
 * independently from the paper estimate, so the model can only classify
 * existing strokes; it never adds pixels to an object.
 */
import { prepareInk, maxFilter } from './processing.js';
import { CLASS_INDEX } from './segmentation.js';

const { print: PRINT, line: LINE, hand: HAND, signature: SIGN, stamp: STAMP } = CLASS_INDEX;
const clamp = (n, lo, hi) => Math.max(lo, Math.min(hi, n));

/** 8-connected components of a binary mask with bounding boxes. */
export function components(mask, width, height) {
  const labels = new Int32Array(mask.length), queue = new Int32Array(mask.length), boxes = [];
  for (let start = 0; start < mask.length; start++) {
    if (!mask[start] || labels[start]) continue;
    const id = boxes.length + 1;
    let head = 0, tail = 1, left = start % width, right = left, top = (start / width) | 0, bottom = top;
    queue[0] = start; labels[start] = id;
    while (head < tail) {
      const index = queue[head++], x = index % width, y = (index / width) | 0;
      if (x < left) left = x; if (x > right) right = x; if (y < top) top = y; if (y > bottom) bottom = y;
      for (let dy = -1; dy <= 1; dy++) {
        const ny = y + dy; if (ny < 0 || ny >= height) continue;
        for (let dx = -1; dx <= 1; dx++) {
          const nx = x + dx, n = ny * width + nx;
          if (nx >= 0 && nx < width && mask[n] && !labels[n]) { labels[n] = id; queue[tail++] = n; }
        }
      }
    }
    boxes.push({ id, x: left, y: top, w: right - left + 1, h: bottom - top + 1, area: tail });
  }
  return { labels, boxes };
}

/** Ink at map resolution: a map cell has ink if any of its 2×2 working pixels has. */
export function inkGrid(work, options = {}) {
  const prepared = prepareInk(work, { threshold: options.threshold ?? 16, speckle: options.speckle ?? 2, mode: 'all', contrast: 1 });
  const w2 = Math.ceil(work.width / 2), h2 = Math.ceil(work.height / 2), grid = new Uint8Array(w2 * h2);
  for (let y = 0; y < work.height; y++) for (let x = 0; x < work.width; x++)
    if (prepared.mask[y * work.width + x]) grid[(y >> 1) * w2 + (x >> 1)] = 1;
  return { prepared, grid, width: w2, height: h2 };
}

export function inkClass(r, g, b) {
  if (b - r > 12 && b - g > 4) return 'blue';
  if (r - g > 22 && r - b > 15) return 'red';
  if (Math.max(r, g, b) - Math.min(r, g, b) > 38) return 'colored';
  return 'black';
}

function dominantInk(work, maps, plane, box, threshold) {
  const counts = { black: 0, blue: 0, red: 0, colored: 0 };
  for (let y = box.y; y < box.y + box.h; y++) for (let x = box.x; x < box.x + box.w; x++) {
    if (plane[y * maps.width + x] < threshold) continue;
    for (let dy = 0; dy < 2; dy++) for (let dx = 0; dx < 2; dx++) {
      const wx = x * 2 + dx, wy = y * 2 + dy;
      if (wx >= work.width || wy >= work.height) continue;
      const p = (wy * work.width + wx) * 4;
      if (Math.min(work.data[p], work.data[p + 1], work.data[p + 2]) > 225) continue;
      counts[inkClass(work.data[p], work.data[p + 1], work.data[p + 2])]++;
    }
  }
  return Object.entries(counts).sort((a, b) => b[1] - a[1])[0][0];
}

const overlapArea = (a, b) => Math.max(0, Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x)) *
  Math.max(0, Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y));
const unionBox = (a, b) => {
  const x = Math.min(a.x, b.x), y = Math.min(a.y, b.y);
  return { x, y, w: Math.max(a.x + a.w, b.x + b.w) - x, h: Math.max(a.y + a.h, b.y + b.h) - y };
};

/** Group confident pixels of one class into candidate boxes (map coordinates). */
function proposals(maps, ink, cls, { threshold, rx, ry, minArea, minW, minH }) {
  const { width, height } = maps, plane = maps.planes[cls], seed = new Uint8Array(width * height);
  for (let i = 0; i < seed.length; i++) if (ink.grid[i] && plane[i] >= threshold) seed[i] = 1;
  const grouped = maxFilter(seed, width, height, rx, ry);
  const { labels, boxes } = components(grouped, width, height);
  const stats = boxes.map(box => ({ ...box, x: width, y: height, right: -1, bottom: -1, count: 0, sum: 0 }));
  for (let i = 0; i < seed.length; i++) {
    if (!seed[i]) continue;
    const s = stats[labels[i] - 1], x = i % width, y = (i / width) | 0;
    s.count++; s.sum += plane[i];
    if (x < s.x) s.x = x; if (x > s.right) s.right = x; if (y < s.y) s.y = y; if (y > s.bottom) s.bottom = y;
  }
  return stats.filter(s => s.count >= minArea).map(s => ({ x: s.x, y: s.y, w: s.right - s.x + 1, h: s.bottom - s.y + 1,
    count: s.count, mean: s.sum / s.count / 255 })).filter(b => b.w >= minW && b.h >= minH);
}

function classMass(maps, ink, box, cls, threshold) {
  const plane = maps.planes[cls]; let n = 0;
  for (let y = box.y; y < box.y + box.h; y++) for (let x = box.x; x < box.x + box.w; x++) {
    const i = y * maps.width + x; if (ink.grid[i] && plane[i] >= threshold) n++;
  }
  return n;
}

/** Merge boxes of the same object that were split by faint or overlapped ink. */
function mergeBoxes(boxes, gapOf, canMerge) {
  const list = boxes.map(b => ({ ...b }));
  let changed = true;
  while (changed) {
    changed = false;
    outer: for (let i = 0; i < list.length; i++) for (let j = i + 1; j < list.length; j++) {
      const a = list[i], b = list[j], g = gapOf(a, b);
      const grown = { x: a.x - g, y: a.y - g, w: a.w + 2 * g, h: a.h + 2 * g };
      if (!overlapArea(grown, b) || !canMerge(a, b)) continue;
      const merged = unionBox(a, b);
      list[i] = { ...merged, count: a.count + b.count, mean: (a.mean * a.count + b.mean * b.count) / (a.count + b.count) };
      list.splice(j, 1); changed = true; break outer;
    }
  }
  return list;
}

/** Post-processing constants. Chosen on validation sets only (see VALIDATION.md). */
export const TUNING = Object.freeze({
  stampMinMean: 0.62, stampMinCount: 70, stampMerge: 1.45,
  signRx: 7, signRy: 3, signMinMean: 0.58, signMinCount: 40, signHandRatio: 0.55, signPrintRatio: 0.35,
  signInsideStamp: 0.8, signStampProb: 0.45, stampMinGlyphs: 8
});

/** Letter-like pieces inside a stamp candidate. A seal carries lettering (many
 * small separate marks); a large loop of a signature consists of a few long
 * strokes. Counted at working resolution on ink the network calls stamp ink. */
export function glyphCount(work, maps, ink, box, threshold) {
  const x0 = box.x * 2, y0 = box.y * 2, w = Math.min(work.width - x0, box.w * 2), h = Math.min(work.height - y0, box.h * 2);
  const mask = new Uint8Array(w * h), plane = maps.planes[STAMP];
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const wx = x0 + x, wy = y0 + y;
    if (ink.prepared.mask[wy * work.width + wx] && plane[(wy >> 1) * maps.width + (wx >> 1)] >= threshold) mask[y * w + x] = 1;
  }
  const limit = 0.3 * Math.max(w, h);
  return components(mask, w, h).boxes.filter(c => c.area >= 4 && Math.max(c.w, c.h) >= 3 && Math.max(c.w, c.h) <= limit).length;
}

/** Mean probability of class `cls` over pixels of `box` where class `seedCls` is confident. */
function meanOver(maps, ink, box, seedCls, cls, threshold) {
  const seed = maps.planes[seedCls], plane = maps.planes[cls]; let n = 0, sum = 0;
  for (let y = box.y; y < box.y + box.h; y++) for (let x = box.x; x < box.x + box.w; x++) {
    const i = y * maps.width + x; if (ink.grid[i] && seed[i] >= threshold) { n++; sum += plane[i]; }
  }
  return n ? sum / n / 255 : 0;
}

/** Find stamps and signatures. Coordinates are returned in working pixels. */
export function detectObjects(work, maps, options = {}) {
  const t = { ...TUNING, ...(options.tuning || {}) };
  const sensitivity = clamp(Number(options.sensitivity ?? 0.5), 0.2, 0.85);
  const strong = Math.round(255 * sensitivity), weak = Math.round(255 * sensitivity * 0.7);
  const ink = options.ink || inkGrid(work, options);
  const scale = Math.max(maps.width, maps.height) / 800; // proposals tuned on ~800 px grids

  let stamps = proposals(maps, ink, STAMP, { threshold: strong, rx: Math.round(3 * scale) + 1, ry: Math.round(3 * scale) + 1,
    minArea: Math.round(45 * scale * scale), minW: Math.round(12 * scale), minH: Math.round(8 * scale) });
  stamps = mergeBoxes(stamps, (a, b) => Math.round(Math.max(a.w, a.h, b.w, b.h) * 0.08) + 2, (a, b) => {
    const m = unionBox(a, b);
    return Math.max(m.w, m.h) <= Math.max(a.w, a.h, b.w, b.h) * t.stampMerge && m.w / m.h < 4.5 && m.h / m.w < 4.5;
  }).filter(b => b.mean >= t.stampMinMean && b.count >= t.stampMinCount * scale * scale);
  // Stamp-like ink without lettering is treated as a signature (see glyphCount).
  const loops = [];
  stamps = stamps.filter(b => {
    if (glyphCount(work, maps, ink, b, weak) >= t.stampMinGlyphs) return true;
    loops.push({ ...b, mean: b.mean * 0.8, anyObjectInk: true }); return false;
  });

  let signatures = proposals(maps, ink, SIGN, { threshold: strong, rx: Math.round(t.signRx * scale) + 1, ry: Math.round(t.signRy * scale) + 1,
    minArea: Math.round(28 * scale * scale), minW: Math.round(12 * scale), minH: Math.round(5 * scale) });
  signatures = mergeBoxes(signatures, (a, b) => Math.round(Math.max(a.h, b.h) * 0.35) + 2, (a, b) => {
    const m = unionBox(a, b);
    return m.h <= Math.max(a.h, b.h) * 1.7 && m.w <= (a.w + b.w) * 1.6;
  }).filter(box => {
    // Reject groups where the network sees mostly handwritten text or print.
    const sig = classMass(maps, ink, box, SIGN, weak), hand = classMass(maps, ink, box, HAND, strong), printed = classMass(maps, ink, box, PRINT, strong);
    if (!(box.mean >= t.signMinMean && sig >= t.signHandRatio * (sig + hand) && sig >= t.signPrintRatio * (sig + printed) &&
      box.count >= t.signMinCount * scale * scale)) return false;
    // Curved lettering of a seal can look cursive: drop small "signatures" that lie
    // inside a detected stamp and whose strokes the network also calls stamp ink.
    const inside = Math.max(0, ...stamps.map(st => overlapArea(st, box))) / (box.w * box.h);
    return !(inside >= t.signInsideStamp && meanOver(maps, ink, box, SIGN, STAMP, strong) >= t.signStampProb);
  });
  // A loop group replaces signature boxes it overlaps (they are parts of the same strokes).
  for (const loop of loops) {
    signatures = signatures.filter(sig => overlapArea(sig, loop) < 0.5 * sig.w * sig.h);
    signatures.push(loop);
  }

  let handwriting = [];
  if (options.includeHandwriting) {
    handwriting = proposals(maps, ink, HAND, { threshold: strong, rx: Math.round(8 * scale) + 1, ry: Math.round(2 * scale) + 1,
      minArea: Math.round(30 * scale * scale), minW: Math.round(16 * scale), minH: Math.round(5 * scale) })
      .filter(b => b.mean >= 0.6);
  }

  const regions = [];
  const push = (box, kind, plane) => {
    const pad = kind === 'stamp' ? Math.max(3, Math.round(Math.max(box.w, box.h) * 0.05)) : Math.max(3, Math.round(box.h * 0.12));
    const x = clamp(box.x - pad, 0, maps.width - 1), y = clamp(box.y - pad, 0, maps.height - 1);
    const right = clamp(box.x + box.w + pad, 1, maps.width), bottom = clamp(box.y + box.h + pad, 1, maps.height);
    const mode = dominantInk(work, maps, maps.planes[plane], box, strong);
    regions.push({ x: x * 2, y: y * 2, w: Math.min(work.width, right * 2) - x * 2, h: Math.min(work.height, bottom * 2) - y * 2,
      kind, mode, confidence: Math.round(box.mean * 100) / 100, ...(box.anyObjectInk ? { anyObjectInk: true } : {}) });
  };
  for (const box of stamps) push(box, 'stamp', STAMP);
  for (const box of signatures) push(box, 'signature', box.anyObjectInk ? STAMP : SIGN);
  for (const box of handwriting) push(box, 'handwriting', HAND);

  const filtered = regions.filter(r => !options.mode || options.mode === 'all' || options.mode === r.mode ||
    (options.mode === 'colored' && r.mode !== 'black'));
  filtered.sort((a, b) => a.y - b.y || a.x - b.x);
  filtered.forEach((region, index) => { region.key = 'object-' + index; });
  return { regions: filtered.slice(0, 80), truncated: filtered.length > 80,
    stats: { stamps: stamps.length, signatures: signatures.length, handwriting: handwriting.length } };
}
