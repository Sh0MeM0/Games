// Real desktop flow: bundled OCR, authored printed document, extracted PNGs.
// The only application dependency replaced is the interactive output dialog.
const { _electron } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const { PNG } = require('pngjs');

const root = path.resolve(__dirname, '..');
const results = path.join(root, 'test-results');
const digest = bytes => crypto.createHash('sha256').update(bytes).digest('hex');

(async () => {
  await fs.mkdir(results, { recursive: true });
  const output = await fs.mkdtemp(path.join(results, 'document-export-'));
  const fixture = JSON.parse(await fs.readFile(path.join(__dirname, 'fixtures/document.json'), 'utf8'));
  const source = path.join(root, 'examples/document.png');
  const sourceHash = digest(await fs.readFile(source));
  const decoded = PNG.sync.read(await fs.readFile(source));
  const doubled = new PNG({ width: decoded.width * 2, height: decoded.height * 2 });
  for (let y = 0; y < doubled.height; y++) for (let x = 0; x < doubled.width; x++) {
    const from = (Math.floor(y / 2) * decoded.width + Math.floor(x / 2)) * 4;
    const to = (y * doubled.width + x) * 4;
    decoded.data.copy(doubled.data, to, from, from + 4);
  }
  const highResolution = path.join(results, 'document-hires.png');
  await fs.writeFile(highResolution, PNG.sync.write(doubled));
  const packagedExecutable = process.env.EXTRACTOR_EXECUTABLE;
  const args = process.platform === 'linux'
    ? ['--no-sandbox', '--ozone-platform=headless', '--disable-gpu'] : [];
  if (!packagedExecutable) args.push(root);
  const app = await _electron.launch({ executablePath: packagedExecutable || require('electron'), args, cwd: root, timeout: 30_000 });
  let page;
  try {
    await app.evaluate(({ dialog }, directory) => {
      dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [directory] });
    }, output);
    page = await app.firstWindow();
    page.setDefaultTimeout(120_000);
    const errors = [], networkRequests = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('request', request => { if (/^https?:/.test(request.url())) networkRequests.push(request.url()); });
    page.on('dialog', dialog => dialog.accept());
    await page.waitForLoadState('domcontentloaded');
    await page.setViewportSize({ width: 1440, height: 960 });
    assert.equal(await page.evaluate(() => typeof window.require), 'undefined');
    assert.equal(await page.evaluate(() => typeof window.desktop.recognize), 'function');

    async function open(filename) {
      await page.locator('#file').setInputFiles(filename);
      await page.waitForFunction(name => document.getElementById('sourceInfo').textContent.includes(name), path.basename(filename));
      await page.waitForFunction(() => !document.getElementById('open').disabled);
      const status = await page.locator('#status').textContent();
      assert.equal(await page.locator('#status').evaluate(el => el.classList.contains('error')), false, status);
    }
    async function ready() {
      await page.waitForFunction(() => !document.getElementById('preview').hidden && document.getElementById('previewBusy').hidden);
    }
    async function previewInk() {
      await ready();
      return page.locator('#preview').evaluate(canvas => {
        const data = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
        let ink = 0;
        for (let i = 0; i < data.length; i += 4) if (Math.min(data[i], data[i+1], data[i+2]) < 230) ink++;
        return ink;
      });
    }
    async function assertFourObjects() {
      assert.equal(await page.locator('#count').textContent(), '4', await page.locator('#status').textContent());
      assert.match(await page.locator('#analysisInfo').textContent(), /Печати: 2 · Подписи: 2/);
      const names = await page.locator('.object-row > button:first-child').allTextContents();
      assert.equal(names.filter(name => name.includes('печать')).length, 2);
      assert.equal(names.filter(name => name.includes('подпись')).length, 2);
    }
    async function exportFour() {
      await page.locator('#saveAll').click();
      await page.waitForFunction(() => !document.getElementById('open').disabled &&
        document.getElementById('status').textContent.includes('Сохранено отдельных PNG: 4 из 4.'));
    }

    await open(source); // Opening starts actual Russian + English OCR automatically.
    await assertFourObjects();
    await ready();
    await page.screenshot({ path: path.join(results, 'document-interface.png') });
    await exportFour();
    const exported = (await fs.readdir(output)).filter(name => name.endsWith('.png')).sort();
    assert.equal(exported.length, 4);
    const references = fixture.objects.slice().sort((a, b) => a.bbox.y - b.bbox.y || a.bbox.x - b.bbox.x);
    const filePairs = references.map((reference, i) => {
      const suffix = `_${String(i+1).padStart(2, '0')}.png`;
      const filename = exported.find(name => name.endsWith(suffix));
      assert.ok(filename, `Missing separate output for ${reference.id}`);
      return { id: reference.id, kind: reference.kind, result: path.join(output, filename),
        expected: path.join(__dirname, 'fixtures', reference.clean_reference) };
    });
    const quality = await app.evaluate(({ nativeImage }, pairs) => {
      function inspect(filename) {
        const image = nativeImage.createFromPath(filename), { width, height } = image.getSize(), data = image.getBitmap();
        let ink = 0, opaque = true, whiteBorder = true, centerInk = 0;
        for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
          const p = (y * width + x) * 4;
          if (data[p+3] !== 255) opaque = false;
          if (Math.min(data[p], data[p+1], data[p+2]) < 230) {
            ink++;
            if (x > width*.3 && x < width*.7 && y > height*.3 && y < height*.7) centerInk++;
          }
          if ((x < 8 || x >= width-8 || y < 8 || y >= height-8) &&
            (data[p] !== 255 || data[p+1] !== 255 || data[p+2] !== 255 || data[p+3] !== 255)) whiteBorder = false;
        }
        return { width, height, ink, opaque, whiteBorder, centerInk };
      }
      return pairs.map(pair => ({ id: pair.id, kind: pair.kind, actual: inspect(pair.result), expected: inspect(pair.expected) }));
    }, filePairs);
    for (const { id, kind, actual, expected } of quality) {
      assert.ok(actual.opaque && actual.whiteBorder, `${id}: output must have opaque, pure-white margins`);
      assert.ok(Math.abs(actual.width - expected.width) <= 18 && Math.abs(actual.height - expected.height) <= 18,
        `${id}: crop dimensions differ from the isolated object: ${JSON.stringify({actual, expected})}`);
      assert.ok(actual.ink > expected.ink * .75, `${id}: too much object ink removed`);
      assert.ok(actual.ink < expected.ink * 1.4, `${id}: too much unrelated ink retained`);
      if (kind === 'stamp') assert.ok(actual.centerInk > expected.centerInk * .75, `${id}: own seal lettering was erased`);
    }

    // A deliberately broad manual frame contains paragraphs as well as objects.
    // The text-removal switch must change pixels, while protected seal letters stay.
    await page.locator('#draw').click();
    const overlay = await page.locator('#overlay').boundingBox();
    await page.mouse.move(overlay.x+2, overlay.y+2);
    await page.mouse.down();
    await page.mouse.move(overlay.x+overlay.width-2, overlay.y+overlay.height-2, { steps: 8 });
    await page.mouse.up();
    assert.equal(await page.locator('#count').textContent(), '5');
    const withoutText = await previewInk();
    await page.locator('#removeText').uncheck();
    const withText = await previewInk();
    assert.ok(withText > withoutText * 1.5, `Printed text removal had insufficient effect: ${withoutText} -> ${withText}`);
    await page.locator('#removeText').check();
    assert.equal(await previewInk(), withoutText);
    await page.locator('.object-row').last().locator('.remove').click();
    await assertFourObjects();

    await open(path.join(root, 'examples/document-photo.jpg'));
    await assertFourObjects();
    await exportFour();
    assert.equal((await fs.readdir(output)).filter(name => name.endsWith('.png')).length, 8);

    for (const variant of ['document-gray.png', 'document-overlap.png', 'document-rotated.png']) {
      await open(path.join(root, 'examples', variant));
      await assertFourObjects();
      assert.ok(await previewInk() > 500, `${variant}: extracted preview lost the object`);
    }

    // OCR works at <= 2400 px and detection at <= 1800 px, but output uses the
    // original scan. A 2x scan exercises both coordinate transformations.
    await open(highResolution);
    await assertFourObjects();
    await page.locator('#saveOne').click();
    await page.waitForFunction(() => !document.getElementById('open').disabled &&
      document.getElementById('status').textContent.includes('Сохранено отдельных PNG: 1 из 1.'));
    const highFiles = (await fs.readdir(output)).filter(name => name.startsWith('document-hires_') && name.endsWith('.png'));
    assert.equal(highFiles.length, 1);
    const highSize = await app.evaluate(({ nativeImage }, filename) => nativeImage.createFromPath(filename).getSize(), path.join(output, highFiles[0]));
    const normalSize = quality[0].actual;
    assert.ok(Math.abs(highSize.width - (normalSize.width * 2 - 32)) <= 6 &&
      Math.abs(highSize.height - (normalSize.height * 2 - 32)) <= 6,
      `High-resolution export mapping is wrong: ${JSON.stringify({ highSize, normalSize })}`);
    assert.equal((await fs.readdir(output)).filter(name => name.endsWith('.png')).length, 9);

    // Ordinary printed words and form rules must never become signature files.
    for (const negative of ['document-text-only.png', 'document-text-only-blue.png']) {
      await open(path.join(__dirname, 'fixtures', negative));
      assert.equal(await page.locator('#count').textContent(), '0', `${negative}: ${await page.locator('#status').textContent()}`);
      assert.equal(await page.locator('#saveAll').isDisabled(), true);
    }
    assert.equal(digest(await fs.readFile(source)), sourceHash);
    assert.deepEqual(errors, []);
    assert.deepEqual(networkRequests, []);
    await fs.writeFile(path.join(results, 'document-quality.json'), JSON.stringify(quality, null, 2)+'\n');
    console.log(`PASS: ${packagedExecutable ? 'packaged' : 'development'} Electron + offline OCR, 2 seals and 2 signatures in PNG/JPEG/grayscale/overlap/rotated/high-resolution documents, 9 separate PNGs, opaque white background, seal lettering retained, printed-text cleanup toggle, black/blue text-only negative controls, 3600px original-resolution export, source unchanged.`);
    console.log('Image quality metrics and screenshot:', results);
  } catch (error) {
    if (page) {
      console.error('Document source:', await page.locator('#sourceInfo').textContent());
      console.error('Document UI status:', await page.locator('#status').textContent());
      console.error('Document objects:', await page.locator('.object-row > button:first-child').allTextContents());
      await page.screenshot({ path: path.join(results, 'document-failure.png') });
    }
    throw error;
  } finally { await app.close(); }
})().catch(error => { console.error(error); process.exit(1); });
