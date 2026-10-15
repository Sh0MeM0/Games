import { cropImage, renderRegion, trimAndPad, MAX_OUTPUT_PIXELS } from '../lib/processing.js';
import { analyzeDocument, renderExtractedRegion, scaleAnalysis } from '../lib/extraction.js';
let source, analysis, words, ocrSize;
function scaleWord(word, sx, sy) {
  const bbox = b => ({ x0: b.x0*sx, y0:b.y0*sy, x1:b.x1*sx, y1:b.y1*sy });
  return { ...word, bbox:bbox(word.bbox), symbols:(word.symbols||[]).map(symbol=>({...symbol,bbox:bbox(symbol.bbox)})) };
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
      analysis = null; words = null; ocrSize = null;
      result = {};
    } else {
      if (!source) throw new Error('Сначала откройте изображение.');
      if (type === 'ocrInput') {
        const small = fitImage(source, 2400);
        ocrSize = { width: small.width, height: small.height };
        const cleaned = renderRegion(small, { x: 0, y: 0, w: small.width, h: small.height }, { threshold: 12, speckle: 2, contrast: 1 });
        const canvas = canvasOf(cleaned);
        const blob = await canvas.convertToBlob({ type: 'image/png' });
        const buffer = await blob.arrayBuffer(); canvas.width = 1;
        result = { buffer }; transfer = [buffer];
      } else if (type === 'detect') {
        if (payload.words) {
          if (!ocrSize) throw new Error('Не подготовлено изображение для распознавания.');
          const sx = source.width / ocrSize.width, sy = source.height / ocrSize.height;
          words = payload.words.map(word => scaleWord(word, sx, sy));
        }
        if (!words) throw new Error('Сначала выполните распознавание текста.');
        const small = fitImage(source, 1800);
        const sx = small.width / source.width, sy = small.height / source.height;
        const scaledWords = words.map(word => scaleWord(word, sx, sy));
        analysis = scaleAnalysis(analyzeDocument(small, scaledWords, payload), 1 / sx, 1 / sy);
        result = { regions: analysis.regions.map(({selection, ...region}) => region), stats: analysis.stats, truncated: analysis.truncated };
      } else if (type === 'previewOriginal') {
        const preview = fitImage(cropImage(source, payload.rect), 1200);
        result = { width: preview.width, height: preview.height, buffer: preview.data.buffer };
        transfer = [result.buffer];
      } else if (type === 'preview' || type === 'export') {
        const rendered = renderExtractedRegion(source, payload.rect, payload.options, payload.strokes, analysis);
        if (type === 'preview') {
          if(payload.highlightRepairs&&rendered.inferredMask) {
            const mask=rendered.inferredMask;
            for(let y=0;y<rendered.height;y++)for(let x=0;x<rendered.width;x++)if(mask[y*rendered.width+x]) {
              for(let dy=-1;dy<=1;dy++)for(let dx=-1;dx<=1;dx++) {const xx=x+dx,yy=y+dy;
                if(xx<0||xx>=rendered.width||yy<0||yy>=rendered.height)continue;
                const p=(yy*rendered.width+xx)*4;rendered.data[p]=245;rendered.data[p+1]=110;rendered.data[p+2]=20;
              }
            }
          }
          const preview = fitImage(rendered, 1200);
          result = { width: preview.width, height: preview.height, inkPixels: rendered.inkPixels, inferredPixels: rendered.inferredPixels || 0, uncertainPixels: rendered.uncertainPixels || 0, buffer: preview.data.buffer };
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
          result = { buffer, width, height, inferredPixels:rendered.inferredPixels||0 }; transfer = [buffer];
        }
      } else throw new Error('Неизвестная операция обработки.');
    }
    self.postMessage({ id, ok: true, result }, transfer);
  } catch (error) { self.postMessage({ id, ok: false, error: error.message || 'Не удалось обработать изображение.' }); }
};
