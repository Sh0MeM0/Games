// Deterministic pixel processing: no recognition model and no invented strokes.
export const MAX_PIXELS = 24_000_000;
export const MAX_OUTPUT_PIXELS = 32_000_000;
const clamp = (n, lo, hi) => Math.max(lo, Math.min(hi, n));

function validateImage(image) {
  const { width, height, data } = image;
  if (!Number.isInteger(width) || !Number.isInteger(height) || width < 1 || height < 1 ||
      width * height > MAX_PIXELS || !data || data.length !== width * height * 4) {
    throw new Error('Некорректное изображение или превышен предел 24 мегапикселя.');
  }
}

export function normalizeRect(rect, width, height) {
  if (![rect?.x, rect?.y, rect?.w, rect?.h].every(Number.isFinite) || rect.w <= 0 || rect.h <= 0) {
    throw new Error('Область должна иметь положительные размеры.');
  }
  const x = clamp(Math.floor(rect.x), 0, width);
  const y = clamp(Math.floor(rect.y), 0, height);
  const right = clamp(Math.ceil(rect.x + rect.w), 0, width);
  const bottom = clamp(Math.ceil(rect.y + rect.h), 0, height);
  if (right <= x || bottom <= y) throw new Error('Область находится за пределами изображения.');
  return { x, y, w: right - x, h: bottom - y };
}

export function cropImage(image, rect) {
  validateImage(image);
  const box = normalizeRect(rect, image.width, image.height);
  const data = new Uint8ClampedArray(box.w * box.h * 4);
  for (let y = 0; y < box.h; y++) {
    const offset = ((y + box.y) * image.width + box.x) * 4;
    data.set(image.data.subarray(offset, offset + box.w * 4), y * box.w * 4);
  }
  return { width: box.w, height: box.h, data };
}

// Sliding maximum in O(width*height), independent of brush/grouping radius.
export function maxFilter(input, width, height, rx, ry = rx) {
  rx = Math.max(0, Math.round(rx)); ry = Math.max(0, Math.round(ry));
  const horizontal = new Uint8Array(input.length);
  const output = new Uint8Array(input.length);
  const deque = new Int32Array(Math.max(width, height));
  function line(count, radius, read, write) {
    let head = 0, tail = 0, added = -1;
    for (let i = 0; i < count; i++) {
      const end = Math.min(count - 1, i + radius);
      while (added < end) {
        added++;
        while (tail > head && read(deque[tail - 1]) <= read(added)) tail--;
        deque[tail++] = added;
      }
      while (head < tail && deque[head] < i - radius) head++;
      write(i, read(deque[head]));
    }
  }
  for (let y = 0; y < height; y++) {
    const offset = y * width;
    line(width, rx, x => input[offset + x], (x, n) => { horizontal[offset + x] = n; });
  }
  for (let x = 0; x < width; x++) {
    line(height, ry, y => horizontal[y * width + x], (y, n) => { output[y * width + x] = n; });
  }
  return output;
}

function matchesMode(r, g, b, gray, mode) {
  const spread = Math.max(r, g, b) - Math.min(r, g, b);
  const blue = b > r + 12 && b > g + 4;
  const red = r > g + 24 && r > b + 16;
  const other = (r > g + 18 && b > g + 18) || (g > r + 24 && g > b + 16);
  if (mode === 'blue') return blue;
  if (mode === 'red') return red;
  if (mode === 'colored') return blue || red || other;
  if (mode === 'black') return gray < 90 || spread < 35;
  return true;
}

function smoothPaperInput(input, width, height) {
  const horizontal = new Uint16Array(input.length), output = new Uint8Array(input.length), radius = 2;
  for (let y = 0; y < height; y++) {
    const row = y * width; let sum = 0;
    for (let x = 0; x <= Math.min(radius, width-1); x++) sum += input[row+x];
    for (let x = 0; x < width; x++) {
      horizontal[row+x] = sum;
      if (x-radius >= 0) sum -= input[row+x-radius];
      if (x+radius+1 < width) sum += input[row+x+radius+1];
    }
  }
  for (let x = 0; x < width; x++) {
    const columns = Math.min(width-1,x+radius)-Math.max(0,x-radius)+1; let sum = 0;
    for (let y = 0; y <= Math.min(radius,height-1); y++) sum += horizontal[y*width+x];
    for (let y = 0; y < height; y++) {
      const rows = Math.min(height-1,y+radius)-Math.max(0,y-radius)+1;
      output[y*width+x] = Math.round(sum/(columns*rows));
      if(y-radius >= 0)sum-=horizontal[(y-radius)*width+x];
      if(y+radius+1 < height)sum+=horizontal[(y+radius+1)*width+x];
    }
  }
  return output;
}

function components(mask, width, height, visit) {
  const remaining = new Uint8Array(mask);
  const queue = new Int32Array(mask.length);
  for (let first = 0; first < remaining.length; first++) {
    if (!remaining[first]) continue;
    let head = 0, tail = 1;
    queue[0] = first; remaining[first] = 0;
    let left = first % width, right = left, top = Math.floor(first / width), bottom = top;
    while (head < tail) {
      const index = queue[head++], x = index % width, y = Math.floor(index / width);
      left = Math.min(left, x); right = Math.max(right, x);
      top = Math.min(top, y); bottom = Math.max(bottom, y);
      for (let dy = -1; dy <= 1; dy++) {
        const ny = y + dy;
        if (ny < 0 || ny >= height) continue;
        for (let dx = -1; dx <= 1; dx++) {
          const nx = x + dx, neighbor = ny * width + nx;
          if (nx >= 0 && nx < width && remaining[neighbor]) {
            remaining[neighbor] = 0; queue[tail++] = neighbor;
          }
        }
      }
    }
    visit({ x: left, y: top, w: right - left + 1, h: bottom - top + 1, area: tail }, queue, tail);
  }
}

export function removeSpeckles(mask, width, height, minimumArea) {
  if (minimumArea <= 1) return mask;
  components(mask, width, height, (_, pixels, count) => {
    if (count < minimumArea) for (let i = 0; i < count; i++) mask[pixels[i]] = 0;
  });
  return mask;
}

export function prepareInk(image, options = {}) {
  validateImage(image);
  const { width, height, data } = image, size = width * height;
  const mode = options.mode || 'all';
  const threshold = clamp(Number(options.threshold) || 18, 3, 90);
  const gray = new Uint8Array(size);
  const rgb = new Uint8ClampedArray(data.length);
  // Flatten transparent source pixels onto white before measuring the background.
  for (let i = 0; i < size; i++) {
    const p = i * 4, alpha = data[p + 3] / 255;
    rgb[p] = 255 + (data[p] - 255) * alpha;
    rgb[p + 1] = 255 + (data[p + 1] - 255) * alpha;
    rgb[p + 2] = 255 + (data[p + 2] - 255) * alpha; rgb[p + 3] = 255;
    gray[i] = (rgb[p] * 77 + rgb[p + 1] * 150 + rgb[p + 2] * 29) >> 8;
  }
  const radius = clamp(Math.round(Math.min(width, height) / 3), 16, 80);
  // Grayscale closing fills dark strokes, then erodes bright outliers back to
  // their original size. A maximum alone spreads JPEG ringing across the paper.
  // Smooth only the paper estimate; the source strokes stay at full resolution.
  const expanded = maxFilter(smoothPaperInput(gray, width, height), width, height, radius);
  for (let i = 0; i < size; i++) expanded[i] = 255 - expanded[i];
  const paper = maxFilter(expanded, width, height, radius);
  for (let i = 0; i < size; i++) paper[i] = 255 - paper[i];
  const mask = new Uint8Array(size), available = new Uint8Array(size);
  const contrast = clamp(Number(options.contrast) || 1.2, 1, 2.5);
  for (let i = 0; i < size; i++) {
    const p = i * 4, r = rgb[p], g = rgb[p + 1], b = rgb[p + 2];
    const spread = Math.max(r, g, b) - Math.min(r, g, b);
    const colored = matchesMode(r, g, b, gray[i], 'colored');
    const strength = Math.max(paper[i] - gray[i], colored ? spread * 0.45 : 0, (70 - gray[i]) * 2);
    const matches = matchesMode(r, g, b, gray[i], mode);
    available[i] = strength > 3 && matches ? 1 : 0;
    mask[i] = strength >= threshold && matches ? 1 : 0;
    // The local paper estimate removes gradual lighting variation, not just pure white.
    const correction = 255 - Math.max(paper[i], 100);
    rgb[p] = clamp(255 - (255 - Math.min(255, r + correction)) * contrast, 0, 255);
    rgb[p + 1] = clamp(255 - (255 - Math.min(255, g + correction)) * contrast, 0, 255);
    rgb[p + 2] = clamp(255 - (255 - Math.min(255, b + correction)) * contrast, 0, 255);
  }
  removeSpeckles(mask, width, height, clamp(Math.round(options.speckle ?? 3), 0, 40));
  return { width, height, data: rgb, mask, available };
}

export function applyStrokes(mask, available, width, height, strokes = []) {
  function circle(cx, cy, radius, restore) {
    const left = Math.max(0, Math.floor(cx - radius)), right = Math.min(width - 1, Math.ceil(cx + radius));
    const top = Math.max(0, Math.floor(cy - radius)), bottom = Math.min(height - 1, Math.ceil(cy + radius));
    for (let y = top; y <= bottom; y++) for (let x = left; x <= right; x++) {
      if ((x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2) {
        const i = y * width + x; mask[i] = restore ? available[i] : 0;
      }
    }
  }
  for (const stroke of strokes) {
    if (!Array.isArray(stroke.points)) continue;
    const radius = clamp(Number(stroke.radius) || 5, 1, 100);
    const restore = stroke.tool === 'restore';
    let previous;
    for (const point of stroke.points) {
      if (!Number.isFinite(point.x) || !Number.isFinite(point.y)) continue;
      if (previous) {
        const distance = Math.hypot(point.x - previous.x, point.y - previous.y);
        const count = Math.max(1, Math.ceil(distance / Math.max(1, radius / 2)));
        for (let i = 0; i <= count; i++) {
          const t = i / count;
          circle(previous.x + (point.x - previous.x) * t, previous.y + (point.y - previous.y) * t, radius, restore);
        }
      } else circle(point.x, point.y, radius, restore);
      previous = point;
    }
  }
  return mask;
}

export function renderRegion(image, rect, options = {}, strokes = []) {
  const prepared = prepareInk(cropImage(image, rect), options);
  applyStrokes(prepared.mask, prepared.available, prepared.width, prepared.height, strokes);
  let count = 0;
  for (let i = 0; i < prepared.mask.length; i++) {
    if (!prepared.mask[i]) {
      const p = i * 4; prepared.data[p] = prepared.data[p + 1] = prepared.data[p + 2] = 255;
    } else count++;
  }
  return { width: prepared.width, height: prepared.height, data: prepared.data, inkPixels: count };
}

export function trimAndPad(image, padding = 16) {
  validateImage(image);
  const { width, height, data } = image;
  let left = width, right = -1, top = height, bottom = -1;
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    const p = (y * width + x) * 4;
    if (Math.min(data[p], data[p + 1], data[p + 2]) < 250) {
      left = Math.min(left, x); right = Math.max(right, x); top = Math.min(top, y); bottom = Math.max(bottom, y);
    }
  }
  if (right < left) throw new Error('После очистки область пуста. Уменьшите удаление фона или верните штрихи кистью.');
  padding = clamp(Math.round(padding), 0, 128);
  const w = right - left + 1 + padding * 2, h = bottom - top + 1 + padding * 2;
  if (w * h > MAX_OUTPUT_PIXELS) throw new Error('Результат слишком большой. Уменьшите область.');
  const result = new Uint8ClampedArray(w * h * 4); result.fill(255);
  for (let y = top; y <= bottom; y++) {
    const start = (y * width + left) * 4;
    result.set(data.subarray(start, start + (right - left + 1) * 4), ((y - top + padding) * w + padding) * 4);
  }
  return { width: w, height: h, data: result };
}

export function detectRegions(image, options = {}) {
  const prepared = prepareInk(image, { ...options, speckle: options.speckle ?? 3 });
  const gap = clamp(Number(options.gap) || 10, 1, 40);
  const grouped = maxFilter(prepared.mask, image.width, image.height, gap, Math.max(2, Math.round(gap * 0.55)));
  const regions = []; let truncated = false;
  components(grouped, image.width, image.height, box => {
    let left = image.width, right = -1, top = image.height, bottom = -1, count = 0;
    for (let y = box.y; y < box.y + box.h; y++) for (let x = box.x; x < box.x + box.w; x++) {
      if (!prepared.mask[y * image.width + x]) continue;
      left = Math.min(left, x); right = Math.max(right, x); top = Math.min(top, y); bottom = Math.max(bottom, y); count++;
    }
    const w = right - left + 1, h = bottom - top + 1;
    if (count < 15 || w < 12 || h < 5) return;
    if (count / (w * h) > 0.6 && w * h > image.width * image.height * 0.3) return;
    const margin = 8;
    if (regions.length < 2000) regions.push(normalizeRect({ x: left - margin, y: top - margin, w: w + margin * 2, h: h + margin * 2 }, image.width, image.height));
    else truncated = true;
  });
  // A ring and its disconnected internal lettering are normally one stamp.
  // Keep an enclosing frame instead of exporting every internal glyph separately.
  regions.sort((a,b) => b.w*b.h - a.w*a.h);
  const distinct = [];
  for (const box of regions) {
    const inside = distinct.some(outer => {
      const intersection = Math.max(0, Math.min(box.x+box.w,outer.x+outer.w)-Math.max(box.x,outer.x)) *
        Math.max(0, Math.min(box.y+box.h,outer.y+outer.h)-Math.max(box.y,outer.y));
      return intersection >= box.w*box.h*0.9;
    });
    if (inside) continue;
    if (distinct.length < 80) distinct.push(box); else truncated = true;
  }
  distinct.sort((a,b) => a.y-b.y || a.x-b.x);
  return { regions: distinct, truncated };
}
