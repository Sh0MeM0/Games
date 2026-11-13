// Image processing worker: local model inference, detection, preview and export.
import * as ort from '../vendor/ort/ort.wasm.min.mjs';
import { cropImage, trimAndPad, MAX_OUTPUT_PIXELS } from '../lib/processing.js';
import { workingScale, segmentImage } from '../lib/segmentation.js';
import { workingImage, regionsToSource } from '../lib/pipeline.js';
import { detectObjects } from '../lib/objects.js';
import { renderObject } from '../lib/render.js';

ort.env.wasm.wasmPaths = new URL('../vendor/ort/', import.meta.url).href;
ort.env.wasm.numThreads = self.crossOriginIsolated ? Math.min(4, navigator.hardwareConcurrency || 1) : 1;
ort.env.wasm.proxy = false;
ort.env.logLevel = 'error';

let source = null, sourceKey = null, work = null, scale = 1, maps = null, session = null, sessionPromise = null;
const mapCache = new Map(); // page key → maps (a few MB each)

function model() {
  sessionPromise ||= (async () => {
    const response = await fetch(new URL('../models/ink-seg.onnx', import.meta.url));
    if (!response.ok) throw new Error('Не найдена встроенная модель. Распакуйте архив программы целиком.');
    session = await ort.InferenceSession.create(new Uint8Array(await response.arrayBuffer()),
      { executionProviders: ['wasm'], graphOptimizationLevel: 'all' });
    return session;
  })().catch(error => { sessionPromise = null; throw error; });
  return sessionPromise;
}

function canvasOf(image) {
  const canvas = new OffscreenCanvas(image.width, image.height);
  canvas.getContext('2d').putImageData(new ImageData(image.data, image.width, image.height), 0, 0);
  return canvas;
}
function fitImage(image, maxSide) {
  if (Math.max(image.width, image.height) <= maxSide) return image;
  const factor = maxSide / Math.max(image.width, image.height);
  const width = Math.max(1, Math.round(image.width * factor)), height = Math.max(1, Math.round(image.height * factor));
  const original = canvasOf(image), canvas = new OffscreenCanvas(width, height);
  const context = canvas.getContext('2d', { willReadFrequently: true });
  context.imageSmoothingQuality = 'high'; context.drawImage(original, 0, 0, width, height); original.width = 1;
  const data = context.getImageData(0, 0, width, height).data; canvas.width = 1;
  return { width, height, data };
}
function context() { return maps ? { maps, scale } : null; }
function highlight(rendered) {
  const mask = rendered.inferredMask; if (!mask) return;
  for (let y = 0; y < rendered.height; y++) for (let x = 0; x < rendered.width; x++) if (mask[y * rendered.width + x]) {
    for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
      const xx = x + dx, yy = y + dy;
      if (xx < 0 || xx >= rendered.width || yy < 0 || yy >= rendered.height) continue;
      const p = (yy * rendered.width + xx) * 4; rendered.data[p] = 245; rendered.data[p + 1] = 110; rendered.data[p + 2] = 20;
    }
  }
}

/** Runs the model for the current page unless its maps are cached. Returns model time in ms. */
async function ensureMaps() {
  if (!work) ({ work, scale } = workingImage(source));
  if (maps) return 0;
  const s = await model(), t = performance.now();
  maps = await segmentImage(s, ort, work);
  if (sourceKey) { mapCache.set(sourceKey, maps); while (mapCache.size > 60) mapCache.delete(mapCache.keys().next().value); }
  return Math.round(performance.now() - t);
}

const handlers = {
  async warmup() { await model(); return {}; },
  async init(payload) {
    source = { width: payload.width, height: payload.height, data: new Uint8ClampedArray(payload.buffer) };
    sourceKey = payload.key || null;
    scale = workingScale(source.width, source.height);
    work = null; maps = sourceKey && mapCache.get(sourceKey) || null;
    const cached = !!maps;
    if (payload.ensureMaps) await ensureMaps();
    return { cached };
  },
  async analyze(payload) {
    if (!source) throw new Error('Сначала откройте изображение.');
    const started = performance.now(), modelMs = await ensureMaps();
    const found = detectObjects(work, maps, payload);
    const regions = regionsToSource(found.regions, scale, source);
    return { regions, truncated: found.truncated, stats: found.stats, modelMs, totalMs: Math.round(performance.now() - started) };
  },
  async previewOriginal(payload) {
    const preview = fitImage(cropImage(source, payload.rect), 1200);
    return { result: { width: preview.width, height: preview.height, buffer: preview.data.buffer }, transfer: [preview.data.buffer] };
  },
  async preview(payload) {
    const rendered = renderObject(source, payload.rect, payload.options, payload.strokes, context());
    if (payload.highlightRepairs) highlight(rendered);
    const preview = fitImage(rendered, 1200);
    return { result: { width: preview.width, height: preview.height, inkPixels: rendered.inkPixels, inferredPixels: rendered.inferredPixels,
      uncertainPixels: rendered.uncertainPixels, buffer: preview.data.buffer }, transfer: [preview.data.buffer] };
  },
  async export(payload) {
    const rendered = renderObject(source, payload.rect, payload.options, payload.strokes, context());
    const image = trimAndPad(rendered, 16), factor = [1, 2, 3].includes(payload.scale) ? payload.scale : 1;
    const width = image.width * factor, height = image.height * factor;
    if (width * height > MAX_OUTPUT_PIXELS) throw new Error('Результат больше 32 мегапикселей. Уменьшите масштаб или область.');
    let canvas = canvasOf(image);
    if (factor > 1) {
      const larger = new OffscreenCanvas(width, height), ctx = larger.getContext('2d');
      ctx.imageSmoothingEnabled = true; ctx.imageSmoothingQuality = 'high'; ctx.drawImage(canvas, 0, 0, width, height);
      canvas.width = 1; canvas = larger;
    }
    const blob = await canvas.convertToBlob({ type: 'image/png' });
    const buffer = await blob.arrayBuffer(); canvas.width = 1;
    return { result: { buffer, width, height, inferredPixels: rendered.inferredPixels }, transfer: [buffer] };
  }
};

self.onmessage = async event => {
  const { id, type, payload } = event.data;
  try {
    const handler = handlers[type];
    if (!handler) throw new Error('Неизвестная операция обработки.');
    const output = await handler(payload || {});
    const result = output.result ?? output, transfer = output.transfer || [];
    self.postMessage({ id, ok: true, result }, transfer);
  } catch (error) {
    self.postMessage({ id, ok: false, error: error?.message || 'Не удалось обработать изображение.' });
  }
};
