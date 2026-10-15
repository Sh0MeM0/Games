// Real Electron + sandboxed preload + worker + filesystem export. Only the folder dialog is automated.
const { _electron } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '..'), results = path.join(root, 'test-results');
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
(async () => {
  await fs.mkdir(results, { recursive: true });
  const output = await fs.mkdtemp(path.join(results, 'export-'));
  const original = path.join(root, 'examples/synthetic.png'), before = hash(await fs.readFile(original));
  const args = process.platform === 'linux' ? ['--no-sandbox','--ozone-platform=headless','--disable-gpu',root] : [root];
  const application = await _electron.launch({ executablePath: require('electron'), args, cwd: root, timeout: 30_000 });
  let page;
  try {
    await application.evaluate(({ dialog }, directory) => { dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [directory] }); }, output);
    page = await application.firstWindow(); const errors = [];
    page.on('pageerror',error=>errors.push(error.message));
    page.on('dialog',dialog=>dialog.accept());
    await page.waitForLoadState('domcontentloaded');
    await page.setViewportSize({width:1440,height:960});
    assert.equal(await page.title(),'Печати и подписи');
    assert.equal(await page.evaluate(()=>typeof window.require),'undefined');
    assert.equal(await page.evaluate(()=>typeof window.desktop.beginExport),'function');
    await page.locator('#file').setInputFiles(original);
    await page.waitForFunction(()=>document.getElementById('sourceInfo').textContent.includes('1200 × 800'));
    await page.waitForFunction(()=>!document.getElementById('detect').disabled, null, {timeout:120_000});
    assert.equal(await page.locator('#count').textContent(),'4');
    const ready = () => page.waitForFunction(()=>!document.getElementById('preview').hidden&&document.getElementById('previewBusy').hidden);
    await ready();
    const inkCount = () => page.locator('#preview').evaluate(canvas=>{
      const bytes=canvas.getContext('2d').getImageData(0,0,canvas.width,canvas.height).data;let n=0;
      for(let i=0;i<bytes.length;i+=4)if(Math.min(bytes[i],bytes[i+1],bytes[i+2])<245)n++;return n;
    });
    const initialMode = await page.locator('#mode').inputValue();
    const initial = await inkCount(); assert.ok(initial>500);
    await page.locator('#mode').selectOption('red');await ready();
    assert.ok((await inkCount())<initial/10);
    await page.locator('#mode').selectOption(initialMode);await ready();
    await page.locator('#compare').click();await ready();
    assert.ok((await inkCount())>initial);
    await page.locator('#compare').click();await ready();
    const preview = await page.locator('#preview').boundingBox();
    await page.mouse.click(preview.x+preview.width/2,preview.y+preview.height/2);
    await ready();const erased=await inkCount();assert.ok(erased<initial);
    await page.locator('#undo').click();await ready();assert.equal(await inkCount(),initial);
    await page.locator('#redo').click();await ready();assert.equal(await inkCount(),erased);
    await page.locator('#undo').click();await ready();
    await page.locator('#name').fill('Синяя печать');
    await page.locator('#duplicate').click();
    assert.equal(await page.locator('#count').textContent(),'5');
    await page.locator('.object-row').last().locator('.remove').click();
    await ready();
    await page.locator('#draw').click();
    const overlay=await page.locator('#overlay').boundingBox();
    await page.mouse.move(overlay.x+95/1200*overlay.width,overlay.y+510/800*overlay.height);
    await page.mouse.down();await page.mouse.move(overlay.x+440/1200*overlay.width,overlay.y+665/800*overlay.height,{steps:8});await page.mouse.up();
    assert.equal(await page.locator('#count').textContent(),'5');await ready();
    await page.locator('.object-row').last().locator('.remove').click();await ready();
    await page.screenshot({path:path.join(results,'interface.png')});
    await page.locator('#saveAll').click();
    await page.waitForFunction(()=>document.getElementById('status').textContent.includes('Сохранено отдельных PNG: 4 из 4.'));
    let filenames=(await fs.readdir(output)).filter(p=>p.endsWith('.png'));assert.equal(filenames.length,4);
    assert.ok(filenames.some(p=>p.includes('Синяя печать')));
    const dimensions=await application.evaluate(({nativeImage},files)=>files.map(filename=>{
      const im=nativeImage.createFromPath(filename),size=im.getSize(),rgba=im.getBitmap();let opaque=true,whiteEdges=true;
      for(let p=3;p<rgba.length;p+=4)if(rgba[p]!==255)opaque=false;
      for(let x=0;x<size.width;x++)for(const y of [0,size.height-1])for(let c=0;c<4;c++)if(rgba[(y*size.width+x)*4+c]!==255)whiteEdges=false;
      return {...size,opaque,whiteEdges};
    }),filenames.map(p=>path.join(output,p)));
    assert.ok(dimensions.every(r=>r.width>30&&r.height>30&&r.opaque&&r.whiteEdges));
    // A doubled export must change dimensions and retain every previous file.
    const firstDimensions=dimensions[filenames.findIndex(p=>p.includes('Синяя печать'))];
    await page.locator('#scale').selectOption('2');
    await page.locator('#saveOne').click();
    await page.waitForFunction(()=>document.getElementById('status').textContent.includes('Сохранено отдельных PNG: 1 из 1.'));
    const after=(await fs.readdir(output)).filter(p=>p.endsWith('.png'));assert.equal(after.length,5);
    const doubled=await application.evaluate(({nativeImage},filename)=>nativeImage.createFromPath(filename).getSize(),path.join(output,after.find(p=>!filenames.includes(p))));
    assert.equal(doubled.width,firstDimensions.width*2);assert.equal(doubled.height,firstDimensions.height*2);
    // Actual JPEG decode, including lossy noise and a smooth gray shadow.
    await page.locator('#file').setInputFiles(path.join(root,'examples/synthetic-photo.jpg'));
    await page.waitForFunction(()=>document.getElementById('sourceInfo').textContent.includes('synthetic-photo.jpg'));
    await page.waitForFunction(()=>!document.getElementById('detect').disabled, null, {timeout:120_000});
    assert.equal(await page.locator('#count').textContent(),'4');await ready();
    assert.ok(await inkCount()>500);
    await page.locator('#saveAll').click();
    await page.waitForFunction(()=>document.getElementById('status').textContent.includes('Сохранено отдельных PNG: 4 из 4.'));
    assert.equal((await fs.readdir(output)).filter(p=>p.endsWith('.png')).length,9);
    assert.equal(hash(await fs.readFile(original)),before);
    assert.deepEqual(errors,[]);
    console.log('PASS: real Electron, local module worker, four crops from PNG/JPEG, color filter, original comparison, brush/undo/redo, manual/duplicate frames, 9 PNG files, opaque white edges, no overwrite, original unchanged.');
    console.log('Screenshot and exported images:',results);
  } catch (error) {
    if(page){console.error('UI status:',await page.locator('#status').textContent());await page.screenshot({path:path.join(results,'failure.png')});}
    throw error;
  } finally { await application.close(); }
})().catch(error=>{console.error(error);process.exit(1);});
