'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { Worker } = require('node:worker_threads');

const MODELS = Object.freeze({
  'eng.traineddata': '7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2',
  'rus.traineddata': 'e16e5e036cce1d9ec2b00063cf8b54472625b9e14d893a169e2b0dedeb4df225'
});
const PNG = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);

function validatePng(input) {
  if (!(input instanceof ArrayBuffer) && !(input instanceof Uint8Array)) {
    throw new Error('Для распознавания нужно передать изображение PNG.');
  }
  const bytes = input instanceof ArrayBuffer
    ? Buffer.from(input) : Buffer.from(input.buffer, input.byteOffset, input.byteLength);
  if (bytes.length < 33 || bytes.length > 80 * 1024 * 1024 || !bytes.subarray(0, 8).equals(PNG)
      || bytes.readUInt32BE(8) !== 13 || bytes.toString('ascii', 12, 16) !== 'IHDR') {
    throw new Error('Не удалось прочитать PNG для распознавания текста.');
  }
  const width = bytes.readUInt32BE(16), height = bytes.readUInt32BE(20);
  if (!width || !height || width > 16000 || height > 16000 || width * height > 24_000_000) {
    throw new Error('Изображение слишком большое для распознавания. Уменьшите его до 24 мегапикселей.');
  }
  return { bytes, width, height };
}

function flattenWords(blocks, width, height) {
  const words = [];
  for (const block of blocks || []) for (const paragraph of block.paragraphs || []) {
    for (const line of paragraph.lines || []) for (const word of line.words || []) {
      const box = word.bbox;
      if (!box || !['x0', 'y0', 'x1', 'y1'].every(key => Number.isFinite(box[key]))) continue;
      const bbox = {
        x0: Math.max(0, Math.min(width, Math.floor(box.x0))),
        y0: Math.max(0, Math.min(height, Math.floor(box.y0))),
        x1: Math.max(0, Math.min(width, Math.ceil(box.x1))),
        y1: Math.max(0, Math.min(height, Math.ceil(box.y1)))
      };
      const text = String(word.text || '').trim();
      if (!text || bbox.x1 <= bbox.x0 || bbox.y1 <= bbox.y0) continue;
      const symbols = (word.symbols || []).filter(symbol => symbol.bbox &&
        ['x0','y0','x1','y1'].every(key => Number.isFinite(symbol.bbox[key]))).map(symbol => ({
          text: String(symbol.text || ''), confidence: Number(symbol.confidence) || 0,
          bbox: { x0: Math.max(0, Math.floor(symbol.bbox.x0)), y0: Math.max(0, Math.floor(symbol.bbox.y0)),
            x1: Math.min(width, Math.ceil(symbol.bbox.x1)), y1: Math.min(height, Math.ceil(symbol.bbox.y1)) }
        }));
      words.push({ text, confidence: Number.isFinite(word.confidence) ? word.confidence : 0, bbox, symbols });
      if (words.length >= 30_000) return words;
    }
  }
  return words;
}

/** Offline Tesseract.js 7.0.0, one owned Node worker and a bounded serial queue.
 * The pinned upstream worker protocol is used directly so a hung initialization
 * can be terminated too (createWorker() only exposes its worker after loading).
 * Models and WASM are local files; no URL or caller-supplied path is accepted.
 */
class OfflineOcr {
  constructor({ modelDirectory, timeoutMs = 120_000, workerPath } = {}) {
    this.modelDirectory = modelDirectory || path.join(__dirname, '..', 'resources', 'ocr');
    this.workerPath = workerPath || require.resolve('tesseract.js/src/worker-script/node/index.js')
      .replace(`${path.sep}app.asar${path.sep}`, `${path.sep}app.asar.unpacked${path.sep}`);
    this.timeoutMs = timeoutMs;
    this.worker = undefined;
    this.pending = new Map();
    this.sequence = 0;
    this.queued = 0;
    this.tail = Promise.resolve();
    this.closed = false;
    this.checkedModels = false;
  }

  async recognize(input) {
    const image = validatePng(input);
    if (this.closed) throw new Error('Программа закрывается.');
    if (this.queued >= 3) throw new Error('Распознавание уже выполняется. Дождитесь завершения.');
    this.queued++;
    const task = this.tail.then(async () => {
      if (this.closed) throw new Error('Программа закрывается.');
      await this.start();
      const result = await this.send('recognize', {
        image: image.bytes,
        options: {},
        output: { text: false, blocks: true }
      });
      return { words: flattenWords(result.blocks, image.width, image.height), width: image.width, height: image.height };
    }).finally(() => { this.queued--; });
    this.tail = task.catch(() => {});
    return task;
  }

  async start() {
    if (this.worker) return;
    if (!this.checkedModels) {
      for (const [filename, expected] of Object.entries(MODELS)) {
        let actual;
        try { actual = createHash('sha256').update(fs.readFileSync(path.join(this.modelDirectory, filename))).digest('hex'); }
        catch { throw new Error('Не найдены встроенные модули распознавания. Скачайте portable-программу заново.'); }
        if (actual !== expected) throw new Error('Встроенные модули распознавания повреждены. Скачайте portable-программу заново.');
      }
      this.checkedModels = true;
    }
    let worker;
    try { worker = new Worker(this.workerPath); }
    catch { throw new Error('Не удалось запустить встроенный модуль распознавания. Скачайте программу заново.'); }
    this.worker = worker;
    worker.on('message', packet => {
      const current = this.pending.get(packet.jobId);
      if (!current || !['resolve', 'reject'].includes(packet.status)) return;
      this.pending.delete(packet.jobId); clearTimeout(current.timer);
      if (packet.status === 'resolve') current.resolve(packet.data);
      else current.reject(new Error('Не удалось распознать текст. Попробуйте изображение меньшего размера или другой снимок.'));
    });
    worker.on('error', () => {
      if (this.worker === worker) this.stop(new Error('Модуль распознавания остановился. Повторите обработку изображения.'));
    });
    worker.on('exit', () => {
      if (this.worker === worker) this.stop(new Error('Модуль распознавания остановился. Повторите обработку изображения.'));
    });
    try {
      // The Node adapter accepts the LSTM_ONLY numeric OEM (1) here.
      await this.send('load', { options: { lstmOnly: 1, logging: false } });
      await this.send('loadLanguage', { langs: 'rus+eng', options: {
        langPath: this.modelDirectory, cacheMethod: 'none', gzip: false, lstmOnly: true
      } });
      await this.send('initialize', { langs: 'rus+eng', oem: 1, config: {} });
      await this.send('setParameters', { params: { tessedit_pageseg_mode: '11', user_defined_dpi: '300' } });
    } catch (error) { this.stop(error); throw error; }
  }

  send(action, payload) {
    return new Promise((resolve, reject) => {
      if (!this.worker || this.closed) { reject(new Error('Программа закрывается.')); return; }
      const jobId = `ocr-${++this.sequence}`;
      const timer = setTimeout(() => this.stop(new Error('Распознавание заняло слишком много времени. Уменьшите изображение и повторите.')), this.timeoutMs);
      this.pending.set(jobId, { resolve, reject, timer });
      try { this.worker.postMessage({ workerId: 'offline-ocr', jobId, action, payload }); }
      catch { this.stop(new Error('Не удалось запустить распознавание. Повторите обработку.')); }
    });
  }

  stop(reason = new Error('Распознавание отменено.')) {
    const worker = this.worker; this.worker = undefined;
    for (const current of this.pending.values()) { clearTimeout(current.timer); current.reject(reason); }
    this.pending.clear();
    if (worker) worker.terminate().catch(() => {});
  }

  close() { this.closed = true; this.stop(new Error('Программа закрывается.')); }
}

module.exports = { OfflineOcr, validatePng, flattenWords, MODELS };
