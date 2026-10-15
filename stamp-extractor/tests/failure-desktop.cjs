// Real Electron regression checks for authored reproductions of reported errors.
// These fixtures are not the user's attachments. OCR, worker processing and PNG
// export are real; only the interactive directory picker is replaced.
const { _electron } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const { PNG } = require('pngjs');

const root = path.resolve(__dirname, '..');
const fixtures = path.join(__dirname, 'fixtures-v3');
const results = path.join(root, 'test-results');
const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const hasMask = (image, i) => image.data[i * 4] > 32;
const isInk = (data, i) => Math.min(data[i * 4], data[i * 4 + 1], data[i * 4 + 2]) < 230;
const overlap = (a, b) => Math.max(0, Math.min(a.x+a.w,b.x+b.w)-Math.max(a.x,b.x)) *
  Math.max(0, Math.min(a.y+a.h,b.y+b.h)-Math.max(a.y,b.y));
const objectBox = object => ({ x: object.bbox.x, y: object.bbox.y, w: object.bbox.width, h: object.bbox.height });
const ratio = (a, b) => b ? Number((a / b).toFixed(5)) : null;

async function png(filename) { return PNG.sync.read(await fs.readFile(filename)); }

// Source-aligned comparison against independent generator masks, not merely
// total ink/counts. Desired visible pixels and foreign strokes are measured
// separately; antialias fringes adjacent to desired ink are not called errors.
async function measure(preview, rect, object, truth) {
  const visible = await png(path.join(fixtures, object.visible_mask));
  const full = await png(path.join(fixtures, object.mask));
  const letters = object.letter_mask ? await png(path.join(fixtures, object.letter_mask)) : null;
  const foreign = await Promise.all([
    ...truth.backgrounds,
    ...truth.objects.filter(other => other.id !== object.id).map(other => ({ id: other.id, mask: other.mask })),
    ...truth.objects.filter(other => object.kind === 'signature' && other.kind === 'stamp' && other.letter_mask)
      .map(other => ({ id: other.id+'.lettering', mask: other.letter_mask }))
  ].map(async other => ({ id: other.id, image: await png(path.join(fixtures, other.mask)), kept: 0, total: 0 })));
  let kept = 0, total = 0, lettersKept = 0, lettersTotal = 0;
  for (let y = 0; y < truth.height; y++) for (let x = 0; x < truth.width; x++) {
    const i = y * truth.width + x;
    const inside = x >= rect.x && x < rect.x + preview.width && y >= rect.y && y < rect.y + preview.height;
    const outputInk = inside && isInk(preview.data, (y-rect.y)*preview.width + x-rect.x);
    if (hasMask(visible, i)) {
      total++; if (outputInk) kept++;
      if (letters && hasMask(letters, i)) { lettersTotal++; if (outputInk) lettersKept++; }
    }
    if (!inside || hasMask(full, i)) continue;
    let adjacent = false;
    for (let yy = Math.max(0, y-1); yy <= Math.min(truth.height-1, y+1) && !adjacent; yy++)
      for (let xx = Math.max(0, x-1); xx <= Math.min(truth.width-1, x+1); xx++)
        if (hasMask(full, yy*truth.width+xx)) { adjacent = true; break; }
    if (adjacent) continue;
    for (const other of foreign) if (hasMask(other.image, i)) {
      other.total++; if (outputInk) other.kept++;
    }
  }
  return { id: object.id, visibleInkRetained: ratio(kept,total), visibleLetteringRetained: ratio(lettersKept,lettersTotal),
    unwanted: Object.fromEntries(foreign.map(other => [other.id, { retained: other.kept, total: other.total,
      retainedFraction: ratio(other.kept,other.total) }])) };
}

function inspectExport(image) {
  let opaque = true, whiteBorder = true, orange = 0, ink = 0;
  for (let y = 0; y < image.height; y++) for (let x = 0; x < image.width; x++) {
    const p = (y*image.width+x)*4, d = image.data;
    if (d[p+3] !== 255) opaque = false;
    if (Math.min(d[p],d[p+1],d[p+2]) < 230) ink++;
    if (d[p] > 220 && d[p+1] > 70 && d[p+1] < 160 && d[p+2] < 60) orange++;
    if ((x < 8 || x >= image.width-8 || y < 8 || y >= image.height-8) &&
      (d[p] !== 255 || d[p+1] !== 255 || d[p+2] !== 255 || d[p+3] !== 255)) whiteBorder = false;
  }
  return { opaque, whiteBorder, orange, ink };
}

(async () => {
  await fs.mkdir(results, { recursive: true });
  const output = await fs.mkdtemp(path.join(results, 'failures-export-'));
  const packagedExecutable = process.env.EXTRACTOR_EXECUTABLE;
  const args = process.platform === 'linux' ? ['--no-sandbox','--ozone-platform=headless','--disable-gpu'] : [];
  if (!packagedExecutable) args.push(root);
  const app = await _electron.launch({ executablePath: packagedExecutable || require('electron'), args, cwd: root, timeout: 30_000 });
  let page;
  const reports = [], failures = [], sourceHashes = new Map();
  const check = (condition, message) => { if (!condition) failures.push(message); };
  try {
    await app.evaluate(({ dialog }, directory) => { dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [directory] }); }, output);
    page = await app.firstWindow();
    page.setDefaultTimeout(120_000);
    const errors = [], requests = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('request', request => { if (/^https?:/.test(request.url())) requests.push(request.url()); });
    page.on('dialog', dialog => dialog.accept());
    await page.waitForLoadState('domcontentloaded');
    await page.setViewportSize({ width: 1440, height: 1050 });
    // Observe the actual regions painted by the UI. This records geometry only
    // and does not modify image data, detection, processing or user selection.
    await page.evaluate(() => {
      window.__testRegions = [];
      const clear = CanvasRenderingContext2D.prototype.clearRect;
      const fill = CanvasRenderingContext2D.prototype.fillRect;
      CanvasRenderingContext2D.prototype.clearRect = function (...args) {
        if (this.canvas.id === 'overlay') window.__testRegions = [];
        return clear.apply(this,args);
      };
      CanvasRenderingContext2D.prototype.fillRect = function (x,y,w,h) {
        if (this.canvas.id === 'overlay' && w > 24 && h > 20) window.__testRegions.push({x,y,w,h});
        return fill.call(this,x,y,w,h);
      };
    });
    async function ready() {
      await page.waitForFunction(() => !document.getElementById('preview').hidden && document.getElementById('previewBusy').hidden);
    }
    async function open(name) {
      const source = path.join(root,'examples',name+'.png');
      sourceHashes.set(source,sha(await fs.readFile(source)));
      await page.locator('#file').setInputFiles(source);
      await page.waitForFunction(filename => document.getElementById('sourceInfo').textContent.includes(filename),name+'.png');
      await page.waitForFunction(() => !document.getElementById('open').disabled);
      assert.equal(await page.locator('#status').evaluate(element => element.classList.contains('error')),false,await page.locator('#status').textContent());
    }
    async function readPreview() {
      await ready();
      return page.locator('#preview').evaluate(canvas => ({ width: canvas.width, height: canvas.height,
        data: Array.from(canvas.getContext('2d').getImageData(0,0,canvas.width,canvas.height).data) }));
    }
    async function exportOne(name) {
      await page.locator('#name').fill(name);
      const before = new Set(await fs.readdir(output));
      await page.locator('#saveOne').click();
      await page.waitForFunction(() => !document.getElementById('open').disabled &&
        document.getElementById('status').textContent.includes('Сохранено отдельных PNG: 1 из 1.'));
      const created = (await fs.readdir(output)).filter(file => file.endsWith('.png') && !before.has(file));
      assert.equal(created.length,1);
      const image = await png(path.join(output,created[0]));
      const stats = inspectExport(image);
      check(stats.opaque && stats.whiteBorder,`${created[0]}: PNG must be opaque with pure-white margins`);
      check(stats.orange === 0,`${created[0]}: diagnostic orange overlay leaked into export`);
      return { name: created[0], image, stats };
    }

    for (const name of ['failures-rectangular','failures-rectangular-clean','failures-form-signature','failures-text-only','failures-text-only-blue']) {
      await open(name);
      const truth = JSON.parse(await fs.readFile(path.join(fixtures,name+'.json'),'utf8'));
      const names = await page.locator('.object-row > button:first-child').allTextContents();
      const boxes = await page.evaluate(() => window.__testRegions);
      const found = names.map((label,index) => ({ label,index,rect:boxes[index],kind:label.includes('печать')?'stamp':label.includes('подпись')?'signature':'candidate' }));
      const counts = { stamp: found.filter(object => object.kind === 'stamp').length, signature: found.filter(object => object.kind === 'signature').length };
      const report = { name, expected: truth.expected_counts, detected: counts, objects: [] };
      check(counts.stamp === truth.expected_counts.stamp && counts.signature === truth.expected_counts.signature && found.length === counts.stamp+counts.signature,
        `${name}: expected only ${JSON.stringify(truth.expected_counts)}, got ${JSON.stringify(counts)} (${found.length} total)`);
      if (!truth.objects.length) check(await page.locator('#saveAll').isDisabled(),`${name}: printed fragments must not be exportable as signatures`);
      const used = new Set();
      for (const object of truth.objects) {
        const target = objectBox(object);
        const candidate = found.filter(item => item.kind === object.kind && item.rect && !used.has(item.index))
          .sort((a,b) => overlap(b.rect,target)/(b.rect.w*b.rect.h)-overlap(a.rect,target)/(a.rect.w*a.rect.h))
          .find(item => overlap(item.rect,target) > target.w*target.h*.35);
        if (!candidate) { report.objects.push({ id: object.id, detected:false }); check(false,`${name}/${object.id}: missing`); continue; }
        used.add(candidate.index);
        await page.locator('.object-row > button:first-child').nth(candidate.index).click();
        const preview = await readPreview();
        const metrics = await measure(preview,candidate.rect,object,truth);
        metrics.rect = candidate.rect;
        const info = await page.locator('#previewInfo').textContent();
        const inferred = Number(info.match(/достроено (\d+) пикс/)?.[1] || 0);
        metrics.inferredPixels = inferred;
        report.objects.push(metrics);
        check(metrics.visibleInkRetained >= .85,`${name}/${object.id}: visible desired ink retained only ${metrics.visibleInkRetained}`);
        if (object.kind === 'stamp') check(metrics.visibleLetteringRetained >= .85,
          `${name}/${object.id}: own visible stamp lettering retained only ${metrics.visibleLetteringRetained}`);
        for (const [foreign,stats] of Object.entries(metrics.unwanted)) if (stats.total >= 40) {
          const limit = foreign.endsWith('.lettering') ? .03 : .15;
          check(stats.retainedFraction <= limit,`${name}/${object.id}: foreign ${foreign} retained ${stats.retainedFraction} (${stats.retained}/${stats.total}); limit ${limit}`);
        }
        const extracted = await exportOne(object.id);
        check(extracted.name.includes('_достроено') === (inferred > 0),`${extracted.name}: inferred suffix must describe actual added pixels`);
      }
      await page.screenshot({path:path.join(results,name+'-interface.png')});
      reports.push(report);
    }

    // The crossed signature gives independent known removed-line evidence.
    // Verify that repair really changes pixels, is reversible, is marked in the
    // filename, and can be highlighted without changing exported pixels.
    await open('failures-form-signature');
    await page.locator('#repair').uncheck();
    const without = await readPreview();
    check(!/достроено \d+ пикс/.test(await page.locator('#previewInfo').textContent()),'Repair off must not report inferred pixels');
    const offExport = await exportOne('repair-off');
    check(!offExport.name.includes('_достроено'),'Repair off export must not have inferred suffix');
    await page.locator('#repair').check();
    const withRepair = await readPreview();
    const inferred = Number((await page.locator('#previewInfo').textContent()).match(/достроено (\d+) пикс/)?.[1] || 0);
    const repairRect = await page.evaluate(() => window.__testRegions[0]);
    const repairTruth = JSON.parse(await fs.readFile(path.join(fixtures,'failures-form-signature.json'),'utf8'));
    const repairTarget = await png(path.join(fixtures,repairTruth.objects[0].mask));
    let added = 0, lost = 0, supported = 0;
    for (let i = 0; i < without.width*without.height; i++) {
      if (isInk(withRepair.data,i) && !isInk(without.data,i)) {
        added++;
        const x = repairRect.x+i%without.width, y = repairRect.y+Math.floor(i/without.width);
        let nearDesired = false;
        for (let yy=Math.max(0,y-1);yy<=Math.min(repairTarget.height-1,y+1)&&!nearDesired;yy++)
          for (let xx=Math.max(0,x-1);xx<=Math.min(repairTarget.width-1,x+1);xx++)
            if (hasMask(repairTarget,yy*repairTarget.width+xx)) { nearDesired = true; break; }
        if (nearDesired) supported++;
      }
      if (!isInk(withRepair.data,i) && isInk(without.data,i)) lost++;
    }
    check(inferred > 0 && added > 0,`Repair must add actual stroke pixels: reported=${inferred}, observed=${added}`);
    check(added > 0 && supported/added >= .85,`Repair added pixels outside the authored signature: supported=${supported}/${added}`);
    check(lost === 0,`Repair removed ${lost} existing ink pixels`);
    const onExport = await exportOne('repair-on');
    check(onExport.name.includes('_достроено'),'Actual repair export must have inferred suffix');
    await page.locator('#highlightRepairs').click();
    const highlighted = await readPreview();
    check(inspectExport(highlighted).orange > 0,'Highlight mode must visibly mark inferred strokes');
    const highlightedExport = await exportOne('repair-highlight');
    check(sha(onExport.image.data) === sha(highlightedExport.image.data), 'Highlight switch changed exported image pixels');
    await page.locator('#highlightRepairs').click();
    const normal = await readPreview();
    check(sha(Buffer.from(normal.data)) === sha(Buffer.from(withRepair.data)),'Turning highlight off must restore the exact normal preview');
    reports.push({ name:'repair-ui', inferred, added, lost, supportedByAuthoredStroke:supported, supportedFraction:ratio(supported,added) });
    for (const [source,before] of sourceHashes) check(sha(await fs.readFile(source)) === before,`${path.basename(source)}: source changed`);
    check(errors.length === 0,`Renderer errors: ${errors.join('; ')}`);
    check(requests.length === 0,`Unexpected online requests: ${requests.join('; ')}`);
    const reportFile = path.join(results,'failures-desktop-quality.json');
    await fs.writeFile(reportFile,JSON.stringify({packaged:Boolean(packagedExecutable),reports,failures,output},null,2)+'\n');
    console.log('Authored failure regression metrics:',reportFile);
    for (const failure of failures) console.error('FAIL:',failure);
    assert.deepEqual(failures,[],'Authored failure regressions remain; see quality report');
    console.log(`PASS: ${packagedExecutable?'packaged':'development'} Electron + real offline OCR, tilted stamp lettering, separate signatures, handwriting/text exclusion, actual optional stroke repair, no highlight in white-background PNG exports, source unchanged.`);
  } catch (error) {
    if (page) {
      console.error('Current document:',await page.locator('#sourceInfo').textContent());
      console.error('UI status:',await page.locator('#status').textContent());
      await page.screenshot({path:path.join(results,'failures-desktop-failure.png')});
    }
    throw error;
  } finally { await app.close(); }
})().catch(error => { console.error(error); process.exit(1); });
