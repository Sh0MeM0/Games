/** Local ink segmentation with the bundled ONNX model.
 *
 * The network was trained only on synthetic documents (see model/README.md).
 * It returns five independent probabilities per pixel at half of the working
 * resolution: printed text, form line, handwritten text, signature, stamp.
 * Several classes can be high at once where inks overlap.
 */
export const CLASSES = Object.freeze(['print', 'line', 'hand', 'signature', 'stamp']);
export const CLASS_INDEX = Object.freeze(Object.fromEntries(CLASSES.map((name, i) => [name, i])));
export const WORK_SIDE = 1600;
export const MIN_SIDE = 64;

/** Scale that brings the long side to WORK_SIDE; small screenshots are not enlarged. */
export function workingScale(width, height) {
  return Math.min(1, WORK_SIDE / Math.max(width, height));
}

/** Area-average resampling of RGBA pixels (used to build the working image). */
export function resizeImage(image, width, height) {
  if (width === image.width && height === image.height) return image;
  const out = new Uint8ClampedArray(width * height * 4);
  const sx = image.width / width, sy = image.height / height;
  for (let y = 0; y < height; y++) {
    const y0 = y * sy, y1 = Math.min(image.height, (y + 1) * sy);
    for (let x = 0; x < width; x++) {
      const x0 = x * sx, x1 = Math.min(image.width, (x + 1) * sx);
      let r = 0, g = 0, b = 0, a = 0, total = 0;
      for (let yy = Math.floor(y0); yy < Math.ceil(y1); yy++) {
        const wy = Math.min(yy + 1, y1) - Math.max(yy, y0);
        if (wy <= 0) continue;
        for (let xx = Math.floor(x0); xx < Math.ceil(x1); xx++) {
          const wx = Math.min(xx + 1, x1) - Math.max(xx, x0);
          if (wx <= 0) continue;
          const weight = wx * wy, p = (yy * image.width + xx) * 4;
          r += image.data[p] * weight; g += image.data[p + 1] * weight; b += image.data[p + 2] * weight; a += image.data[p + 3] * weight;
          total += weight;
        }
      }
      const q = (y * width + x) * 4;
      out[q] = r / total; out[q + 1] = g / total; out[q + 2] = b / total; out[q + 3] = a / total;
    }
  }
  return { width, height, data: out };
}

/** 97th percentile of each channel over every 7th pixel, as in model/train.py. */
export function paperColor(image) {
  const histograms = [new Uint32Array(256), new Uint32Array(256), new Uint32Array(256)];
  let count = 0;
  for (let i = 0; i < image.width * image.height; i += 7) {
    const p = i * 4, alpha = image.data[p + 3] / 255;
    for (let c = 0; c < 3; c++) histograms[c][Math.round(255 + (image.data[p + c] - 255) * alpha)]++;
    count++;
  }
  return histograms.map(histogram => {
    const target = count * 0.97; let sum = 0;
    for (let v = 0; v < 256; v++) { sum += histogram[v]; if (sum >= target) return Math.max(40, v); }
    return 255;
  });
}

/** Float32 CHW tensor data padded to a multiple of 16 with the paper colour. */
export function modelInput(image) {
  const width = Math.max(MIN_SIDE, Math.ceil(image.width / 16) * 16);
  const height = Math.max(MIN_SIDE, Math.ceil(image.height / 16) * 16);
  const paper = paperColor(image), plane = width * height, input = new Float32Array(plane * 3);
  for (let c = 0; c < 3; c++) input.fill(0.5, c * plane, (c + 1) * plane);
  for (let y = 0; y < image.height; y++) for (let x = 0; x < image.width; x++) {
    const p = (y * image.width + x) * 4, alpha = image.data[p + 3] / 255, i = y * width + x;
    for (let c = 0; c < 3; c++) {
      const value = 255 + (image.data[p + c] - 255) * alpha;
      input[c * plane + i] = Math.min(1.25, Math.max(0, value / paper[c])) - 0.5;
    }
  }
  return { input, width, height, paper };
}

/** Run the network and return Uint8 probability planes (0..255) at half resolution. */
export async function segmentImage(session, ort, image) {
  const { input, width, height } = modelInput(image);
  const tensor = new ort.Tensor('float32', input, [1, 3, height, width]);
  const output = await session.run({ [session.inputNames[0]]: tensor });
  const logits = output[session.outputNames[0]];
  const [, channels, h2, w2] = logits.dims;
  if (channels !== CLASSES.length) throw new Error('Модель вернула неожиданный результат.');
  const mapWidth = Math.ceil(image.width / 2), mapHeight = Math.ceil(image.height / 2);
  const planes = CLASSES.map(() => new Uint8Array(mapWidth * mapHeight));
  const data = logits.data;
  for (let c = 0; c < channels; c++) for (let y = 0; y < mapHeight; y++) for (let x = 0; x < mapWidth; x++) {
    const value = data[(c * h2 + y) * w2 + x];
    planes[c][y * mapWidth + x] = Math.round(255 / (1 + Math.exp(-value)));
  }
  tensor.dispose?.(); logits.dispose?.();
  return { width: mapWidth, height: mapHeight, planes };
}

/** Bilinear probability (0..1) of class `c` at working-image coordinates. */
export function sampleMap(maps, c, x, y) {
  const plane = maps.planes[c], w = maps.width, h = maps.height;
  const fx = Math.min(w - 1, Math.max(0, x / 2 - 0.25)), fy = Math.min(h - 1, Math.max(0, y / 2 - 0.25));
  const x0 = Math.floor(fx), y0 = Math.floor(fy), x1 = Math.min(w - 1, x0 + 1), y1 = Math.min(h - 1, y0 + 1);
  const ax = fx - x0, ay = fy - y0;
  const top = plane[y0 * w + x0] * (1 - ax) + plane[y0 * w + x1] * ax;
  const bottom = plane[y1 * w + x0] * (1 - ax) + plane[y1 * w + x1] * ax;
  return (top * (1 - ay) + bottom * ay) / 255;
}

/** Object classes (handwriting, signature, stamp) are also evaluated at half
 * scale, which helps with very large objects such as close-up photographs.
 * Printed text and form lines are taken from the full-scale pass only. */
export async function segmentMultiScale(session, ort, image, { minSide = 1000 } = {}) {
  const full = await segmentImage(session, ort, image);
  if (Math.max(image.width, image.height) < minSide) return full;
  const half = resizeImage(image, Math.max(1, Math.round(image.width / 2)), Math.max(1, Math.round(image.height / 2)));
  const coarse = await segmentImage(session, ort, half);
  for (const c of [CLASS_INDEX.hand, CLASS_INDEX.signature, CLASS_INDEX.stamp]) {
    const plane = full.planes[c];
    for (let y = 0; y < full.height; y++) for (let x = 0; x < full.width; x++) {
      // Cell (x, y) of the full-scale map is centred on pixel x, y of the half-size image.
      const value = Math.round(255 * sampleMap(coarse, c, x, y));
      const i = y * full.width + x; if (value > plane[i]) plane[i] = value;
    }
  }
  return full;
}
