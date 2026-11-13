/* Opening images and PDF documents. PDF pages are rendered locally by pdf.js. */
import * as pdfjs from '../vendor/pdfjs/build/pdf.min.mjs';
import { MAX_PIXELS } from '../lib/processing.js';

const vendor = path => new URL('../vendor/pdfjs/' + path, import.meta.url).href;
pdfjs.GlobalWorkerOptions.workerSrc = vendor('build/pdf.worker.min.mjs');

export const IMAGE_TYPES = /\.(png|jpe?g|jfif|webp|bmp|gif|avif)$/i;
export const MAX_FILE_BYTES = 200 * 1024 * 1024;
export const MAX_SIDE = 16000;

function fitPixels(width, height) {
  return Math.min(1, Math.sqrt(MAX_PIXELS / (width * height)), MAX_SIDE / Math.max(width, height));
}

function canvasPixels(draw, width, height) {
  const canvas = new OffscreenCanvas(width, height), ctx = canvas.getContext('2d', { willReadFrequently: true });
  ctx.fillStyle = 'white'; ctx.fillRect(0, 0, width, height);
  return { canvas, ctx };
}

async function openImage(file) {
  let bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
  return {
    kind: 'image', pageCount: 1,
    async renderPage() {
      if (!bitmap) bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
      const factor = fitPixels(bitmap.width, bitmap.height);
      const width = Math.max(1, Math.floor(bitmap.width * factor)), height = Math.max(1, Math.floor(bitmap.height * factor));
      const { canvas, ctx } = canvasPixels(null, width, height);
      ctx.drawImage(bitmap, 0, 0, width, height);
      const data = ctx.getImageData(0, 0, width, height).data; canvas.width = 1;
      return { width, height, data, reduced: factor < 1 };
    },
    close() { bitmap?.close(); bitmap = null; }
  };
}

async function openPdf(file) {
  const data = new Uint8Array(await file.arrayBuffer());
  const task = pdfjs.getDocument({
    data, cMapUrl: vendor('cmaps/'), cMapPacked: true, standardFontDataUrl: vendor('standard_fonts/'),
    wasmUrl: vendor('wasm/'), iccUrl: vendor('iccs/'), isEvalSupported: false, enableXfa: false,
    useSystemFonts: false, stopAtErrors: false
  });
  let pdf;
  try { pdf = await task.promise; }
  catch (error) {
    if (error?.name === 'PasswordException') throw new Error('PDF защищён паролем. Снимите пароль и откройте файл снова.');
    if (error?.name === 'InvalidPDFException') throw new Error('Файл повреждён или не является PDF.');
    throw error;
  }
  return {
    kind: 'pdf', pageCount: pdf.numPages,
    async renderPage(index, dpi = 200) {
      const page = await pdf.getPage(index + 1);
      try {
        const base = page.getViewport({ scale: 1 });
        let scale = dpi / 72;
        scale *= fitPixels(base.width * scale, base.height * scale);
        const viewport = page.getViewport({ scale });
        const width = Math.max(1, Math.floor(viewport.width)), height = Math.max(1, Math.floor(viewport.height));
        const { canvas, ctx } = canvasPixels(null, width, height);
        await page.render({ canvasContext: ctx, canvas, viewport, background: 'white',
          annotationMode: pdfjs.AnnotationMode.ENABLE, intent: 'print' }).promise;
        const pixels = ctx.getImageData(0, 0, width, height).data; canvas.width = 1;
        return { width, height, data: pixels, reduced: scale < dpi / 72 - 1e-6, dpi: Math.round(scale * 72) };
      } finally { page.cleanup(); }
    },
    close() { pdf.destroy(); }
  };
}

/** Opens a PDF or image file. The original file is only read, never changed. */
export async function openDocument(file) {
  if (file.size > MAX_FILE_BYTES) throw new Error('Файл больше 200 МБ. Сохраните уменьшенную копию.');
  const head = new Uint8Array(await file.slice(0, 5).arrayBuffer());
  const isPdf = /\.pdf$/i.test(file.name) || String.fromCharCode(...head) === '%PDF-';
  if (isPdf) return openPdf(file);
  if (IMAGE_TYPES.test(file.name) || /^image\//.test(file.type)) return openImage(file);
  throw new Error('Поддерживаются PDF, PNG, JPG, WEBP, BMP, GIF и AVIF. TIFF и HEIC сначала сохраните как PDF, PNG или JPG.');
}
