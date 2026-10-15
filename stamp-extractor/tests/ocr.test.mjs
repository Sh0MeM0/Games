import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import ocrModule from '../lib/ocr.cjs';

const { OfflineOcr, validatePng } = ocrModule;
const root = fileURLToPath(new URL('..', import.meta.url));
const document = fs.readFileSync(path.join(root, 'examples/document.png'));

test('real bundled OCR recognizes Russian and English document words and coordinates', async () => {
  const ocr = new OfflineOcr();
  try {
    const result = await ocr.recognize(document);
    assert.equal(result.width, 1800);
    assert.equal(result.height, 1400);
    assert.ok(result.words.length > 80);
    for (const text of ['ДОКУМЕНТ', 'TEST', 'Printed', 'Ответственный:', 'Проверил:']) {
      const word = result.words.find(word => word.text === text);
      assert.ok(word, `real OCR recognized ${text}`);
      assert.ok(word.confidence >= 80, `${text} has useful confidence`);
      assert.ok(word.bbox.x1 > word.bbox.x0 && word.bbox.y1 > word.bbox.y0);
    }
    const heading = result.words.find(word => word.text === 'ДОКУМЕНТ');
    assert.ok(heading.bbox.y0 < 140 && heading.bbox.x0 > 300);
    // A second image goes through the same real WASM worker, not cached words.
    const second = await ocr.recognize(fs.readFileSync(path.join(root, 'tests/fixtures/document-text-only.png')));
    assert.ok(second.words.some(word => word.text === 'Printed'));
    assert.equal(second.words.some(word => word.text === 'ОБРАЗЕЦ'), false);
  } finally { ocr.close(); }
});

test('OCR rejects paths, arbitrary data and oversized PNG dimensions before decoding', async () => {
  const ocr = new OfflineOcr();
  try {
    await assert.rejects(ocr.recognize('/etc/passwd'), /PNG/);
    await assert.rejects(ocr.recognize(Buffer.from('not an image')), /PNG/);
    const forged = Buffer.from(document); forged.writeUInt32BE(20000, 16);
    assert.throws(() => validatePng(forged), /слишком большое/);
    assert.equal(ocr.worker, undefined);
  } finally { ocr.close(); }
});

test('missing offline models fail locally without downloading a fallback', async () => {
  const ocr = new OfflineOcr({ modelDirectory: path.join(root, 'test-results/nonexistent-ocr-models') });
  try {
    await assert.rejects(ocr.recognize(document), /Не найдены встроенные модули/);
    assert.equal(ocr.worker, undefined);
  } finally { ocr.close(); }
});

test('OCR timeout terminates initialization worker and bounded queue rejects excess jobs', async () => {
  const ocr = new OfflineOcr({ timeoutMs: 1 });
  try {
    const jobs = Array.from({ length: 4 }, () => ocr.recognize(document));
    const results = await Promise.allSettled(jobs);
    assert.ok(results.every(result => result.status === 'rejected'));
    assert.match(results[3].reason.message, /уже выполняется/);
    assert.match(results[0].reason.message, /слишком много времени/);
    assert.equal(ocr.worker, undefined);
    assert.equal(ocr.pending.size, 0);
    assert.equal(ocr.queued, 0);
  } finally { ocr.close(); }
  await assert.rejects(ocr.recognize(document), /закрывается/);
});
