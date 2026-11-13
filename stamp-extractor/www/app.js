import { openDocument } from './documents.js';
import { initPlatform, isNative, chooseOutput, writePng, openOutput, hasOutputFolder } from './platform.js';

const $ = id => document.getElementById(id);
const worker = new Worker(new URL('./worker.js', import.meta.url), { type: 'module' });
const waiting = new Map(); let requestId = 0;
worker.onmessage = ({ data }) => {
  const item = waiting.get(data.id); if (!item) return; waiting.delete(data.id);
  if (data.ok) item.resolve(data.result); else item.reject(new Error(data.error));
};
worker.onerror = () => {
  for (const item of waiting.values()) item.reject(new Error('Обработка прервалась. Перезапустите программу и откройте файл заново.'));
  waiting.clear(); status('Обработчик изображений недоступен. Перезапустите программу.', true);
};
function call(type, payload = {}, transfer = []) {
  return new Promise((resolve, reject) => { const id = ++requestId; waiting.set(id, { resolve, reject }); worker.postMessage({ id, type, payload }, transfer); });
}

const KIND_NAMES = { stamp: 'печать', signature: 'подпись', handwriting: 'рукопись', candidate: 'область' };
const state = { doc: null, docId: 0, page: 0, pages: [], source: null, selected: null, drawing: false, busy: false,
  tool: 'erase', zoom: 1, fit: 1, previewReady: false, previewRevision: 0, compare: false, stopExport: false, highlightRepairs: false };
let previewRunning = false, previewAgain = false, previewTimer, draft, stroke;
const current = () => state.pages[state.page];
const regions = () => current()?.regions || [];
function status(text, error = false) { $('status').textContent = text; $('status').classList.toggle('error', error); $('status').title = text; }
function selected() { return regions().find(r => r.id === state.selected); }
function busy(value) { state.busy = value; listRegions(); }
function totalObjects() { return state.pages.reduce((n, p) => n + (p?.regions.length || 0), 0); }

function updateButtons() {
  const source = !!state.source, region = !!selected(), pdf = state.doc?.pageCount > 1;
  for (const id of ['open', 'openEmpty']) $(id).disabled = state.busy;
  for (const id of ['detect', 'draw', 'zoomIn', 'zoomOut', 'fit', 'detectMode', 'sensitivityMode']) $(id).disabled = !source || state.busy;
  $('pager').hidden = !pdf;
  $('allPages').hidden = !pdf;
  if (pdf) {
    $('prevPage').disabled = state.busy || state.page === 0;
    $('nextPage').disabled = state.busy || state.page >= state.doc.pageCount - 1;
    $('allPages').disabled = state.busy;
    $('pageValue').textContent = `${state.page + 1} / ${state.doc.pageCount}`;
  }
  const total = totalObjects();
  $('saveAll').disabled = !total || state.busy;
  $('saveAll').textContent = total ? `Сохранить все PNG (${total})` : 'Сохранить все PNG';
  $('duplicate').disabled = !region || state.busy || regions().length >= 80;
  $('compare').disabled = !region || state.busy;
  $('highlightRepairs').disabled = !region || state.busy;
  $('highlightRepairs').setAttribute('aria-pressed', String(state.highlightRepairs));
  $('stampLayerLabel').hidden = selected()?.options.kind !== 'stamp';
  $('editor').disabled = !region || state.busy;
  $('undo').disabled = !selected()?.strokes.length;
  $('redo').disabled = !selected()?.redo.length;
  $('draw').setAttribute('aria-pressed', String(state.drawing));
  $('overlay').classList.toggle('drawing', state.drawing);
  $('count').textContent = regions().length;
  $('compare').setAttribute('aria-pressed', String(state.compare));
  $('compare').textContent = state.compare ? 'Показать результат' : 'Показать оригинал';
  $('erase').setAttribute('aria-pressed', String(state.tool === 'erase'));
  $('restore').setAttribute('aria-pressed', String(state.tool === 'restore'));
  $('openOutput').disabled = !hasOutputFolder();
  $('openOutput').hidden = !isNative;
}
function summary() {
  const list = regions(), count = kind => list.filter(r => r.options.kind === kind).length;
  const other = list.length - count('stamp') - count('signature');
  return `Печати: ${count('stamp')} · Подписи: ${count('signature')}` + (other ? ` · Другое: ${other}` : '');
}
function listRegions() {
  $('objects').replaceChildren();
  if (state.source && !state.busy && current()?.analyzed) $('analysisInfo').textContent = summary();
  if (!regions().length) {
    const p = document.createElement('p'); p.className = 'muted';
    p.textContent = state.source ? 'Объекты не выделены. Можно выделить вручную.' : 'Откройте документ: печати и подписи будут выделены автоматически.';
    $('objects').append(p);
  }
  regions().forEach((region, i) => {
    const row = document.createElement('div'); row.className = 'object-row' + (region.id === state.selected ? ' active' : '');
    const button = document.createElement('button'); button.disabled = state.busy; button.onclick = () => choose(region.id);
    const name = document.createElement('span'); name.textContent = `${i + 1}. ${region.name}`;
    button.append(name);
    if (region.confidence) { const c = document.createElement('small'); c.textContent = `${Math.round(region.confidence * 100)}%`; c.title = 'Средняя уверенность модели'; button.append(c); }
    const remove = document.createElement('button'); remove.textContent = '×'; remove.className = 'remove'; remove.disabled = state.busy;
    remove.title = 'Удалить область'; remove.setAttribute('aria-label', `Удалить ${region.name}`);
    remove.onclick = () => { current().regions = regions().filter(r => r.id !== region.id); choose(state.selected === region.id ? regions()[0]?.id : state.selected); };
    row.append(button, remove); $('objects').append(row);
  });
  updateButtons();
}
const EDIT_VALUES = ['kind', 'mode', 'threshold', 'speckle', 'contrast', 'stampLayer', 'repairGap', 'sensitivity'];
const EDIT_CHECKS = ['removeText', 'removeLines', 'isolate', 'repair'];
function choose(id) {
  state.selected = id ?? null; state.compare = false; state.previewReady = false;
  const region = selected();
  if (region) {
    $('name').value = region.name; $('scale').value = region.scale;
    for (const key of EDIT_VALUES) $(key).value = region.options[key];
    for (const key of EDIT_CHECKS) $(key).checked = region.options[key];
    updateValues(); schedulePreview();
  } else {
    state.previewRevision++; $('preview').hidden = true; $('previewEmpty').hidden = false; $('previewBusy').hidden = true;
    $('previewInfo').textContent = 'Белый непрозрачный фон · PNG';
  }
  listRegions(); drawOverlay();
}
function newRegion(rect, options = {}) {
  const page = current();
  if (page.regions.length >= 80) throw new Error('На странице можно сохранить до 80 объектов.');
  const x = Math.max(0, Math.floor(rect.x)), y = Math.max(0, Math.floor(rect.y)), kind = rect.kind || 'candidate';
  const number = String(page.next).padStart(2, '0');
  const width = page.width ?? state.source.width, height = page.height ?? state.source.height;
  const region = { id: page.next++, rect: { x, y, w: Math.min(width - x, Math.ceil(rect.w)), h: Math.min(height - y, Math.ceil(rect.h)) },
    name: `${KIND_NAMES[kind] || 'область'}_${number}`, scale: 1, confidence: rect.confidence || 0,
    options: { objectKey: rect.key, kind, anyObjectInk: !!rect.anyObjectInk, mode: 'all', stampLayer: 'printed', isolate: true, repair: false, repairGap: 12,
      removeText: true, removeLines: true, threshold: 16, speckle: 3, contrast: 1.1, sensitivity: Number($('sensitivityMode').value), ...options },
    strokes: [], redo: [] };
  page.regions.push(region); return region;
}
function updateValues() { for (const key of ['threshold', 'speckle', 'contrast', 'brush', 'repairGap', 'sensitivity']) $(key + 'Value').textContent = $(key).value; }
function params(region) { return { rect: region.rect, options: region.options, strokes: region.strokes, scale: region.scale, highlightRepairs: state.highlightRepairs }; }
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
    const result = await call(compare ? 'previewOriginal' : 'preview', payload);
    if (revision !== state.previewRevision || id !== state.selected) return;
    $('preview').width = result.width; $('preview').height = result.height;
    $('preview').getContext('2d').putImageData(new ImageData(new Uint8ClampedArray(result.buffer), result.width, result.height), 0, 0);
    $('preview').hidden = false; $('previewEmpty').hidden = true;
    $('previewInfo').textContent = compare ? 'Исходная область · кисти работают на результате' :
      `${region.rect.w} × ${region.rect.h} пикс. · ${result.inkPixels ? 'белый фон' : 'пусто — снизьте порог уверенности или удаление фона'}` +
      (result.inferredPixels ? ` · достроено ${result.inferredPixels} пикс. (предположение)` : '') +
      (result.uncertainPixels ? ` · пересечения: ${result.uncertainPixels} пикс., проверьте` : '');
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
  for (const id of ['original', 'overlay']) { $(id).style.width = width + 'px'; $(id).style.height = height + 'px'; }
  $('zoomValue').textContent = Math.round(factor * state.source.displayWidth / state.source.width * 100) + '%';
}
const COLORS = { stamp: '#3971ce', signature: '#1f9d6b', handwriting: '#8a5cc2', candidate: '#6b7380' };
function drawOverlay() {
  const canvas = $('overlay'), context = canvas.getContext('2d'); context.clearRect(0, 0, canvas.width, canvas.height);
  if (!state.source) return;
  const sx = canvas.width / state.source.width, sy = canvas.height / state.source.height;
  regions().forEach((region, i) => {
    const rect = region.rect, active = region.id === state.selected, color = COLORS[region.options.kind] || COLORS.candidate;
    const x = rect.x * sx, y = rect.y * sy, w = rect.w * sx, h = rect.h * sy;
    context.fillStyle = active ? '#e89b1e19' : color + '12'; context.fillRect(x, y, w, h);
    context.strokeStyle = active ? '#d38b18' : color; context.lineWidth = active ? 3 : 2; context.strokeRect(x, y, w, h);
    context.fillStyle = context.strokeStyle; context.fillRect(x, y, 24, 20); context.fillStyle = 'white'; context.font = '12px sans-serif'; context.fillText(String(i + 1), x + 5, y + 14);
  });
  if (draft) { context.strokeStyle = '#d38b18'; context.lineWidth = 2; context.setLineDash([6, 4]); context.strokeRect(Math.min(draft.x, draft.endX) * sx, Math.min(draft.y, draft.endY) * sy, Math.abs(draft.endX - draft.x) * sx, Math.abs(draft.endY - draft.y) * sy); context.setLineDash([]); }
}
function pointOn(canvas, event, width, height) {
  const box = canvas.getBoundingClientRect();
  return { x: Math.max(0, Math.min(width - 1, (event.clientX - box.left) / box.width * width)), y: Math.max(0, Math.min(height - 1, (event.clientY - box.top) / box.height * height)) };
}

/** Renders a page, sends pixels to the worker and shows it. */
async function loadPage(index, { show = true, ensureMaps = false } = {}) {
  const doc = state.doc, pixels = await doc.renderPage(index, Number($('pdfDpi').value));
  const page = state.pages[index] ||= { regions: [], next: 1, analyzed: false };
  page.width = pixels.width; page.height = pixels.height;
  if (show) {
    const displayFactor = Math.min(1, 1800 / Math.max(pixels.width, pixels.height));
    const displayWidth = Math.max(1, Math.round(pixels.width * displayFactor)), displayHeight = Math.max(1, Math.round(pixels.height * displayFactor));
    const full = new OffscreenCanvas(pixels.width, pixels.height);
    full.getContext('2d').putImageData(new ImageData(pixels.data, pixels.width, pixels.height), 0, 0);
    for (const id of ['original', 'overlay']) { $(id).width = displayWidth; $(id).height = displayHeight; }
    const ctx = $('original').getContext('2d'); ctx.imageSmoothingQuality = 'high'; ctx.drawImage(full, 0, 0, displayWidth, displayHeight); full.width = 1;
    state.source = { width: pixels.width, height: pixels.height, displayWidth, displayHeight, reduced: pixels.reduced };
  }
  state.previewRevision++; previewAgain = false; clearTimeout(previewTimer);
  await call('init', { key: `${state.docId}:${index}:${$('pdfDpi').value}`, width: pixels.width, height: pixels.height, buffer: pixels.data.buffer, ensureMaps }, [pixels.data.buffer]);
  return page;
}
async function analyzePage(page) {
  const result = await call('analyze', { mode: $('detectMode').value, sensitivity: Number($('sensitivityMode').value) });
  page.regions = []; page.next = 1; page.analyzed = true;
  const saved = state.page; state.page = state.pages.indexOf(page);
  for (const rect of result.regions) newRegion(rect);
  state.page = saved;
  return result;
}
function pageLabel(index) { return state.doc.pageCount > 1 ? `страница ${index + 1}: ` : ''; }
async function showPage(index) {
  state.page = index; state.selected = null; state.drawing = false; draft = undefined; state.zoom = 1;
  const page = await loadPage(index);
  state.page = index;
  $('stage').hidden = false; $('empty').hidden = true; fitSource();
  $('sourceInfo').textContent = `${state.doc.name}${state.doc.pageCount > 1 ? ` · стр. ${index + 1} из ${state.doc.pageCount}` : ''} · ${state.source.width} × ${state.source.height}` +
    (state.source.reduced ? ' · уменьшено до 24 Мп' : '');
  $('sourceInfo').title = $('sourceInfo').textContent;
  if (!page.analyzed) {
    $('analysisInfo').textContent = 'Анализ модели…';
    status(`Ищу печати и подписи${state.doc.pageCount > 1 ? ` на странице ${index + 1}` : ''}…`);
    const result = await analyzePage(page);
    report(result, index);
  } else status(`${pageLabel(index)}${summary()}.`);
  choose(page.regions[0]?.id);
}
function report(result, index) {
  const page = state.pages[index];
  const time = result.modelMs ? ` Модель: ${(result.modelMs / 1000).toFixed(1)} с.` : '';
  status(result.truncated ? 'Найдено больше 80 объектов. Показаны первые 80; проверьте результат.' : page.regions.length ?
    `${pageLabel(index)}выделено: ${page.regions.length}. Проверьте результат и сохраните PNG.${time}` :
    `${pageLabel(index)}печати и подписи не найдены. Можно выделить объект вручную или выбрать «Мягче».${time}`);
}
async function openFile(file) {
  if (!file || state.busy) return;
  busy(true); status('Открываю файл…');
  try {
    const doc = await openDocument(file);
    try { state.doc?.close(); } catch { /* the previous document is released anyway */ }
    state.doc = Object.assign(doc, { name: file.name || 'вставка.png', stem: (file.name || 'вставка').replace(/\.[^.]+$/, '') });
    state.docId++; state.pages = []; state.page = 0;
    $('pdfDpiLabel').hidden = doc.kind !== 'pdf';
    document.querySelector('aside').scrollTop = 0;
    await showPage(0);
  } catch (error) { status('Не удалось открыть файл. ' + error.message, true); }
  finally { busy(false); }
}
async function goTo(index) {
  if (!state.doc || state.busy || index < 0 || index >= state.doc.pageCount) return;
  busy(true);
  try { await showPage(index); } catch (error) { status(error.message, true); }
  finally { busy(false); }
}
async function analyzeAllPages() {
  if (!state.doc || state.busy) return;
  busy(true); state.stopExport = false; $('cancelExport').hidden = false;
  const start = state.page;
  try {
    for (let i = 0; i < state.doc.pageCount && !state.stopExport; i++) {
      if (state.pages[i]?.analyzed) continue;
      status(`Анализ страницы ${i + 1} из ${state.doc.pageCount}…`);
      const page = await loadPage(i, { show: false });
      await analyzePage(page);
    }
    const found = state.pages.filter(p => p?.regions.length).length;
    await showPage(start);
    status(`Проверено страниц: ${state.pages.filter(p => p?.analyzed).length} из ${state.doc.pageCount}. Объектов: ${totalObjects()} на ${found} стр.` +
      (state.stopExport ? ' Остановлено.' : ''));
  } catch (error) { status(error.message, true); }
  finally { $('cancelExport').hidden = true; busy(false); }
}
async function detect() {
  if (!state.source || state.busy) return;
  if (regions().length && !confirm('Повторить поиск на этой странице? Ручные изменения объектов будут сброшены.')) return;
  busy(true);
  try { status('Повторяю поиск…'); const result = await analyzePage(current()); report(result, state.page); choose(current().regions[0]?.id); }
  catch (error) { status(error.message, true); }
  finally { busy(false); }
}
async function save(all) {
  if (state.busy || (!all && !selected())) return;
  const jobs = [];
  if (all) state.pages.forEach((page, index) => page?.regions.forEach(region => jobs.push({ index, region: structuredClone(region) })));
  else jobs.push({ index: state.page, region: structuredClone(selected()) });
  let saved = 0; const errors = []; state.stopExport = false; busy(true);
  const start = state.page, multi = state.doc.pageCount > 1;
  try {
    const target = await chooseOutput();
    if (target.canceled) { status('Сохранение отменено.'); return; }
    $('cancelExport').hidden = false;
    let loaded = state.page;
    for (const { index, region } of jobs) {
      if (state.stopExport) break;
      status(`Сохраняю ${saved + errors.length + 1} из ${jobs.length}: ${region.name}…`);
      try {
        if (index !== loaded) { await loadPage(index, { show: false, ensureMaps: true }); loaded = index; }
        const result = await call('export', params(region));
        if (state.stopExport) break;
        const stem = `${state.doc.stem}${multi ? `_стр${String(index + 1).padStart(2, '0')}` : ''}_${region.name}${result.inferredPixels ? '_достроено' : ''}`;
        await writePng(target, stem, new Uint8Array(result.buffer));
        saved++;
      } catch (error) { errors.push(`${region.name}: ${error.message}`); }
    }
    if (loaded !== start) await loadPage(start, { show: false, ensureMaps: true });
    const text = `Сохранено отдельных PNG: ${saved} из ${jobs.length}.`;
    status(text + (state.stopExport ? ' Сохранение остановлено.' : '') + (errors.length ? ' ' + errors[0] : ''), !!errors.length);
    if (errors.length) alert(text + '\n\nНе сохранены:\n' + errors.join('\n'));
  } catch (error) { status(error.message, true); }
  finally { $('cancelExport').hidden = true; busy(false); }
}

$('open').onclick = $('openEmpty').onclick = () => $('file').click();
$('file').onchange = () => { openFile($('file').files[0]); $('file').value = ''; };
window.addEventListener('dragover', event => event.preventDefault());
window.addEventListener('drop', event => { event.preventDefault(); openFile(event.dataTransfer.files[0]); });
window.addEventListener('paste', event => {
  const active = document.activeElement;
  if (active?.tagName === 'TEXTAREA' || (active?.tagName === 'INPUT' && active.type === 'text')) return;
  const item = [...(event.clipboardData?.items || [])].find(i => i.type.startsWith('image/'));
  const file = item?.getAsFile();
  if (file) { event.preventDefault(); openFile(new File([file], 'вставка.png', { type: file.type })); }
});
$('detect').onclick = detect;
$('allPages').onclick = analyzeAllPages;
$('prevPage').onclick = () => goTo(state.page - 1);
$('nextPage').onclick = () => goTo(state.page + 1);
$('pdfDpi').onchange = () => { if (state.doc?.kind === 'pdf' && !state.busy) { state.pages = []; goTo(state.page); } };
$('draw').onclick = () => { state.drawing = !state.drawing; updateButtons(); status(state.drawing ? 'Протяните рамку вокруг одного объекта на исходном изображении.' : 'Нажмите на рамку, чтобы выбрать объект.'); };
$('duplicate').onclick = () => { const r = selected(); if (!r) return; const copy = newRegion({ ...r.rect }, { ...r.options }); copy.name = r.name + '_копия'; copy.scale = r.scale; choose(copy.id); status('Область продублирована.'); };
$('zoomIn').onclick = () => { state.zoom = Math.min(8, state.zoom * 1.3); applyZoom(); };
$('zoomOut').onclick = () => { state.zoom = Math.max(.2, state.zoom / 1.3); applyZoom(); };
$('fit').onclick = () => { state.zoom = 1; fitSource(); };
new ResizeObserver(() => fitSource()).observe($('viewport'));
$('name').oninput = () => { if (selected()) { selected().name = $('name').value; listRegions(); } };
$('scale').onchange = () => { if (selected()) selected().scale = Number($('scale').value); };
const SELECTS = ['mode', 'kind', 'stampLayer'];
for (const key of EDIT_VALUES) $(key).addEventListener(SELECTS.includes(key) ? 'change' : 'input', () => {
  if (!selected()) return;
  selected().options[key] = SELECTS.includes(key) ? $(key).value : Number($(key).value);
  state.compare = false; updateValues(); listRegions(); schedulePreview();
});
for (const key of EDIT_CHECKS) $(key).onchange = () => { if (!selected()) return; selected().options[key] = $(key).checked; state.compare = false; updateButtons(); schedulePreview(); };
$('brush').oninput = updateValues;
$('erase').onclick = () => { state.tool = 'erase'; state.compare = false; updateButtons(); schedulePreview(); };
$('restore').onclick = () => { state.tool = 'restore'; state.compare = false; updateButtons(); schedulePreview(); };
$('undo').onclick = () => { const r = selected(); if (r?.strokes.length) { r.redo.push(r.strokes.pop()); state.compare = false; updateButtons(); schedulePreview(); } };
$('redo').onclick = () => { const r = selected(); if (r?.redo.length) { r.strokes.push(r.redo.pop()); state.compare = false; updateButtons(); schedulePreview(); } };
$('highlightRepairs').onclick = () => { state.highlightRepairs = !state.highlightRepairs; state.compare = false; updateButtons(); schedulePreview(); };
$('compare').onclick = () => { state.compare = !state.compare; updateButtons(); schedulePreview(); };
$('saveOne').onclick = () => save(false); $('saveAll').onclick = () => save(true);
$('cancelExport').onclick = () => { state.stopExport = true; status('Останавливаю после текущей операции…'); };
$('openOutput').onclick = async () => { try { await openOutput(); } catch (error) { status(error.message, true); } };

$('overlay').onpointerdown = event => {
  if (!state.source || state.busy || event.button !== 0) return;
  const p = pointOn($('overlay'), event, state.source.width, state.source.height);
  if (state.drawing) { draft = { ...p, endX: p.x, endY: p.y }; $('overlay').setPointerCapture(event.pointerId); }
  else { const r = [...regions()].reverse().find(r => p.x >= r.rect.x && p.x <= r.rect.x + r.rect.w && p.y >= r.rect.y && p.y <= r.rect.y + r.rect.h); if (r) choose(r.id); }
};
$('overlay').onpointermove = event => { if (!draft) return; const p = pointOn($('overlay'), event, state.source.width, state.source.height); draft.endX = p.x; draft.endY = p.y; drawOverlay(); };
$('overlay').onpointerup = () => {
  if (!draft) return; const box = { x: Math.min(draft.x, draft.endX), y: Math.min(draft.y, draft.endY), w: Math.abs(draft.x - draft.endX), h: Math.abs(draft.y - draft.endY) }; draft = undefined;
  try { if (box.w >= 6 && box.h >= 6) { const r = newRegion(box); state.drawing = false; choose(r.id); status('Область добавлена. Выберите тип объекта или очистите результат кистью.'); } } catch (error) { status(error.message, true); } drawOverlay();
};
$('overlay').onpointercancel = () => { draft = undefined; drawOverlay(); };
$('preview').onpointerdown = event => {
  if (!selected() || state.busy || !state.previewReady || event.button !== 0) return;
  const region = selected(); if (region.strokes.length >= 300) return status('Достигнут предел 300 движений кисти для одной области.', true);
  const p = pointOn($('preview'), event, region.rect.w, region.rect.h);
  stroke = { tool: state.tool, radius: Number($('brush').value) / 2, points: [p] }; $('preview').setPointerCapture(event.pointerId);
};
$('preview').onpointermove = event => {
  if (!stroke || !selected()) return; const region = selected(), p = pointOn($('preview'), event, region.rect.w, region.rect.h), previous = stroke.points.at(-1);
  if (Math.hypot(p.x - previous.x, p.y - previous.y) < Math.max(1, stroke.radius / 3) || stroke.points.length >= 5000) return;
  stroke.points.push(p);
  const canvas = $('preview'), ctx = canvas.getContext('2d'), sx = canvas.width / region.rect.w, sy = canvas.height / region.rect.h;
  ctx.strokeStyle = stroke.tool === 'erase' ? 'white' : '#3fa87c70'; ctx.lineWidth = stroke.radius * 2 * sx; ctx.lineCap = 'round'; ctx.beginPath(); ctx.moveTo(previous.x * sx, previous.y * sy); ctx.lineTo(p.x * sx, p.y * sy); ctx.stroke();
};
function finishStroke() { if (!stroke || !selected()) return; selected().strokes.push(stroke); selected().redo = []; stroke = undefined; updateButtons(); schedulePreview(); }
$('preview').onpointerup = finishStroke; $('preview').onpointercancel = finishStroke;
window.addEventListener('keydown', event => {
  const typing = ['INPUT', 'SELECT', 'TEXTAREA'].includes(document.activeElement.tagName);
  if (event.key === 'Escape') { draft = undefined; state.drawing = false; drawOverlay(); updateButtons(); }
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z' && !typing) { event.preventDefault(); if (!state.busy) $(event.shiftKey ? 'redo' : 'undo').click(); }
  if (!typing && state.doc?.pageCount > 1 && (event.key === 'PageDown' || event.key === 'PageUp')) { event.preventDefault(); goTo(state.page + (event.key === 'PageDown' ? 1 : -1)); }
});

updateValues(); updateButtons();
initPlatform().catch(() => {});
call('warmup').then(() => { if (!state.source) status('Готово к работе. Модель загружена.'); })
  .catch(error => status('Не удалось загрузить встроенную модель: ' + error.message, true));
