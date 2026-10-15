import { MAX_PIXELS } from '../lib/processing.js';
const $ = id => document.getElementById(id);
const worker = new Worker(new URL('./processing-worker.js', import.meta.url), { type: 'module' });
const waiting = new Map(); let requestId = 0;
worker.onmessage = ({ data }) => {
  const item = waiting.get(data.id); if (!item) return; waiting.delete(data.id);
  if (data.ok) item.resolve(data.result); else item.reject(new Error(data.error));
};
worker.onerror = () => {
  for (const item of waiting.values()) item.reject(new Error('Обработка прервалась. Перезапустите программу и откройте изображение заново.'));
  waiting.clear(); status('Обработчик изображений недоступен. Перезапустите программу.', true);
};
function call(type, payload = {}, transfer = []) {
  return new Promise((resolve, reject) => { const id = ++requestId; waiting.set(id, { resolve, reject }); worker.postMessage({ id, type, payload }, transfer); });
}
const state = { source: null, regions: [], selected: null, next: 1, drawing: false, busy: false,
  tool: 'erase', zoom: 1, fit: 1, previewReady: false, previewRevision: 0, compare: false, stopExport: false };
let previewRunning = false, previewAgain = false, previewTimer, draft, stroke;
function status(text, error = false) { $('status').textContent = text; $('status').classList.toggle('error', error); $('status').title = text; }
function selected() { return state.regions.find(r => r.id === state.selected); }
function busy(value) { state.busy = value; listRegions(); }
function updateButtons() {
  const source = !!state.source, region = !!selected();
  for (const id of ['open','openEmpty']) $(id).disabled = state.busy;
  for (const id of ['detect','draw','zoomIn','zoomOut','fit','detectMode','gap']) $(id).disabled = !source || state.busy;
  $('saveAll').disabled = !state.regions.length || state.busy;
  $('saveAll').textContent = state.regions.length ? `Сохранить все PNG (${state.regions.length})` : 'Сохранить все PNG';
  $('duplicate').disabled = !region || state.busy || state.regions.length >= 80;
  $('compare').disabled = !region || state.busy;
  $('editor').disabled = !region || state.busy;
  $('undo').disabled = !selected()?.strokes.length;
  $('redo').disabled = !selected()?.redo.length;
  $('draw').setAttribute('aria-pressed', String(state.drawing));
  $('overlay').classList.toggle('drawing', state.drawing);
  $('count').textContent = state.regions.length;
  $('compare').setAttribute('aria-pressed', String(state.compare));
  $('compare').textContent = state.compare ? 'Показать результат' : 'Показать оригинал';
  $('erase').setAttribute('aria-pressed', String(state.tool === 'erase'));
  $('restore').setAttribute('aria-pressed', String(state.tool === 'restore'));
}
function listRegions() {
  $('objects').replaceChildren();
  if (!state.regions.length) { const p = document.createElement('p'); p.className = 'muted'; p.textContent = 'Нажмите «Найти области» или обведите нужный объект вручную.'; $('objects').append(p); }
  for (let i = 0; i < state.regions.length; i++) {
    const region = state.regions[i], row = document.createElement('div'); row.className = 'object-row' + (region.id === state.selected ? ' active' : '');
    const button = document.createElement('button'); button.textContent = `${i + 1}. ${region.name}`; button.disabled = state.busy;
    button.onclick = () => choose(region.id);
    const remove = document.createElement('button'); remove.textContent = '×'; remove.className = 'remove'; remove.disabled = state.busy;
    remove.title = 'Удалить область'; remove.setAttribute('aria-label', `Удалить ${region.name}`);
    remove.onclick = () => { state.regions = state.regions.filter(r => r.id !== region.id); choose(state.selected === region.id ? state.regions[0]?.id : state.selected); };
    row.append(button, remove); $('objects').append(row);
  }
  updateButtons();
}
function choose(id) {
  state.selected = id ?? null; state.compare = false; state.previewReady = false;
  const region = selected();
  if (region) {
    $('name').value = region.name; $('scale').value = region.scale;
    for (const key of ['mode','threshold','speckle','contrast']) $(key).value = region.options[key];
    updateValues(); schedulePreview();
  } else {
    state.previewRevision++; $('preview').hidden = true; $('previewEmpty').hidden = false; $('previewBusy').hidden = true;
    $('previewInfo').textContent = 'Белый непрозрачный фон · PNG';
  }
  listRegions(); drawOverlay();
}
function newRegion(rect, options) {
  if (state.regions.length >= 80) throw new Error('Можно сохранить до 80 объектов за один раз.');
  const x = Math.max(0, Math.floor(rect.x)), y = Math.max(0, Math.floor(rect.y));
  const region = { id: state.next++, rect: { x, y, w: Math.min(state.source.width - x, Math.ceil(rect.w)), h: Math.min(state.source.height - y, Math.ceil(rect.h)) },
    name: `объект_${String(state.next - 1).padStart(2,'0')}`, scale: 1,
    options: { mode: 'all', threshold: 18, speckle: 3, contrast: 1.2, ...options }, strokes: [], redo: [] };
  state.regions.push(region); return region;
}
function updateValues() { for (const key of ['threshold','speckle','contrast','brush','gap']) $(key+'Value').textContent = $(key).value; }
function params(region) { return { rect: region.rect, options: region.options, strokes: region.strokes, scale: region.scale }; }
function schedulePreview() {
  state.previewReady = false; state.previewRevision++; previewAgain = true; $('previewBusy').hidden = !selected();
  clearTimeout(previewTimer); previewTimer = setTimeout(pumpPreview, 90);
}
async function pumpPreview() {
  if (previewRunning || !previewAgain || !selected()) return;
  previewRunning = true; previewAgain = false;
  const revision = state.previewRevision, region = selected(), id = region.id, compare = state.compare;
  try {
    const payload = structuredClone(params(region));
    const result = compare ? await call('previewOriginal', payload) : await call('preview', payload);
    if (revision !== state.previewRevision || id !== state.selected) return;
    $('preview').width = result.width; $('preview').height = result.height;
    $('preview').getContext('2d').putImageData(new ImageData(new Uint8ClampedArray(result.buffer), result.width, result.height), 0, 0);
    $('preview').hidden = false; $('previewEmpty').hidden = true;
    $('previewInfo').textContent = compare ? 'Оригинальная область · кисти работают на результате' : `${region.rect.w} × ${region.rect.h} пикс. · ${result.inkPixels ? 'белый фон' : 'пусто — уменьшите удаление фона'}`;
    state.previewReady = !compare;
  } catch (error) { if (revision === state.previewRevision) status(error.message, true); }
  finally {
    previewRunning = false;
    if (revision === state.previewRevision) $('previewBusy').hidden = true;
    if (previewAgain) pumpPreview();
  }
}
function fitSource() {
  if (!state.source) return;
  state.fit = Math.min(($('viewport').clientWidth - 48) / state.source.displayWidth, ($('viewport').clientHeight - 48) / state.source.displayHeight, 1.7);
  state.fit = Math.max(.03, state.fit); applyZoom();
}
function applyZoom() {
  if (!state.source) return;
  const factor = state.fit * state.zoom, width = state.source.displayWidth * factor, height = state.source.displayHeight * factor;
  $('stage').style.width = width + 'px'; $('stage').style.height = height + 'px';
  for (const id of ['original','overlay']) { $(id).style.width = width + 'px'; $(id).style.height = height + 'px'; }
  $('zoomValue').textContent = Math.round(factor * state.source.displayWidth / state.source.width * 100) + '%';
}
function drawOverlay() {
  const canvas = $('overlay'), context = canvas.getContext('2d'); context.clearRect(0, 0, canvas.width, canvas.height);
  if (!state.source) return;
  const sx = canvas.width / state.source.width, sy = canvas.height / state.source.height;
  for (let i = 0; i < state.regions.length; i++) {
    const region = state.regions[i], rect = region.rect, active = region.id === state.selected;
    const x = rect.x * sx, y = rect.y * sy, w = rect.w * sx, h = rect.h * sy;
    context.fillStyle = active ? '#e89b1e19' : '#235bc010'; context.fillRect(x, y, w, h);
    context.strokeStyle = active ? '#d38b18' : '#3971ce'; context.lineWidth = active ? 3 : 2; context.strokeRect(x, y, w, h);
    context.fillStyle = context.strokeStyle; context.fillRect(x, y, 24, 20); context.fillStyle = 'white'; context.font = '12px sans-serif'; context.fillText(String(i+1), x+5, y+14);
  }
  if (draft) { context.strokeStyle = '#d38b18'; context.lineWidth = 2; context.setLineDash([6,4]); context.strokeRect(Math.min(draft.x,draft.endX)*sx,Math.min(draft.y,draft.endY)*sy,Math.abs(draft.endX-draft.x)*sx,Math.abs(draft.endY-draft.y)*sy); context.setLineDash([]); }
}
function pointOn(canvas, event, width, height) {
  const box = canvas.getBoundingClientRect();
  return { x: Math.max(0, Math.min(width-1, (event.clientX-box.left)/box.width*width)), y: Math.max(0, Math.min(height-1, (event.clientY-box.top)/box.height*height)) };
}
async function openFile(file) {
  if (!file || state.busy) return;
  if (!/\.(png|jpe?g|webp|bmp)$/i.test(file.name)) return status('Поддерживаются PNG, JPG, WEBP и BMP. Скан PDF/TIFF сначала сохраните как PNG или JPG.',true);
  if (file.size > 80 * 1024 * 1024) return status('Файл больше 80 МБ. Сохраните уменьшенную копию изображения.',true);
  busy(true); status('Открываю изображение…');
  let bitmap;
  try {
    bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
    const factor = Math.min(1, Math.sqrt(MAX_PIXELS / (bitmap.width * bitmap.height)), 16000 / Math.max(bitmap.width, bitmap.height));
    const width = Math.max(1, Math.floor(bitmap.width * factor)), height = Math.max(1, Math.floor(bitmap.height * factor));
    const full = new OffscreenCanvas(width,height), ctx = full.getContext('2d',{willReadFrequently:true});
    ctx.fillStyle = 'white'; ctx.fillRect(0,0,width,height); ctx.drawImage(bitmap,0,0,width,height); bitmap.close(); bitmap = undefined;
    const displayFactor = Math.min(1,1800/Math.max(width,height));
    const displayWidth = Math.max(1,Math.round(width*displayFactor)), displayHeight = Math.max(1,Math.round(height*displayFactor));
    // Invalidate any preview from the previous document before replacing worker pixels.
    state.previewRevision++; previewAgain = false; clearTimeout(previewTimer);
    for (const id of ['original','overlay']) { $(id).width = displayWidth; $(id).height = displayHeight; }
    $('original').getContext('2d').drawImage(full,0,0,displayWidth,displayHeight);
    const pixels = ctx.getImageData(0,0,width,height).data; full.width = 1;
    await call('init', { width, height, buffer:pixels.buffer }, [pixels.buffer]);
    state.source = { width,height,displayWidth,displayHeight,filename:file.name,stem:file.name.replace(/\.[^.]+$/,'') };
    state.regions = []; state.selected = null; state.next = 1; state.drawing = true; state.zoom = 1; draft = undefined;
    $('stage').hidden = false; $('empty').hidden = true; choose(null); fitSource(); document.querySelector('aside').scrollTop=0;
    $('sourceInfo').textContent = `${file.name} · ${width} × ${height}`; $('sourceInfo').title = $('sourceInfo').textContent;
    $('sourceHint').textContent = 'Обведите печать или подпись рамкой. Либо нажмите «Найти области».';
    status(factor < 1 ? 'Открыто. Большое изображение уменьшено для обработки; оригинал сохранён.' : 'Открыто. Выделите объекты вручную или запустите поиск.');
  } catch (error) { bitmap?.close(); status('Не удалось открыть изображение. Проверьте формат и целостность файла. '+error.message,true); }
  finally { busy(false); }
}
async function detect() {
  if (!state.source || state.busy) return;
  if (state.regions.length && !confirm('Заменить текущие области результатами автопоиска? Ручная очистка этих областей будет сброшена.')) return;
  busy(true); listRegions(); status('Ищу отдельные области…');
  try {
    const mode = $('detectMode').value;
    const result = await call('detect',{mode,gap:Number($('gap').value),threshold:18});
    state.regions = []; state.next = 1;
    for (const rect of result.regions) newRegion(rect,{mode});
    state.drawing = !state.regions.length; choose(state.regions[0]?.id);
    status(result.truncated ? 'Показаны первые 80 областей. Выберите отдельный фрагмент вручную.' : state.regions.length ? `Найдено областей: ${state.regions.length}. Проверьте рамки; лишние области удалите крестиком.` : 'Отдельные объекты не найдены. Обведите нужную область вручную или измените фильтр.');
  } catch (error) { status(error.message,true); }
  finally { busy(false); listRegions(); }
}
async function save(all) {
  if (!selected() || state.busy) return;
  if (!window.desktop) return status('Сохранение доступно в настольной программе.',true);
  const regions = structuredClone(all ? state.regions : [selected()]), stem = state.source.stem;
  let token, saved = 0; const errors = []; state.stopExport = false; busy(true); listRegions();
  try {
    const start = await window.desktop.beginExport();
    if (!start.ok) throw new Error(start.error);
    if (start.canceled) { status('Сохранение отменено.'); return; }
    token = start.token; $('cancelExport').hidden = false;
    for (const region of regions) {
      if (state.stopExport) break;
      status(`Сохраняю ${saved+errors.length+1} из ${regions.length}: ${region.name}…`);
      try {
        const result = await call('export',params(region));
        if (state.stopExport) break;
        const written = await window.desktop.writeImage(token,`${stem}_${region.name}`,new Uint8Array(result.buffer));
        if (!written.ok) throw new Error(written.error);
        saved++;
      } catch (error) { errors.push(`${region.name}: ${error.message}`); }
    }
    if (saved) $('openOutput').disabled = false;
    const summary = `Сохранено отдельных PNG: ${saved} из ${regions.length}.`;
    status(summary + (state.stopExport ? ' Сохранение остановлено.' : '') + (errors.length ? ' '+errors[0] : ''),!!errors.length);
    if (errors.length) alert(summary+'\n\nНе сохранены:\n'+errors.join('\n'));
  } catch (error) { status(error.message,true); }
  finally { if (token) await window.desktop.endExport(token).catch(()=>{}); $('cancelExport').hidden = true; busy(false); listRegions(); }
}

$('open').onclick = $('openEmpty').onclick = () => $('file').click();
$('file').onchange = () => { openFile($('file').files[0]); $('file').value = ''; };
window.addEventListener('dragover',event=>event.preventDefault());
window.addEventListener('drop',event=>{event.preventDefault();openFile(event.dataTransfer.files[0]);});
$('detect').onclick = detect;
$('draw').onclick = () => { state.drawing = !state.drawing; updateButtons(); status(state.drawing ? 'Протяните рамку вокруг одного объекта на исходном изображении.' : 'Нажмите на рамку, чтобы выбрать объект.'); };
$('duplicate').onclick = () => { const r=selected();if(!r)return;const copy=newRegion({...r.rect},{...r.options});copy.scale=r.scale;choose(copy.id);status('Область продублирована. Выберите отдельный цвет чернил.'); };
$('zoomIn').onclick = () => { state.zoom=Math.min(8,state.zoom*1.3);applyZoom(); };
$('zoomOut').onclick = () => { state.zoom=Math.max(.2,state.zoom/1.3);applyZoom(); };
$('fit').onclick = () => { state.zoom=1;fitSource(); };
new ResizeObserver(()=>fitSource()).observe($('viewport'));
$('gap').oninput = updateValues;
$('name').oninput = () => { if(selected()){selected().name=$('name').value;listRegions();} };
$('scale').onchange = () => { if(selected())selected().scale=Number($('scale').value); };
for (const key of ['mode','threshold','speckle','contrast']) $(key).addEventListener(key==='mode'?'change':'input',()=>{
  if(!selected())return; selected().options[key]=key==='mode'?$(key).value:Number($(key).value);state.compare=false;updateValues();updateButtons();schedulePreview();
});
$('brush').oninput = updateValues;
$('erase').onclick = () => { state.tool='erase';state.compare=false;updateButtons();schedulePreview(); };
$('restore').onclick = () => { state.tool='restore';state.compare=false;updateButtons();schedulePreview(); };
$('undo').onclick = () => { const r=selected();if(r?.strokes.length){r.redo.push(r.strokes.pop());state.compare=false;updateButtons();schedulePreview();} };
$('redo').onclick = () => { const r=selected();if(r?.redo.length){r.strokes.push(r.redo.pop());state.compare=false;updateButtons();schedulePreview();} };
$('compare').onclick = () => { state.compare=!state.compare;updateButtons();schedulePreview(); };
$('saveOne').onclick = () => save(false); $('saveAll').onclick = () => save(true);
$('cancelExport').onclick = () => {state.stopExport=true;status('Останавливаю после текущей операции…');};
$('openOutput').onclick = async () => { const result=await window.desktop.openOutput();if(!result.ok)status(result.error,true); };

$('overlay').onpointerdown = event => {
  if (!state.source||state.busy||event.button!==0)return;
  const p=pointOn($('overlay'),event,state.source.width,state.source.height);
  if(state.drawing){draft={...p,endX:p.x,endY:p.y};$('overlay').setPointerCapture(event.pointerId);}
  else {const r=[...state.regions].reverse().find(r=>p.x>=r.rect.x&&p.x<=r.rect.x+r.rect.w&&p.y>=r.rect.y&&p.y<=r.rect.y+r.rect.h);if(r)choose(r.id);}
};
$('overlay').onpointermove = event => {if(!draft)return;const p=pointOn($('overlay'),event,state.source.width,state.source.height);draft.endX=p.x;draft.endY=p.y;drawOverlay();};
$('overlay').onpointerup = () => {
  if(!draft)return;const box={x:Math.min(draft.x,draft.endX),y:Math.min(draft.y,draft.endY),w:Math.abs(draft.x-draft.endX),h:Math.abs(draft.y-draft.endY)};draft=undefined;
  try{if(box.w>=6&&box.h>=6){const r=newRegion(box);state.drawing=false;choose(r.id);status('Область добавлена. Очистите результат кистью или выделите следующую область.');}}catch(error){status(error.message,true);}drawOverlay();
};
$('overlay').onpointercancel = () => {draft=undefined;drawOverlay();};
$('preview').onpointerdown = event => {
  if(!selected()||state.busy||!state.previewReady||event.button!==0)return;
  const region=selected();if(region.strokes.length>=300)return status('Достигнут предел 300 движений кисти для одной области.',true);
  const p=pointOn($('preview'),event,region.rect.w,region.rect.h);
  stroke={tool:state.tool,radius:Number($('brush').value)/2,points:[p]};$('preview').setPointerCapture(event.pointerId);
};
$('preview').onpointermove = event => {
  if(!stroke||!selected())return;const region=selected(),p=pointOn($('preview'),event,region.rect.w,region.rect.h),previous=stroke.points.at(-1);
  if(Math.hypot(p.x-previous.x,p.y-previous.y)<Math.max(1,stroke.radius/3)||stroke.points.length>=5000)return;
  stroke.points.push(p);
  const canvas=$('preview'),ctx=canvas.getContext('2d'),sx=canvas.width/region.rect.w,sy=canvas.height/region.rect.h;
  ctx.strokeStyle=stroke.tool==='erase'?'white':'#3fa87c70';ctx.lineWidth=stroke.radius*2*sx;ctx.lineCap='round';ctx.beginPath();ctx.moveTo(previous.x*sx,previous.y*sy);ctx.lineTo(p.x*sx,p.y*sy);ctx.stroke();
};
function finishStroke(){if(!stroke||!selected())return;selected().strokes.push(stroke);selected().redo=[];stroke=undefined;updateButtons();schedulePreview();}
$('preview').onpointerup=finishStroke;$('preview').onpointercancel=finishStroke;
window.addEventListener('keydown',event=>{
  if(event.key==='Escape'){draft=undefined;state.drawing=false;drawOverlay();updateButtons();}
  if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='z'&&!['INPUT','SELECT','TEXTAREA'].includes(document.activeElement.tagName)){event.preventDefault();if(!state.busy)$(event.shiftKey?'redo':'undo').click();}
});
updateValues();updateButtons();
