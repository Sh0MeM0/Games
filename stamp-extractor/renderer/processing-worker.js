import { cropImage, renderRegion, trimAndPad, detectRegions, MAX_OUTPUT_PIXELS } from '../lib/processing.js';
let source;
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
  context.drawImage(original, 0, 0, width, height); original.width = 1;
  const data = context.getImageData(0, 0, width, height).data; canvas.width = 1;
  return { width, height, data };
}
self.onmessage = async event => {
  const { id, type, payload } = event.data;
  try {
    let result, transfer = [];
    if (type === 'init') {
      source = { width: payload.width, height: payload.height, data: new Uint8ClampedArray(payload.buffer) };
      result = {};
    } else {
      if (!source) throw new Error('Сначала откройте изображение.');
      if (type === 'detect') {
        const analysis = fitImage(source, 1400);
        const detected = detectRegions(analysis, payload);
        const sx = source.width / analysis.width, sy = source.height / analysis.height;
        result = { ...detected, regions: detected.regions.map(r => ({ x: Math.floor(r.x * sx), y: Math.floor(r.y * sy),
          w: Math.min(source.width - Math.floor(r.x * sx), Math.ceil(r.w * sx)),
          h: Math.min(source.height - Math.floor(r.y * sy), Math.ceil(r.h * sy)) })) };
      } else if (type === 'previewOriginal') {
        const preview = fitImage(cropImage(source, payload.rect), 1200);
        result = { width: preview.width, height: preview.height, buffer: preview.data.buffer };
        transfer = [result.buffer];
      } else if (type === 'preview' || type === 'export') {
        const rendered = renderRegion(source, payload.rect, payload.options, payload.strokes);
        if (type === 'preview') {
          const preview = fitImage(rendered, 1200);
          result = { width: preview.width, height: preview.height, inkPixels: rendered.inkPixels, buffer: preview.data.buffer };
          transfer = [result.buffer];
        } else {
          const image = trimAndPad(rendered, 16), scale = [1, 2, 3].includes(payload.scale) ? payload.scale : 1;
          const width = image.width * scale, height = image.height * scale;
          if (width * height > MAX_OUTPUT_PIXELS) throw new Error('Результат больше 32 мегапикселей. Уменьшите масштаб или область.');
          let canvas = canvasOf(image);
          if (scale > 1) {
            const larger = new OffscreenCanvas(width, height), ctx = larger.getContext('2d');
            ctx.imageSmoothingEnabled = true; ctx.imageSmoothingQuality = 'high'; ctx.drawImage(canvas, 0, 0, width, height);
            canvas.width = 1; canvas = larger;
          }
          const blob = await canvas.convertToBlob({ type: 'image/png' });
          const buffer = await blob.arrayBuffer(); canvas.width = 1;
          result = { buffer, width, height }; transfer = [buffer];
        }
      } else throw new Error('Неизвестная операция обработки.');
    }
    self.postMessage({ id, ok: true, result }, transfer);
  } catch (error) { self.postMessage({ id, ok: false, error: error.message || 'Не удалось обработать изображение.' }); }
};
