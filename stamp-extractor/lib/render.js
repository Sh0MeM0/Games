/** Render one object from source pixels using the segmentation maps.
 *
 * Output pixels are original (paper-corrected) source pixels. The only added
 * pixels are optional, explicitly reported short-gap repairs and recoloured
 * overlap pixels; both are counted as `inferredPixels`.
 */
import { prepareInk, cropImage, normalizeRect, applyStrokes } from './processing.js';
import { CLASS_INDEX, CLASSES } from './segmentation.js';
import { repairStrokes } from './repair.js';

const { print: PRINT, line: LINE, hand: HAND, signature: SIGN, stamp: STAMP } = CLASS_INDEX;
const clamp = (n, lo, hi) => Math.max(lo, Math.min(hi, n));
const OBJECT_CLASSES = [HAND, SIGN, STAMP];

export function targetClasses(options) {
  // Objects re-labelled by geometry keep every object-ink class the network assigned.
  if (options.anyObjectInk && options.kind !== 'candidate') return [STAMP, SIGN, HAND];
  if (options.kind === 'stamp') return options.stampLayer === 'whole' ? [STAMP, SIGN, HAND] : [STAMP];
  if (options.kind === 'signature') return [SIGN];
  if (options.kind === 'handwriting') return [HAND];
  return null; // manual area: keep everything except removed printed text and lines
}

/** Per-pixel class probabilities (0..255) for a crop, bilinear from the maps. */
export function cropProbabilities(maps, scale, rect) {
  const { x: rx, y: ry, w, h } = rect, planes = CLASSES.map(() => new Uint8Array(w * h));
  const mw = maps.width, mh = maps.height;
  const fxs = new Float32Array(w), fys = new Float32Array(h);
  for (let x = 0; x < w; x++) fxs[x] = clamp(((rx + x + 0.5) * scale - 0.5) / 2 - 0.25, 0, mw - 1);
  for (let y = 0; y < h; y++) fys[y] = clamp(((ry + y + 0.5) * scale - 0.5) / 2 - 0.25, 0, mh - 1);
  for (let c = 0; c < CLASSES.length; c++) {
    const plane = maps.planes[c], out = planes[c];
    for (let y = 0; y < h; y++) {
      const fy = fys[y], y0 = Math.floor(fy), y1 = Math.min(mh - 1, y0 + 1), ay = fy - y0;
      for (let x = 0; x < w; x++) {
        const fx = fxs[x], x0 = Math.floor(fx), x1 = Math.min(mw - 1, x0 + 1), ax = fx - x0;
        const top = plane[y0 * mw + x0] * (1 - ax) + plane[y0 * mw + x1] * ax;
        const bottom = plane[y1 * mw + x0] * (1 - ax) + plane[y1 * mw + x1] * ax;
        out[y * w + x] = top * (1 - ay) + bottom * ay;
      }
    }
  }
  return planes;
}

/** Keep weak pixels only when they are 8-connected to strong pixels. */
function hysteresis(strong, weak, width, height) {
  const keep = new Uint8Array(strong.length), queue = new Int32Array(strong.length);
  let tail = 0;
  for (let i = 0; i < strong.length; i++) if (strong[i]) { keep[i] = 1; queue[tail++] = i; }
  for (let head = 0; head < tail; head++) {
    const i = queue[head], x = i % width, y = (i / width) | 0;
    for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
      const nx = x + dx, ny = y + dy; if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
      const n = ny * width + nx; if (!keep[n] && weak[n]) { keep[n] = 1; queue[tail++] = n; }
    }
  }
  return keep;
}

function medianColor(data, pixels) {
  if (!pixels.length) return null;
  const channel = c => { const v = pixels.map(i => data[i * 4 + c]).sort((a, b) => a - b); return v[v.length >> 1]; };
  return [channel(0), channel(1), channel(2)];
}

/**
 * @param source full-resolution RGBA image
 * @param rect   object rectangle in source pixels
 * @param context {maps, scale} where working = source × scale and maps are at working/2
 */
export function renderObject(source, rect, options = {}, strokes = [], context = null) {
  const region = normalizeRect(rect, source.width, source.height), crop = cropImage(source, region);
  const prepared = prepareInk(crop, options);
  const { width, height } = prepared, size = width * height;
  const sourceMask = new Uint8Array(prepared.mask), removedMask = new Uint8Array(size), ruleMask = new Uint8Array(size);
  let overlapPixels = 0, recolored = 0;
  const recoloredMask = new Uint8Array(size);
  if (context?.maps) {
    const probs = cropProbabilities(context.maps, context.scale, region);
    const s = Math.round(255 * clamp(Number(options.sensitivity ?? 0.5), 0.2, 0.85)), weakLevel = Math.round(s * 0.5);
    const targets = targetClasses(options), isolate = options.isolate !== false;
    const removeText = options.removeText !== false, removeLines = options.removeLines !== false;
    const strong = new Uint8Array(size), weak = new Uint8Array(size), foreign = new Uint8Array(size);
    for (let i = 0; i < size; i++) {
      if (!prepared.mask[i]) continue;
      const printed = probs[PRINT][i], line = probs[LINE][i];
      let own = 0, other = 0;
      if (targets) {
        for (const c of targets) own = Math.max(own, probs[c][i]);
        for (const c of OBJECT_CLASSES) if (!targets.includes(c)) other = Math.max(other, probs[c][i]);
      }
      if (line >= s && removeLines) ruleMask[i] = 1;
      if (!targets) { // manual area
        const objectLike = Math.max(probs[HAND][i], probs[SIGN][i], probs[STAMP][i]);
        if ((removeText && printed >= s && printed >= objectLike) || (removeLines && line >= s && line >= objectLike)) {
          prepared.mask[i] = 0; removedMask[i] = 1;
        }
        continue;
      }
      if (own >= s) strong[i] = 1; else if (own >= weakLevel) weak[i] = 1;
      const foreignInk = (removeText && printed >= s) || (removeLines && line >= s) || (isolate && other >= s);
      if (foreignInk) foreign[i] = 1;
    }
    if (targets) {
      const keep = hysteresis(strong, weak, width, height);
      // Ink colour of the object from confident pixels that no other class claims.
      const pure = [];
      for (let i = 0; i < size; i++) if (strong[i] && !foreign[i]) pure.push(i);
      const ink = medianColor(prepared.data, pure.length > 30 ? pure : Array.from(strong.keys()).filter(i => strong[i]));
      for (let i = 0; i < size; i++) {
        if (!prepared.mask[i]) continue;
        if (!keep[i]) {
          // Unclassified ink is kept only when the user disabled isolation.
          const printed = probs[PRINT][i] >= s && removeText, line = probs[LINE][i] >= s && removeLines;
          if (isolate || printed || line) { prepared.mask[i] = 0; removedMask[i] = 1; }
          continue;
        }
        if (!foreign[i] || !ink) continue;
        overlapPixels++;
        const p = i * 4, distance = Math.hypot(prepared.data[p] - ink[0], prepared.data[p + 1] - ink[1], prepared.data[p + 2] - ink[2]);
        if (distance <= 70) continue; // same-coloured overlap: genuinely ambiguous, keep the source pixel
        if (options.repair === true) { // foreign ink dominates: show the object's own ink there, disclosed as inferred
          prepared.data[p] = ink[0]; prepared.data[p + 1] = ink[1]; prepared.data[p + 2] = ink[2];
          recoloredMask[i] = 1; recolored++;
        } else { prepared.mask[i] = 0; removedMask[i] = 1; }
      }
    }
  }
  const repaired = repairStrokes({ mask: prepared.mask, data: prepared.data, sourceMask, width, height, removedMask, ruleMask },
    { enabled: options.repair === true, maxGap: Number(options.repairGap) || 12 });
  prepared.mask = repaired.mask; prepared.data = repaired.data || prepared.data;
  applyStrokes(prepared.mask, prepared.available, width, height, strokes);
  let inkPixels = 0, inferredPixels = 0;
  const inferredMask = repaired.inferredMask;
  for (let i = 0; i < size; i++) {
    if (recoloredMask[i]) inferredMask[i] = 1;
    if (!prepared.mask[i]) { const p = i * 4; prepared.data[p] = prepared.data[p + 1] = prepared.data[p + 2] = 255; inferredMask[i] = 0; }
    else { inkPixels++; if (inferredMask[i]) inferredPixels++; }
  }
  return { width, height, data: prepared.data, inkPixels, inferredPixels, inferredMask, uncertainPixels: overlapPixels, recolored };
}
