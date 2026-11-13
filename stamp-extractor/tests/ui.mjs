#!/usr/bin/env node
// Interface test in real Chromium (the engine of WebView2) against the dev server.
// Usage: node tests/ui.mjs            — uses scripts/serve.mjs
//        node tests/ui.mjs <base url>  — e.g. the Neutralino server (tests/portable.mjs)
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { PNG } from 'pngjs';
import { startServer } from '../scripts/serve.mjs';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');
const out = path.join(root, 'test-results', 'ui'); fs.mkdirSync(out, { recursive: true });

export async function runUi(base, { native = false, saveDir = null } = {}) {
  const browser = await chromium.launch();
  const context = await browser.newContext({ viewport: { width: 1440, height: 940 }, acceptDownloads: true });
  const page = await context.newPage();
  const errors = [], external = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('request', r => { const u = new URL(r.url()); if (!['127.0.0.1', 'localhost'].includes(u.hostname) && !/^(blob|data):/.test(r.url())) external.push(r.url()); });
  const status = () => page.textContent('#status');
  const waitStatus = (re, timeout = 120000) => page.waitForFunction(r => new RegExp(r).test(document.getElementById('status').textContent), re.source, { timeout });
  try {
    await page.goto(base);
    await waitStatus(/Модель загружена|Не удалось/);
    assert.match(await status(), /Модель загружена/);
    if (native) assert.equal(await page.evaluate(() => typeof NL_TOKEN === 'string' && typeof Neutralino === 'object'), true);

    // 1. Image: automatic detection, preview, comparison, brush, kind switch.
    await page.setInputFiles('#file', path.join(root, 'examples', 'document.png'));
    await waitStatus(/выделено|не найдены|Не удалось/);
    const info = await page.textContent('#analysisInfo');
    console.log('document.png:', await status(), '|', info);
    assert.match(info, /Печати: [1-9]/); assert.match(info, /Подписи: [1-9]/);
    await page.waitForSelector('#preview:not([hidden])');
    await page.click('#compare'); await page.waitForFunction(() => /Исходная область/.test(document.getElementById('previewInfo').textContent));
    await page.click('#compare'); await page.waitForFunction(() => /белый фон|пусто/.test(document.getElementById('previewInfo').textContent));
    const box = await page.locator('#preview').boundingBox();
    await page.mouse.move(box.x + box.width * .3, box.y + box.height * .5); await page.mouse.down();
    await page.mouse.move(box.x + box.width * .6, box.y + box.height * .5, { steps: 8 }); await page.mouse.up();
    assert.equal(await page.isDisabled('#undo'), false);
    await page.click('#undo'); assert.equal(await page.isDisabled('#redo'), false);
    await page.selectOption('#kind', 'candidate'); await page.waitForTimeout(400);
    await page.locator('#sensitivity').fill('0.4'); await page.waitForTimeout(400);
    await page.screenshot({ path: path.join(out, 'image.png') });

    // 2. Clipboard paste of a screenshot.
    await page.evaluate(async () => {
      const blob = await (await fetch('/www/icon.png')).blob(), dt = new DataTransfer();
      dt.items.add(new File([blob], 'clip.png', { type: 'image/png' }));
      window.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt }));
    });
    await waitStatus(/вставка|выделено|не найдены/); await page.waitForFunction(() => /вставка\.png/.test(document.getElementById('sourceInfo').textContent));

    // 3. Two-page scanned PDF: pager, all pages, page switch.
    await page.setInputFiles('#file', path.join(root, 'tests', 'fixtures', 'pdf', 'scan-2pages.pdf'));
    await waitStatus(/страница 1: (выделено|печати и подписи не найдены)/);
    assert.equal(await page.textContent('#pageValue'), '1 / 2');
    await page.click('#allPages'); await waitStatus(/Проверено страниц: 2 из 2/);
    console.log('scan-2pages.pdf:', await status());
    await page.click('#nextPage'); await page.waitForFunction(() => /стр\. 2 из 2/.test(document.getElementById('sourceInfo').textContent));
    const page2 = await page.textContent('#analysisInfo'); console.log('  page 2:', page2);
    assert.match(page2, /Печати: [1-9]/);
    await page.screenshot({ path: path.join(out, 'pdf-page2.png') });

    // 4. Export of all objects of both pages.
    const total = Number((await page.textContent('#saveAll')).match(/\((\d+)\)/)[1]);
    if (native) {
      await page.evaluate(dir => { Neutralino.os.showFolderDialog = async () => dir; }, saveDir); // only the dialog is automated
      await page.click('#saveAll'); await waitStatus(/Сохранено отдельных PNG/);
      const files = fs.readdirSync(saveDir).filter(f => f.endsWith('.png'));
      assert.equal(files.length, total); assert.ok(files.some(f => f.includes('_стр01_')) && files.some(f => f.includes('_стр02_')));
      for (const f of files) {
        const png = PNG.sync.read(fs.readFileSync(path.join(saveDir, f)));
        assert.deepEqual([...png.data.subarray(0, 4)], [255, 255, 255, 255], 'white opaque margin');
      }
      console.log('saved through Neutralino filesystem API:', files.length, 'files');
    } else {
      const downloads = [];
      page.on('download', d => downloads.push(d));
      await page.click('#saveAll'); await waitStatus(/Сохранено отдельных PNG/);
      assert.match(await status(), new RegExp(`Сохранено отдельных PNG: ${total} из ${total}`));
      for (let i = 0; i < 40 && downloads.length < total; i++) await page.waitForTimeout(250);
      // Chromium may throttle many automatic downloads; the desktop build writes files natively instead.
      assert.ok(downloads.length >= 1);
      console.log('downloads:', downloads.length);
    }

    // 5. Vector PDF: standard font text, raster stamp image, vector signature.
    await page.setInputFiles('#file', path.join(root, 'tests', 'fixtures', 'pdf', 'vector.pdf'));
    await waitStatus(/выделено|не найдены/);
    console.log('vector.pdf:', await status(), '|', await page.textContent('#analysisInfo'));
    await page.screenshot({ path: path.join(out, 'vector.png') });

    assert.deepEqual(external, [], 'no external network requests');
    assert.deepEqual(errors.filter(e => !/Warning/i.test(e)), [], 'no page errors');
    console.log('UI test passed');
  } finally { await browser.close(); }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  let server, base = process.argv[2];
  if (!base) { server = await startServer(); base = `http://127.0.0.1:${server.address().port}/www/`; }
  try { await runUi(base); } finally { server?.close(); }
}
