/** Rectangular seals are recognised by four supported border lines, independently
 * of connected ink. A pen stroke crossing a frame therefore cannot change its
 * extent. This module never synthesises lettering or obscured strokes. */
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const median = a => a.length ? [...a].sort((x, y) => x - y)[Math.floor(a.length / 2)] : 0;

function bounds(points, width, height) {
  const left = clamp(Math.floor(Math.min(...points.map(p => p.x)) - 3), 0, width - 1);
  const top = clamp(Math.floor(Math.min(...points.map(p => p.y)) - 3), 0, height - 1);
  const right = clamp(Math.ceil(Math.max(...points.map(p => p.x)) + 4), left + 1, width);
  const bottom = clamp(Math.ceil(Math.max(...points.map(p => p.y)) + 4), top + 1, height);
  return { x: left, y: top, w: right - left, h: bottom - top };
}

/** A convex clockwise frame; positive padding includes the actual border width. */
export function pointInStampFrame(frame, x, y, padding = 2.5) {
  if (!frame.quad?.length) return x >= frame.x - padding && y >= frame.y - padding && x <= frame.x + frame.w + padding && y <= frame.y + frame.h + padding;
  for (let i = 0; i < 4; i++) {
    const a = frame.quad[i], b = frame.quad[(i + 1) % 4];
    const length = Math.hypot(b.x - a.x, b.y - a.y);
    if (((b.x - a.x) * (y - a.y) - (b.y - a.y) * (x - a.x)) / length < -padding) return false;
  }
  return true;
}

function intersection(a, b) {
  return Math.max(0, Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x)) * Math.max(0, Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y));
}

function peakPositions(histogram, minimum, separation, maximum = 16) {
  const candidates = [];
  for (let p = 2; p < histogram.length - 2; p++) {
    const count = histogram[p - 2] + histogram[p - 1] + histogram[p] + histogram[p + 1] + histogram[p + 2];
    if (count >= minimum) candidates.push({ p, count });
  }
  candidates.sort((a, b) => b.count - a.count);
  const result = [];
  for (const peak of candidates) {
    if (result.every(other => Math.abs(other.p - peak.p) >= separation)) result.push(peak);
    if (result.length === maximum) break;
  }
  return result;
}

function support(mask, width, height, a, b, radius = 2) {
  const length = Math.hypot(b.x - a.x, b.y - a.y), count = Math.ceil(length), nx = -(b.y - a.y) / length, ny = (b.x - a.x) / length;
  let hits = 0, longestGap = 0, gap = 0;
  for (let i = 0; i <= count; i++) {
    const x = a.x + (b.x - a.x) * i / count, y = a.y + (b.y - a.y) * i / count;
    let hit = false;
    for (let d = -radius; d <= radius; d++) {
      const xx = Math.round(x + nx * d), yy = Math.round(y + ny * d);
      if (xx >= 0 && yy >= 0 && xx < width && yy < height && mask[yy * width + xx]) { hit = true; break; }
    }
    if (hit) { hits++; gap = 0; } else { gap++; longestGap = Math.max(longestGap, gap); }
  }
  return { coverage: hits / (count + 1), longestGap: longestGap / length };
}

function horizontalRuns(projected, row, size, offset, minimumWidth) {
  const occupied = new Uint8Array(size);
  for (const p of projected) if (Math.abs(p.v - row) <= 2.5) occupied[Math.round(p.u) + offset] = 1;
  const runs = []; let start = -1, last = -1, hits = 0;
  const finish = () => { if (last - start >= minimumWidth && hits / (last - start + 1) > .85) runs.push({ left: start - offset, right: last - offset }); };
  for (let i = 0; i < size; i++) {
    if (occupied[i]) { if (start < 0) start = i; last = i; hits++; }
    else if (start >= 0 && i - last > 5) { finish(); start = -1; hits = 0; }
  }
  if (start >= 0) finish();
  return runs;
}

/** The input is a single-colour binary ink mask at document-analysis resolution.
 * A neutral rectangle alone cannot identify a seal rather than a table cell, so
 * neutral frames require an explicit caller override. Circular seals are left
 * to the independent circular detector. */
export function detectStampFrames(mask, width, height, mode = 'blue', options = {}) {
  if (!mask || mask.length !== width * height || width < 1 || height < 1) throw new Error('Некорректная маска печати.');
  if ((mode === 'black' && !options.allowNeutral) || mode === 'all') return [];
  const points = [];
  for (let i = 0; i < mask.length; i++) if (mask[i]) points.push({ x: i % width, y: Math.floor(i / width) });
  if (points.length < 180) return [];
  const diagonal = Math.ceil(Math.hypot(width, height)), offset = diagonal, size = diagonal * 2 + 5;
  const minimumWidth = Math.max(70, Math.min(width * .10, 120));
  const minimumHeight = Math.max(30, Math.min(height * .045, 50));
  const candidates = [], angleLimit = options.angleLimit ?? 20;
  for (let degrees = -angleLimit; degrees <= angleLimit; degrees++) {
    const angle = degrees * Math.PI / 180, cosine = Math.cos(angle), sine = Math.sin(angle);
    const horizontal = new Uint32Array(size), projected = new Array(points.length);
    for (let i = 0; i < points.length; i++) {
      const p = points[i], u = p.x * cosine + p.y * sine, v = -p.x * sine + p.y * cosine;
      projected[i] = { u, v }; horizontal[Math.round(v) + offset]++;
    }
    // Two long horizontal edges must exist. Text baselines may form projection
    // peaks, but they cannot pass the independent four-edge coverage test below.
    const rows = peakPositions(horizontal, minimumWidth * .85, 8, 14)
      .map(p => ({ ...p, p: p.p - offset, runs: horizontalRuns(projected, p.p - offset, size, offset, minimumWidth) }))
      .filter(p => p.runs.length);
    for (let first = 0; first < rows.length; first++) for (let second = first + 1; second < rows.length; second++) {
      const top = Math.min(rows[first].p, rows[second].p), bottom = Math.max(rows[first].p, rows[second].p), frameHeight = bottom - top;
      if (frameHeight < minimumHeight || frameHeight > height * .68) continue;
      if (!rows[first].runs.some(a => rows[second].runs.some(b => Math.min(a.right, b.right) - Math.max(a.left, b.left) >= minimumWidth))) continue;
      const vertical = new Uint32Array(size);
      for (const p of projected) if (p.v >= top - 2 && p.v <= bottom + 2) vertical[Math.round(p.u) + offset]++;
      const columns = peakPositions(vertical, frameHeight * .85, 8, 12).map(p => ({ ...p, p: p.p - offset }));
      for (let leftIndex = 0; leftIndex < columns.length; leftIndex++) for (let rightIndex = leftIndex + 1; rightIndex < columns.length; rightIndex++) {
        const left = Math.min(columns[leftIndex].p, columns[rightIndex].p), right = Math.max(columns[leftIndex].p, columns[rightIndex].p), frameWidth = right - left;
        if (frameWidth < minimumWidth || frameWidth > width * .94 || frameWidth / frameHeight < 1.15 || frameWidth / frameHeight > 7.5) continue;
        const unproject = (u, v) => ({ x: u * cosine - v * sine, y: u * sine + v * cosine });
        const quad = [unproject(left, top), unproject(right, top), unproject(right, bottom), unproject(left, bottom)];
        if (quad.some(p => p.x < -2 || p.y < -2 || p.x >= width + 2 || p.y >= height + 2)) continue;
        const edges = quad.map((p, i) => support(mask, width, height, p, quad[(i + 1) % 4]));
        const coverage = edges.map(e => e.coverage), average = coverage.reduce((a, b) => a + b) / 4;
        if (Math.min(...coverage) < .68 || average < .86 || edges.some(e => e.longestGap > .22)) continue;
        let insideInk = 0;
        for (const p of projected) if (p.u > left + 6 && p.u < right - 6 && p.v > top + 6 && p.v < bottom - 6) insideInk++;
        // An isolated rectangular ornament or blank form cell is not a seal.
        if (insideInk < Math.max(60, frameWidth * frameHeight * .008)) continue;
        candidates.push({ ...bounds(quad, width, height), quad, shape: 'quadrilateral', kind: 'stamp', mode, angle: degrees,
          confidence: Math.min(.99, .72 + average * .27), borderCoverage: coverage, insideInk, score: average + Math.min(...coverage) * .25 });
      }
    }
  }
  candidates.sort((a, b) => b.score - a.score || b.w * b.h - a.w * a.h);
  const result = [];
  for (const frame of candidates) {
    if (result.some(other => intersection(frame, other) / Math.min(frame.w * frame.h, other.w * other.h) > .65)) continue;
    const { score, ...publicFrame } = frame; result.push(publicFrame);
    if (result.length >= 24) break;
  }
  return result.sort((a, b) => a.y - b.y || a.x - b.x);
}

function inkVector(data, index) {
  const p = index * 4, r = 255 - data[p], g = 255 - data[p + 1], b = 255 - data[p + 2], length = Math.hypot(r, g, b);
  return length > 50 ? [r / length, g / length, b / length, length] : null;
}
const distance = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2]);

/** Crop to the actual frame and, only when its ink colour is consistent, suppress
 * clearly different pen ink. Use ORIGINAL image RGB, not contrast-clipped RGB.
 * Same-colour intersecting ink remains ambiguous and is deliberately retained.
 * The returned mask is a new array; source pixels and input mask stay untouched. */
export function extractStampMask(image, mask, frame, options = {}) {
  const { width, height, data } = image, originX = options.originX || 0, originY = options.originY || 0;
  if (mask.length !== width * height || data.length !== width * height * 4) throw new Error('Некорректное изображение печати.');
  const output = new Uint8Array(mask), samples = [];
  let clippedPixels = 0, removedForeignPixels = 0;
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    const i = y * width + x;
    if (!output[i]) continue;
    const gx = x + originX, gy = y + originY;
    if (!pointInStampFrame(frame, gx, gy)) { output[i] = 0; clippedPixels++; continue; }
    if (pointInStampFrame(frame, gx, gy, -4)) continue;
    const vector = inkVector(data, i); if (vector) samples.push(vector);
  }
  if (options.removeForeignInk === false || samples.length < 60 || !frame.quad) return { mask: output, clippedPixels, removedForeignPixels, colourSeparated: false };
  const prototype = [0, 1, 2].map(channel => median(samples.map(v => v[channel]))), norm = Math.hypot(...prototype);
  for (let i = 0; i < 3; i++) prototype[i] /= norm;
  const deviations = samples.map(v => distance(v, prototype)).sort((a, b) => a - b);
  const spread = deviations[Math.floor(deviations.length * .80)];
  // Chroma-subsampled JPEG and uneven shadows can change thin lettering more
  // than the broad frame. Such a border is insufficient evidence for deleting
  // an interior colour: retain its ink for the geometric layer separator.
  if (spread > .035) return { mask: output, clippedPixels, removedForeignPixels, colourSeparated: false, colourAmbiguous: true };
  const centre = median(deviations), deviation = median(deviations.map(value => Math.abs(value - centre)));
  // Stable scanner ink needs a tighter envelope than a fixed broad blue range.
  // Median absolute deviation tolerates occasional pen crossings on the border
  // without teaching the model that the crossing pen belongs to the seal.
  // One-channel quantisation is proportionally larger in faint antialiased ink.
  const quantisationAllowance = .018 + 1.4 / median(samples.map(vector => vector[3]));
  const tolerance = clamp(Math.max(centre + 3 * deviation + .01, quantisationAllowance), .02, .095);
  for (let i = 0; i < output.length; i++) if (output[i]) {
    const vector = inkVector(data, i);
    if (vector && distance(vector, prototype) > tolerance) { output[i] = 0; removedForeignPixels++; }
  }
  return { mask: output, clippedPixels, removedForeignPixels, colourSeparated: removedForeignPixels > 0, colourTolerance: tolerance };
}
