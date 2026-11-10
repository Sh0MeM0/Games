/* Таксация Лайт — веб-интерфейс. Расчёты: core/engine.js (TaksEngine), справочники: TAKS_REF. */
(function () {
  'use strict';
  var E = window.TaksEngine, REF = window.TAKS_REF;
  var STORE_KEY = 'taksaciya-lite-project-v1';
  var COND = ['хор.', 'уд.', 'плох.', 'ненадл.'];
  var FLOWER_KINDS = ['Композиция из многолетников', 'Композиция из 1-2-летников'];
  var LAWN_KINDS = ['Спортивный', 'Партерный', 'Обыкновенный', 'Луговой', 'Мавританский', 'Специального назначения'];
  var TYPE_NAMES = {
    'хвой.': 'дерево хвойное', 'медл.': 'дерево лиственное медленнорастущее', 'быст.': 'дерево лиственное быстрорастущее',
    'плод.': 'дерево плодовое', 'малоц.': 'дерево малоценное', 'лиан.': 'древесная лиана', 'хвой..': 'кустарник хвойный',
    'вечз.': 'кустарник вечнозелёный', 'крас.': 'кустарник красивоцветущий', 'медл..': 'кустарник медленнорастущий',
    'быст..': 'кустарник быстрорастущий', 'плод..': 'кустарник плодовый', 'ж.и.1': 'живая изгородь однорядная', 'ж.и.2': 'живая изгородь двухрядная'
  };
  var $ = function (s, el) { return (el || document).querySelector(s); };
  var esc = function (s) { return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'); };
  var xn = function (v) { return v === null || v === undefined ? '' : (typeof v === 'number' ? E.xnum(v) : String(v)); };
  var round = function (v, n) { return typeof v === 'number' ? E.xnum(E.xround(v, n)) : xn(v); };

  // ------------------------------------------------------------------ project
  function blankProject() {
    var s = E.defaultSettings(REF);
    s.date = new Date().toISOString().slice(0, 10);
    return {
      format: 'taksaciya-lite', version: 1,
      meta: { org: '', objectNo: '', objectName: '', stage: '', author: '' },
      settings: s, baseValues: REF.baseValues.map(function (b) { return { from: b.from, rub: b.rub, act: b.act }; }),
      customSpecies: [], trees: emptyRows(10), flowers: [], lawns: [], grass: [],
      projected: { perennial: '', annual: '', lawns: {} }, areaTotals: { flowers: '', lawns: '', grass: '' }
    };
  }
  function emptyRow() { return { count: '', start: '', species: '', height: '', diam: '', cond: '', action: '', note: '', reason: '', kRepl: '' }; }
  function emptyRows(n) { var a = []; for (var i = 0; i < n; i++) a.push(emptyRow()); return a; }
  function exampleProject() {
    var p = blankProject();
    p.meta = { org: 'Пример', objectNo: '...-ГП', objectName: 'Пример: данные из файла ТАКСАЦИЯ-АВТО 4.2', stage: 'С', author: '' };
    p.settings.date = REF.defaults.date || p.settings.date;
    var raw = [[1, 20, 20, 'Ель обыкновенная'], [2, 10, 20, 'Береза повислая'], [1, 15, 20, 'Ива ломкая'], [1, 20, 20, 'Ель обыкновенная'],
      [2, 10, 20, 'Береза повислая'], [1, 14, 20, 'Ель обыкновенная'], [20, 12, 40, 'Ель обыкновенная'], [1, 17, 30, 'Ель обыкновенная'],
      [15, 23, 35, 'Береза повислая'], [1, 23, 23, 'Ива ломкая'], [1, 50, 12, 'Клен остролистный'], [1, 23, 23, 'Яблоня домашняя'],
      [1, 23, 12, 'Береза повислая'], [1, 15, 20, 'Ива ломкая'], [1, 15, 20, 'Ива ломкая']];
    p.trees = raw.map(function (t) { var r = emptyRow(); r.count = String(t[0]); r.height = String(t[1]); r.diam = String(t[2]); r.species = t[3]; r.action = 'В'; r.reason = 'СЕТИ'; return r; });
    p.trees.push(emptyRow(), emptyRow());
    p._example = true;
    return p;
  }
  function normalize(p) {
    var b = blankProject();
    p = p || {};
    var out = {};
    Object.keys(b).forEach(function (k) { out[k] = p[k] !== undefined ? p[k] : b[k]; });
    out.settings = Object.assign({}, b.settings, p.settings || {});
    out.settings.compMap = Object.assign({}, b.settings.compMap, (p.settings || {}).compMap || {});
    out.meta = Object.assign({}, b.meta, p.meta || {});
    out.projected = Object.assign({ perennial: '', annual: '', lawns: {} }, p.projected || {});
    out.projected.lawns = Object.assign({}, out.projected.lawns || {});
    out.areaTotals = Object.assign({ flowers: '', lawns: '', grass: '' }, p.areaTotals || {});
    out.trees = (out.trees || []).map(function (r) { return Object.assign(emptyRow(), r); });
    ['flowers', 'lawns', 'grass'].forEach(function (k) { out[k] = (out[k] || []).map(function (r) { return Object.assign({ kind: '', cond: '', area: '', transplant: false, reason: '', note: '', kRepl: '' }, r); }); });
    if (!out.trees.length) out.trees = emptyRows(5);
    out._example = !!p._example;
    return out;
  }
  function loadStored() {
    try { var t = localStorage.getItem(STORE_KEY); if (t) return normalize(JSON.parse(t)); } catch (e) { /* хранилище недоступно */ }
    return null;
  }
  var saveTimer = null;
  function persist() {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(function () { try { localStorage.setItem(STORE_KEY, JSON.stringify(project)); } catch (e) { /* нет места или запрещено */ } }, 400);
  }

  var project = loadStored() || exampleProject();
  var res = null;
  var view = 'trees';

  // ------------------------------------------------------------------ recalc + summary
  var recalcTimer = null;
  function scheduleRecalc() { clearTimeout(recalcTimer); recalcTimer = setTimeout(recalc, 180); }
  function recalc() {
    try { res = E.compute(project, REF); } catch (e) { console.error(e); toast('Ошибка расчёта: ' + e.message); return; }
    renderSummary();
    if (view === 'trees') updateTreeCalc();
    else if (view === 'areas') updateAreaCalc();
    else if (view === 'object') updateObjectCalc();
    else if (view.indexOf('rep-') === 0) renderView();
    persist();
  }
  function renderSummary() {
    var S = res.settings, T = res.removal, el = $('#summary'), html = '';
    var bv = res.baseValue;
    html += item('Базовая величина', xn(bv) + ' руб.');
    if (S.money) {
      html += item('Итого выплат', moneyFmt(T.money.total), true);
    } else {
      html += item('Саженцев деревьев', xn(T.plant.trees) + ' шт.', true);
      html += item('Кустарников', xn(T.plant.shrubsAll));
    }
    html += item('Удаляется', xn(T.count.trees) + ' дер. / ' + xn(T.count.shrubsC2035) + ' куст.');
    html += item('Древесина', round(res.volume.stemsM3.all, 2) + ' м³');
    var n = res.errors.length;
    html += '<button class="pill ' + (n ? 'err' : 'ok') + '" id="btn-errors" type="button">' + (n ? 'Ошибок: ' + n : 'Ошибок нет') + '</button>';
    el.innerHTML = html;
    $('#obj-name').textContent = [project.meta.objectNo, project.meta.objectName].filter(Boolean).join(' · ') || 'Новый объект';
    function item(k, v, main) { return '<div class="sum-item' + (main ? ' main' : '') + '"><span class="k">' + esc(k) + '</span><span class="v">' + esc(v) + '</span></div>'; }
  }
  function moneyFmt(v) {
    var S = res.settings, b = res.baseValue;
    if (S.unit === 'руб.') return xn(v * b) + ' руб.';
    if (S.unit === 'баз.в.') return xn(v) + ' б.в.';
    return xn(v) + ' б.в. (' + xn(v * b) + ' руб.)';
  }

  // ------------------------------------------------------------------ views
  var VIEWS = {
    object: { title: 'Объект и настройки', render: renderObject },
    trees: { title: 'Деревья и кустарники', render: renderTrees },
    areas: { title: 'Цветники, газоны, травяной покров', render: renderAreas },
    'rep-existing': { title: 'Ведомость существующих', render: function () { renderReport(repExisting()); } },
    'rep-removal': { title: 'Ведомость удаляемых', render: function () { renderReport(repRemoval()); } },
    'rep-transplant': { title: 'Ведомость пересаживаемых', render: function () { renderReport(repTransplant()); } },
    'rep-areas': { title: 'Цветники и газоны', render: function () { renderReport(repAreas()); } },
    'rep-balance': { title: 'Баланс и древесина', render: function () { renderReport(repBalance()); } },
    library: { title: 'Справочники', render: renderLibrary },
    help: { title: 'Справка', render: renderHelp }
  };
  function go(v) {
    if (!VIEWS[v]) v = 'trees';
    view = v;
    document.querySelectorAll('nav.tabs a').forEach(function (a) {
      if (a.getAttribute('href') === '#' + v) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current');
    });
    renderView();
    try { localStorage.setItem(STORE_KEY + '-view', v); } catch (e) { /* */ }
  }
  function renderView() {
    var sc = window.scrollY;
    VIEWS[view].render();
    if (view.indexOf('rep-') === 0) window.scrollTo(0, sc);
  }
  window.addEventListener('hashchange', function () { go(location.hash.slice(1)); });

  // ------------------------------------------------------------------ object & settings
  function renderObject() {
    var p = project, S = p.settings;
    var reasons = S.reasons.map(function (r, i) {
      return '<tr><td><input type="text" id="rs-n-' + i + '" data-rs="' + i + '" data-f="name" value="' + esc(r.name) + '"></td>' +
        '<td><input type="text" id="rs-g-' + i + '" data-rs="' + i + '" data-f="group" value="' + esc(r.group) + '"></td>' +
        '<td><input type="text" id="rs-r-' + i + '" data-rs="' + i + '" data-f="replace" value="' + esc(r.replace) + '" list="dl-krepl" placeholder="напр. 12"></td>' +
        '<td><button class="btn small danger" data-rs-del="' + i + '" type="button" aria-label="Удалить причину">✕</button></td></tr>';
    }).join('');
    var compRows = E.TREE_TYPES.concat(E.SHRUB_TYPES).map(function (t) {
      var opts = ['<option value="">своим типом</option>'].concat(E.TREE_TYPES.concat(E.SHRUB_TYPES).map(function (o) {
        return '<option value="' + o + '"' + (S.compMap[t] === o ? ' selected' : '') + '>' + o + '</option>';
      })).join('');
      return '<label for="cm-' + t + '">' + esc(t) + ' <small>(' + esc(TYPE_NAMES[t]) + ')</small></label><select id="cm-' + t + '" data-cm="' + t + '">' + opts + '</select>';
    }).join('');
    var plotOpt = function (key, list) {
      return list.map(function (o) { return '<option value="' + o + '"' + (S.compMap[key] === o ? ' selected' : '') + '>' + o + '</option>'; }).join('');
    };
    var html = head('Объект и настройки', 'Сведения об объекте, базовая величина, коэффициенты К1–К4 и причины вырубки. Всё сохраняется в проекте.') +
      '<div class="grid2">' +
      '<div class="panel"><h3>Объект</h3><div class="fields">' +
      fld('m-org', 'Организация', p.meta.org) + fld('m-no', 'Номер объекта', p.meta.objectNo) + fld('m-name', 'Наименование объекта', p.meta.objectName) +
      fld('m-stage', 'Стадия проекта', p.meta.stage) + fld('m-author', 'Автор (ФИО)', p.meta.author) + '</div></div>' +
      '<div class="panel"><h3>Базовая величина</h3><div class="fields">' +
      '<label for="s-date">Дата расчёта</label><input type="date" id="s-date" value="' + esc(S.date) + '">' +
      '<label>По календарю</label><div id="bv-auto" class="num"></div>' +
      '<label for="s-bv">Своё значение, руб.</label><input type="text" id="s-bv" inputmode="decimal" placeholder="по календарю" value="' + esc(S.baseValue == null ? '' : S.baseValue) + '">' +
      '</div><p class="hint">Значение берётся из календаря базовых величин на дату расчёта. Календарь можно дополнить на вкладке «Справочники».</p></div>' +
      '<div class="panel"><h3>Коэффициенты для всего объекта</h3>' +
      ksw('k1', 'К1 — «зона особ. охр.?»', 'да → К1 = 2, нет → 1', S.k1) +
      ksw('k2', 'К2 — «бюджет или нет?»', 'да → К2 = 0,5, нет → 1', S.k2) +
      ksw('k3', 'К3 — «помеха инж.?»', 'да → К3 = 0,5, нет → 1', S.k3) +
      ksw('k4', 'К4 — «вне насел. пункта?»', 'да → К4 = 0,1, нет → 1', S.k4) +
      '<p class="hint">Подписи — как на кнопках оригинальной программы. Для отдельной причины или строки коэффициент можно заменить на обратный (поле «номера заменяемых коэффициентов»).</p></div>' +
      '<div class="panel"><h3>Как считать</h3><div class="fields">' +
      '<label>Компенсация</label><div class="seg" role="group" aria-label="Вид компенсации">' +
      '<button type="button" data-seg="money" data-v="1" aria-pressed="' + (S.money ? 'true' : 'false') + '">выплатами</button>' +
      '<button type="button" data-seg="money" data-v="0" aria-pressed="' + (!S.money ? 'true' : 'false') + '">посадками</button></div>' +
      sel('s-unit', 'Единицы выплат', E.UNITS, S.unit) +
      sel('s-view', 'Графа «компенсация»', ['формула + итог', 'итог'], S.view) +
      sel('s-cond', 'Состояние по умолчанию', COND, S.defaultCond) +
      fld('s-mass', 'Масса куста при вырубке, кг', S.shrubMassKg) +
      sel('s-flower', 'Учёт проектируемых цветников и газонов', ['метрами', 'стоимостью'], S.flowerMode) +
      chk('s-plots', 'Показывать поросль в балансе', S.showPlots) + chk('s-hedge', 'Живую изгородь также штуками', S.hedgePieces) +
      '</div></div>' +
      '<div class="panel" style="grid-column: 1 / -1"><h3>Причины вырубки</h3><div class="table-wrap" style="max-height:none"><table class="grid"><thead><tr>' +
      '<th>Причина вырубки</th><th>Группа работ в балансе</th><th>Номера заменяемых коэффициентов</th><th></th></tr></thead><tbody>' + reasons + '</tbody></table></div>' +
      '<div class="grid-foot" style="margin-top:8px"><button class="btn" id="rs-add" type="button">Добавить причину</button>' +
      '<span class="hint">Например, «12» — для этой причины К1 и К2 меняются на обратные; «без компенcации» — коэффициенты 0.</span></div></div>' +
      '<div class="panel" style="grid-column: 1 / -1"><h3>Компенсировать типами (для посадок)</h3><div class="fields">' + compRows +
      '<label for="cm-plotTree">поросль деревьев → тип</label><select id="cm-plotTree" data-cm="plotTree">' + plotOpt('plotTree', ['хвой.', 'медл.', 'быст.']) + '</select>' +
      '<label for="cm-plotShrub">поросль кустарников → тип</label><select id="cm-plotShrub" data-cm="plotShrub">' + plotOpt('plotShrub', E.SHRUB_TYPES) + '</select>' +
      '</div></div></div>';
    $('#main').innerHTML = '<section class="view">' + html + '</section>';
    updateObjectCalc();
    var m = $('#main');
    m.oninput = m.onchange = function (ev) {
      var t = ev.target, id = t.id;
      var metaMap = { 'm-org': 'org', 'm-no': 'objectNo', 'm-name': 'objectName', 'm-stage': 'stage', 'm-author': 'author' };
      if (metaMap[id]) project.meta[metaMap[id]] = t.value;
      else if (id === 's-date') S.date = t.value;
      else if (id === 's-bv') S.baseValue = t.value.trim() === '' ? null : t.value.replace(',', '.');
      else if (id === 's-unit') S.unit = t.value;
      else if (id === 's-view') S.view = t.value;
      else if (id === 's-cond') S.defaultCond = t.value;
      else if (id === 's-mass') S.shrubMassKg = Number(String(t.value).replace(',', '.')) || 0;
      else if (id === 's-flower') S.flowerMode = t.value;
      else if (id === 's-plots') S.showPlots = t.checked;
      else if (id === 's-hedge') S.hedgePieces = t.checked;
      else if (/^k[1-4]$/.test(id)) S[id] = t.checked;
      else if (t.dataset.rs !== undefined) S.reasons[+t.dataset.rs][t.dataset.f] = t.value;
      else if (t.dataset.cm) S.compMap[t.dataset.cm] = t.value;
      else return;
      if (/^k[1-4]$/.test(id)) t.closest('.switch-row').querySelector('.kval').textContent = kv(id, t.checked);
      refreshLists();
      scheduleRecalc();
    };
    m.onclick = function (ev) {
      var b = ev.target.closest('button');
      if (!b) return;
      if (b.dataset.seg === 'money') { S.money = b.dataset.v === '1'; renderObject(); scheduleRecalc(); }
      else if (b.id === 'rs-add') { S.reasons.push({ name: '', group: '', replace: '' }); renderObject(); }
      else if (b.dataset.rsDel !== undefined) { S.reasons.splice(+b.dataset.rsDel, 1); renderObject(); refreshLists(); scheduleRecalc(); }
    };
    function fld(id, l, v) { return '<label for="' + id + '">' + esc(l) + '</label><input type="text" id="' + id + '" value="' + esc(v == null ? '' : v) + '">'; }
    function sel(id, l, opts, v) { return '<label for="' + id + '">' + esc(l) + '</label><select id="' + id + '">' + opts.map(function (o) { return '<option' + (o === v ? ' selected' : '') + '>' + esc(o) + '</option>'; }).join('') + '</select>'; }
    function chk(id, l, v) { return '<label for="' + id + '">' + esc(l) + '</label><input type="checkbox" id="' + id + '"' + (v ? ' checked' : '') + '>'; }
    function ksw(id, l, sub, v) { return '<div class="switch-row"><input type="checkbox" id="' + id + '"' + (v ? ' checked' : '') + '><label class="lbl" for="' + id + '">' + esc(l) + '<small>' + esc(sub) + '</small></label><span class="kval">' + kv(id, v) + '</span></div>'; }
  }
  function kv(id, v) { return { k1: v ? 'К1 = 2' : 'К1 = 1', k2: v ? 'К2 = 0,5' : 'К2 = 1', k3: v ? 'К3 = 0,5' : 'К3 = 1', k4: v ? 'К4 = 0,1' : 'К4 = 1' }[id]; }
  function updateObjectCalc() {
    var el = $('#bv-auto');
    if (!el || !res) return;
    var r = res.baseValueRow;
    el.textContent = r ? xn(r.rub) + ' руб. с ' + fmtDate(r.from) + (r.act ? ' — ' + r.act : '') : 'нет значения на эту дату';
  }
  function fmtDate(iso) { var p = String(iso).split('-'); return p.length === 3 ? p[2] + '.' + p[1] + '.' + p[0] : iso; }
  function head(t, p) { return '<div class="view-head"><h2>' + esc(t) + '</h2>' + (p ? '<p>' + esc(p) + '</p>' : '') + '</div>'; }

  // ------------------------------------------------------------------ trees grid
  var TREE_COLS = [
    { k: 'count', l: 'Кол-во', t: 'Количество, шт. Для поросли — площадь, м²; для живой изгороди — длина, п.м.', c: 'col-narrow' },
    { k: 'start', l: '№ нач.', t: 'Начальный номер по плану, если нумерация не по порядку', c: 'col-narrow' },
    { k: 'species', l: 'Порода, вид', c: 'col-wide', list: 'dl-species' },
    { k: 'height', l: 'Высота, м', c: 'col-narrow' },
    { k: 'diam', l: 'Диаметр, см', t: 'Несколько стволов — через запятую: 20,15,12. Кустарник и живая изгородь — «-». Поросль деревьев — «0-4». Мелкие деревья: до1, **до1, до4, **до4', c: 'col-mid' },
    { k: 'cond', l: 'Состояние', sel: [['', '(по умолч.)']].concat(COND.map(function (c) { return [c, c]; })) },
    { k: 'action', l: 'Действие', sel: [['', 'сохранить'], ['В', 'вырубить'], ['П', 'пересадить']], cls: 'act' },
    { k: 'reason', l: 'Причина', c: 'col-mid', list: 'dl-reasons' },
    { k: 'kRepl', l: 'Инд. К', t: 'Номера коэффициентов К1–К4, которые для строки меняются на обратные (например 24), или «без компенcации»', c: 'col-narrow', list: 'dl-krepl' },
    { k: 'note', l: 'Примечание', c: 'col-mid', t: 'Попадает в наименование в скобках. «V=50%» уменьшает массу кустарника' }
  ];
  var CALC_COLS = ['Тип', 'К1; К2; К3; К4', 'Компенсационные посадки (выплаты)', 'Итог'];
  function renderTrees() {
    var rows = project.trees;
    var th = '<th>#</th><th class="calc">№ по плану</th>' + TREE_COLS.map(function (c) { return '<th' + (c.t ? ' title="' + esc(c.t) + '"' : '') + '>' + esc(c.l) + '</th>'; }).join('') +
      CALC_COLS.map(function (c) { return '<th class="calc">' + esc(c) + '</th>'; }).join('');
    var body = rows.map(function (r, i) { return treeRowHtml(r, i); }).join('');
    $('#main').innerHTML = '<section class="view">' +
      head('Деревья и кустарники', 'Ведомость существующих растений (лист «СУЩд» оригинала). Расчёт обновляется сразу. Можно вставлять столбцы из Excel (Ctrl+V) в том же порядке колонок.') +
      '<div class="grid-foot no-print"><button class="btn" data-add="1" type="button">+ строка</button><button class="btn" data-add="10" type="button">+ 10 строк</button>' +
      '<button class="btn" id="tr-clean" type="button">Удалить пустые строки</button><button class="btn danger" id="tr-clear" type="button">Очистить ведомость</button>' +
      '<span class="hint">Enter — следующая строка. Кнопки «＋ ⧉ ✕» у номера строки: вставить, копировать, удалить.</span></div>' +
      '<div id="tr-confirm"></div>' +
      '<div class="table-wrap" id="tr-wrap"><table class="grid" id="tr-grid"><thead><tr>' + th + '</tr></thead><tbody>' + body + '</tbody></table></div></section>';
    updateTreeCalc();
    bindTreeGrid();
  }
  function treeRowHtml(r, i) {
    var cells = TREE_COLS.map(function (c, ci) {
      var id = 'tr-' + i + '-' + c.k, v = r[c.k] == null ? '' : r[c.k];
      if (c.sel) {
        return '<td class="' + (c.cls || '') + '"><select id="' + id + '" data-r="' + i + '" data-k="' + c.k + '" data-ci="' + ci + '" aria-label="' + esc(c.l) + '">' +
          c.sel.map(function (o) { return '<option value="' + esc(o[0]) + '"' + (String(v).toUpperCase() === String(o[0]).toUpperCase() ? ' selected' : '') + '>' + esc(o[1]) + '</option>'; }).join('') + '</select></td>';
      }
      return '<td class="' + (c.c || '') + '"><input type="text" id="' + id + '" data-r="' + i + '" data-k="' + c.k + '" data-ci="' + ci + '" value="' + esc(v) + '"' +
        (c.list ? ' list="' + c.list + '"' : '') + ' aria-label="' + esc(c.l) + '" autocomplete="off"></td>';
    }).join('');
    return '<tr data-row="' + i + '"><td class="rn">' + (i + 1) + '<span class="row-tools"><button type="button" data-ins="' + i + '" title="Вставить строку ниже" aria-label="Вставить строку">＋</button>' +
      '<button type="button" data-dup="' + i + '" title="Копировать строку" aria-label="Копировать строку">⧉</button>' +
      '<button type="button" class="del" data-del="' + i + '" title="Удалить строку" aria-label="Удалить строку">✕</button></span></td>' +
      '<td class="c num" id="tc-no-' + i + '"></td>' + cells + '<td class="c" id="tc-ty-' + i + '"></td><td class="c num" id="tc-k-' + i + '"></td>' +
      '<td class="c wrap" id="tc-g-' + i + '"></td><td class="c num n" id="tc-y-' + i + '"></td></tr>';
  }
  function updateTreeCalc() {
    if (!res || !$('#tr-grid')) return;
    var byIdx = {};
    res.rows.forEach(function (r) { byIdx[r.idx] = r; });
    var trs = $('#tr-grid').tBodies[0].rows;
    for (var i = 0; i < trs.length; i++) {
      var tr = trs[i], r = byIdx[i];
      var set = function (key, txt, title) { var c = document.getElementById('tc-' + key + '-' + i); if (c) { c.textContent = txt; if (title !== undefined) c.title = title; } };
      if (!r) { set('no', ''); set('ty', ''); set('k', ''); set('g', ''); set('y', ''); tr.className = ''; tr.title = ''; continue; }
      set('no', r.no);
      set('ty', r.N || '—', TYPE_NAMES[r.N] || 'порода не найдена в справочнике');
      set('k', r.coef ? r.coef.str.replace(/\s+/g, ' ').trim() : '');
      var g = '', y = '';
      if (r.removed && r.rm) { g = r.rm.G; y = typeof r.rm.Y === 'number' ? xn(r.rm.Y) : (r.rm.Y === '-' ? 'в итогах' : ''); }
      else if (r.transplanted) { g = 'ком: ' + (r.ball || '—'); }
      else g = r.H;
      set('g', g); set('y', y);
      var cls = [];
      if (r.errors.length) cls.push('has-error'); else if (r.warnings.length) cls.push('has-warn');
      if (r.removed) cls.push('removed'); else if (r.transplanted) cls.push('transpl');
      tr.className = cls.join(' ');
      tr.title = r.errors.concat(r.warnings).join('; ');
    }
  }
  function bindTreeGrid() {
    var grid = $('#tr-grid');
    grid.oninput = grid.onchange = function (ev) {
      var t = ev.target;
      if (t.dataset.r === undefined) return;
      var row = project.trees[+t.dataset.r];
      row[t.dataset.k] = t.value;
      if (t.dataset.k === 'species' && ev.type === 'change') autoFillDiam(row, +t.dataset.r);
      if (project._example) project._example = false;
      scheduleRecalc();
    };
    grid.onkeydown = function (ev) {
      var t = ev.target;
      if (ev.key !== 'Enter' || t.dataset.r === undefined) return;
      ev.preventDefault();
      var r = +t.dataset.r + (ev.shiftKey ? -1 : 1);
      if (r >= project.trees.length) { project.trees.push(emptyRow()); appendTreeRow(); }
      var nx = document.getElementById('tr-' + Math.max(0, r) + '-' + t.dataset.k);
      if (nx) nx.focus();
    };
    grid.onpaste = function (ev) {
      var t = ev.target;
      if (t.dataset.r === undefined) return;
      var text = (ev.clipboardData || window.clipboardData).getData('text');
      if (!/[\t\n]/.test(text)) return;
      ev.preventDefault();
      var lines = text.replace(/\r/g, '').replace(/\n$/, '').split('\n');
      var r0 = +t.dataset.r, c0 = +t.dataset.ci;
      lines.forEach(function (line, li) {
        var r = r0 + li;
        while (r >= project.trees.length) project.trees.push(emptyRow());
        line.split('\t').forEach(function (val, vi) {
          var col = TREE_COLS[c0 + vi];
          if (col) project.trees[r][col.k] = val.trim();
        });
      });
      renderTrees(); recalc();
      toast('Вставлено строк: ' + lines.length);
    };
    $('#main').onclick = function (ev) {
      var b = ev.target.closest('button');
      if (!b) return;
      if (b.dataset.add) { for (var i = 0; i < +b.dataset.add; i++) project.trees.push(emptyRow()); renderTrees(); recalc(); }
      else if (b.dataset.ins !== undefined) { project.trees.splice(+b.dataset.ins + 1, 0, emptyRow()); renderTrees(); recalc(); }
      else if (b.dataset.dup !== undefined) { var src = project.trees[+b.dataset.dup]; project.trees.splice(+b.dataset.dup + 1, 0, Object.assign({}, src)); renderTrees(); recalc(); }
      else if (b.dataset.del !== undefined) { project.trees.splice(+b.dataset.del, 1); if (!project.trees.length) project.trees.push(emptyRow()); renderTrees(); recalc(); }
      else if (b.id === 'tr-clean') { project.trees = project.trees.filter(function (r) { return String(r.species || '').trim() !== ''; }); if (!project.trees.length) project.trees = emptyRows(5); renderTrees(); recalc(); }
      else if (b.id === 'tr-clear') askConfirm('#tr-confirm', 'Удалить все строки ведомости?', function () { project.trees = emptyRows(10); renderTrees(); recalc(); });
    };
  }
  function appendTreeRow() {
    var i = project.trees.length - 1, tb = $('#tr-grid').tBodies[0];
    tb.insertAdjacentHTML('beforeend', treeRowHtml(project.trees[i], i));
  }
  /** При выборе поросли/кустарника подставить типовой диаметр, как шаблон строки оригинала (СУЩд!F20). */
  function autoFillDiam(row, i) {
    if (String(row.diam || '').trim() !== '') return;
    var info = E.speciesInfo(REF, row.species);
    if (!info.found) return;
    var v = /ч-к поросли дер/i.test(row.species) ? '0-4' : (E.SHRUB_TYPES.indexOf(info.type) >= 0 ? '-' : '');
    if (v) { row.diam = v; var el = document.getElementById('tr-' + i + '-diam'); if (el) el.value = v; }
  }

  // ------------------------------------------------------------------ areas
  var AREA_SECTIONS = [
    { key: 'flowers', title: 'Цветники', kinds: FLOWER_KINDS },
    { key: 'lawns', title: 'Газоны', kinds: LAWN_KINDS },
    { key: 'grass', title: 'Иной травяной покров', kinds: null }
  ];
  function renderAreas() {
    var p = project;
    AREA_SECTIONS.forEach(function (sec) { if (!p[sec.key].length) for (var i = 0; i < 3; i++) p[sec.key].push({ kind: '', cond: '', area: '', transplant: false, reason: '', note: '', kRepl: '' }); });
    var html = head('Цветники, газоны, травяной покров', 'Лист «ЦвГаТр» оригинала. Стоимость, баз.вел./м²: многолетники 10, 1-2-летники 2; газоны 0,5–2; иной травяной покров 0,25.');
    AREA_SECTIONS.forEach(function (sec) {
      var rows = p[sec.key];
      var body = rows.map(function (r, i) {
        var kind = sec.kinds ? '<td><select id="ar-' + sec.key + '-' + i + '-kind" data-s="' + sec.key + '" data-r="' + i + '" data-k="kind" aria-label="Вид"><option value=""></option>' +
          sec.kinds.map(function (k) { return '<option' + (r.kind === k ? ' selected' : '') + '>' + esc(k) + '</option>'; }).join('') + '</select></td>' : '';
        return '<tr data-row="' + i + '"><td class="rn">' + (i + 1) + '<span class="row-tools"><button type="button" class="del" data-adel="' + sec.key + ':' + i + '" aria-label="Удалить строку">✕</button></span></td>' + kind +
          '<td><select id="ar-' + sec.key + '-' + i + '-cond" data-s="' + sec.key + '" data-r="' + i + '" data-k="cond" aria-label="Состояние"><option value=""></option>' +
          COND.map(function (c) { return '<option' + (r.cond === c ? ' selected' : '') + '>' + c + '</option>'; }).join('') + '</select></td>' +
          '<td class="col-narrow"><input type="text" id="ar-' + sec.key + '-' + i + '-area" data-s="' + sec.key + '" data-r="' + i + '" data-k="area" value="' + esc(r.area) + '" aria-label="Площадь"></td>' +
          '<td style="text-align:center"><input type="checkbox" id="ar-' + sec.key + '-' + i + '-tr" data-s="' + sec.key + '" data-r="' + i + '" data-k="transplant"' + (r.transplant ? ' checked' : '') + ' aria-label="Пересадить"></td>' +
          '<td class="col-mid"><input type="text" id="ar-' + sec.key + '-' + i + '-reason" data-s="' + sec.key + '" data-r="' + i + '" data-k="reason" list="dl-reasons" value="' + esc(r.reason) + '" aria-label="Причина"></td>' +
          '<td class="col-mid"><input type="text" id="ar-' + sec.key + '-' + i + '-note" data-s="' + sec.key + '" data-r="' + i + '" data-k="note" value="' + esc(r.note) + '" aria-label="Примечание"></td>' +
          '<td class="col-narrow"><input type="text" id="ar-' + sec.key + '-' + i + '-k" data-s="' + sec.key + '" data-r="' + i + '" data-k="kRepl" list="dl-krepl" value="' + esc(r.kRepl) + '" aria-label="Инд. К"></td>' +
          '<td class="c num" id="ac-' + sec.key + '-' + i + '-k"></td><td class="c wrap" id="ac-' + sec.key + '-' + i + '-e"></td></tr>';
      }).join('');
      html += '<div class="panel"><h3>' + esc(sec.title) + '</h3><div class="table-wrap" style="max-height:none"><table class="grid" data-sec="' + sec.key + '"><thead><tr><th>#</th>' +
        (sec.kinds ? '<th>Вид</th>' : '') + '<th>Состояние</th><th>Площадь, м²</th><th>Пересадить</th><th>Причина</th><th>Примечание</th><th>Инд. К</th>' +
        '<th class="calc">К1; К2; К3; К4</th><th class="calc">Компенсационные выплаты</th></tr></thead><tbody>' + body + '</tbody></table></div>' +
        '<div class="grid-foot" style="margin-top:8px"><button class="btn" type="button" data-aadd="' + sec.key + '">+ строка</button>' +
        '<label class="hint" for="at-' + sec.key + '">Всего существующих на участке, м²:</label><input type="text" id="at-' + sec.key + '" data-at="' + sec.key + '" style="width:9ch" value="' + esc(p.areaTotals[sec.key]) + '">' +
        '<span class="hint num" id="as-' + sec.key + '"></span></div></div>';
    });
    var pl = p.projected.lawns || {};
    html += '<div class="panel"><h3>Проектируемое озеленение (учитывается при компенсации)</h3><div class="fields">' +
      '<label for="pr-per">Цветники из многолетников, м²</label><input type="text" id="pr-per" data-pr="perennial" value="' + esc(p.projected.perennial) + '">' +
      '<label for="pr-ann">Цветники из 1-2-летников, м²</label><input type="text" id="pr-ann" data-pr="annual" value="' + esc(p.projected.annual) + '">' +
      LAWN_KINDS.map(function (k, i) { return '<label for="pr-l' + i + '">Газон «' + esc(k) + '», м²</label><input type="text" id="pr-l' + i + '" data-prl="' + esc(k) + '" value="' + esc(pl[k] == null ? '' : pl[k]) + '">'; }).join('') +
      '</div><p class="hint">Режим зачёта («метрами» или «стоимостью») задаётся на вкладке «Объект и настройки». Остаток проектируемого газона засчитывается за иной травяной покров.</p></div>';
    $('#main').innerHTML = '<section class="view">' + html + '</section>';
    updateAreaCalc();
    var m = $('#main');
    m.oninput = m.onchange = function (ev) {
      var t = ev.target;
      if (t.dataset.s) { var row = project[t.dataset.s][+t.dataset.r]; row[t.dataset.k] = t.type === 'checkbox' ? t.checked : t.value; }
      else if (t.dataset.at) project.areaTotals[t.dataset.at] = t.value;
      else if (t.dataset.pr) project.projected[t.dataset.pr] = t.value;
      else if (t.dataset.prl) project.projected.lawns[t.dataset.prl] = t.value;
      else return;
      scheduleRecalc();
    };
    m.onclick = function (ev) {
      var b = ev.target.closest('button');
      if (!b) return;
      if (b.dataset.aadd) { project[b.dataset.aadd].push({ kind: '', cond: '', area: '', transplant: false, reason: '', note: '', kRepl: '' }); renderAreas(); recalc(); }
      else if (b.dataset.adel) { var pr = b.dataset.adel.split(':'); project[pr[0]].splice(+pr[1], 1); renderAreas(); recalc(); }
    };
  }
  function updateAreaCalc() {
    if (!res) return;
    AREA_SECTIONS.forEach(function (sec) {
      var r = res[sec.key];
      var by = {}; r.rows.forEach(function (x) { by[x.idx] = x; });
      project[sec.key].forEach(function (src, i) {
        var x = by[i], k = document.getElementById('ac-' + sec.key + '-' + i + '-k'), e = document.getElementById('ac-' + sec.key + '-' + i + '-e');
        if (!k) return;
        k.textContent = x && x.coef ? x.coef.str.replace(/\s+/g, ' ').trim() : '';
        e.textContent = x ? (x.errors.length ? x.errors.join('; ') : x.E) : '';
        var tr = k.parentNode; tr.className = x && x.errors.length ? 'has-error' : '';
      });
      var s = document.getElementById('as-' + sec.key);
      if (s) s.textContent = 'Удаляется ' + xn(r.area) + ' м²; к выплате ' + areaResultText(r);
    });
  }
  function areaResultText(s) {
    if (!s.rows.length) return '—';
    if (s.allTransplanted) return 'не требуется';
    if (s.rest <= 0 && s.P > 0) return 'компенсировано проектируемым озеленением';
    return E.unitText(s.result, s.result * res.baseValue, res.settings);
  }

  // ------------------------------------------------------------------ reports
  function repHeader() {
    var m = project.meta;
    return [m.org, m.objectNo, m.objectName, m.stage ? 'стадия ' + m.stage : ''].filter(Boolean).join(' · ');
  }
  function plotCond(r) {           // СУЩд1!F: «-» для удаляемой поросли
    if (r.removed && r.rm && r.rm.isPlot) return '-';
    return r.cond || '-';
  }
  function repExisting() {
    var ex = res.existing;
    var rows = res.rows.map(function (r) { return [r.no, r.title, r.qty, r.heightRaw == null ? '' : xn(r.height === null ? r.heightRaw : r.height), r.diamText, plotCond(r), r.H]; });
    return {
      id: 'existing', title: 'Ведомость существующих деревьев и кустарников', sub: repHeader(),
      head: ['Номер по плану', 'Порода, вид', 'Кол-во, шт.', 'Высота, м', 'Диаметр ствола, см', 'Качественное состояние', 'Примечание'],
      rows: rows, num: [2, 3],
      totals: [['Итого:', 'Дер. ' + xn(ex.trees), 'Кус. ' + xn(ex.shrubs), 'Дер.поросль ' + xn(ex.treePlots) + '_м²', 'Кус.поросль ' + xn(ex.shrubPlots) + '_м²',
        'ж.изг.1ряд_' + xn(ex.hedge1) + '_п.м.', 'ж.изг.2ряд_' + xn(ex.hedge2) + '_п.м.']],
      widths: [12, 36, 10, 10, 16, 14, 26]
    };
  }
  function repRemoval() {
    var rows = res.rows.filter(function (r) { return r.removed; }).map(function (r) {
      var m = r.rm || {};
      return [r.no, r.title, r.qty, r.height === null ? '' : r.height, m.E, m.F, m.G, m.Hnote];
    });
    var tot = res.removalReport.filter(function (row) { return row.some(function (c) { return c !== '' && c !== null; }); });
    var S = res.settings;
    return {
      id: 'removal', title: 'Ведомость удаляемых деревьев и кустарников', sub: repHeader() + ' · ' + (S.money ? 'компенсационные выплаты, ' + S.unit : 'компенсационные посадки') +
        ' · базовая величина ' + xn(res.baseValue) + ' руб.',
      head: ['Номер по плану', 'Порода, вид', 'Кол-во, шт.', 'Высота, м', 'Диаметр ствола, см', 'Качественное состояние', 'Компенсационные посадки (выплаты)', 'Примечание'],
      rows: rows, num: [2, 3], totals: tot, totalsBig: true, widths: [12, 36, 10, 10, 16, 14, 40, 18],
      notes: plotNotes()
    };
  }
  function plotNotes() {
    var T = res.removal, out = [];
    [['Поросль деревьев', T.treePlots], ['Поросль кустарников', T.shrubPlots]].forEach(function (x) {
      x[1].groups.forEach(function (g) {
        out.push(x[0] + ': ' + xn(g.area) + ' м² × ' + xn(g.norm) + ' × (' + g.str.replace(/\s+/g, ' ').trim() + ') = ' + xn(E.xround(g.value, 6)));
      });
    });
    return out;
  }
  function repTransplant() {
    var t = res.transplant;
    var rows = t.rows.map(function (r) { return [r.no, r.title, r.qty, r.height === null ? '' : r.height, r.diamText, r.cond, r.ball, '(' + r.reason + ')']; });
    var tot = [['', 'Итого пересадить по объекту:', 'деревья, шт.: ' + xn(t.trees), 'кусты, шт.: ' + xn(t.shrubs), 'жив.изг.1р., п.м.: ' + xn(t.hedge1), 'жив.изг.2р., п.м.: ' + xn(t.hedge2), '', '']];
    t.groups.forEach(function (g) { tot.push(['', 'в т.ч. ' + g.group, xn(g.trees), xn(g.shrubs), xn(g.hedge1), xn(g.hedge2), '', '']); });
    return {
      id: 'transplant', title: 'Ведомость пересаживаемых деревьев и кустарников', sub: repHeader(),
      head: ['Номер по плану', 'Порода, вид', 'Кол-во, шт.', 'Высота, м', 'Диаметр ствола, см', 'Качественное состояние', 'Размер кома, м', 'Примечание'],
      rows: rows, num: [2, 3], totals: t.rows.length ? tot : [], widths: [12, 36, 10, 10, 16, 14, 16, 18]
    };
  }
  function repAreas() {
    var parts = [];
    AREA_SECTIONS.forEach(function (sec) {
      var s = res[sec.key];
      if (!s.rows.length) return;
      parts.push({ caption: sec.title, rows: s.rows.map(function (r, i) {
        return [i + 1, r.kind, r.cond, r.area, r.transplant ? '-' : (r.errors.length ? r.errors.join('; ') : r.E), r.F];
      }), totals: [['', 'Итого по объекту:', '', s.area, areaResultText(s), s.projectedValue ? 'учтено проектируемых: ' + xn(E.xround(s.projectedValue, 3)) + ' б.в.' : '']] });
    });
    return {
      id: 'areas', title: 'Ведомость удаляемых цветников, газонов и травяного покрова', sub: repHeader(),
      head: ['Поз.', 'Вид', 'Качественное состояние', 'Площадь, м²', 'Компенсационные посадки (выплаты)', 'Примечание'],
      parts: parts, num: [0, 3], widths: [6, 30, 14, 12, 46, 24], empty: 'Цветники, газоны и травяной покров не внесены.'
    };
  }
  function repBalance() {
    var b = res.balanceReport, v = res.volume;
    var rows = b.map(function (r) { return r; });
    var areaBal = ['flowers', 'lawns', 'grass'].map(function (k) {
      var s = res[k], tr = E.xround(s.rows.filter(function (r) { return r.transplant; }).reduce(function (a, r) { return a + r.area; }, 0), 6);
      var rem = s.area - tr; if (rem < 0.1) rem = 0;
      var total = parseFloat(String(project.areaTotals[k] || '').replace(',', '.')) || 0;
      return { tr: tr, rem: rem, total: total, kept: total - tr - rem };
    });
    var tbl = v.table;
    var hard = ['мяг.', 'хвн.', 'твд.'];
    return {
      id: 'balance', title: 'Баланс деревьев и кустарников, объёмы древесины', sub: repHeader(),
      parts: [
        { caption: 'Баланс существующих деревьев и кустарников', head: ['Проектные предложения', res.settings.showPlots ? 'Деревья всего, шт. + поросль, м²' : 'Деревья всего, шт.', 'в т.ч. лиственные декоративные', 'плодовые', 'хвойные',
          res.settings.showPlots ? 'Кустарники, шт. + поросль, м²' : 'Кустарники, шт.', res.settings.hedgePieces ? 'Живая изгородь однорядная, м / шт.' : 'Живая изгородь однорядная, м', res.settings.hedgePieces ? 'двухрядная, м / шт.' : 'двухрядная, м'], rows: rows, num: [2, 3, 4] },
        { caption: 'Баланс цветников, газонов, иного травяного покрова, м²', head: ['', 'Цветники', 'Газоны', 'Иной травяной покров'],
          rows: [['Сохраняемые'].concat(areaBal.map(function (a) { return a.kept; })), ['Пересаживаемые'].concat(areaBal.map(function (a) { return a.tr; })),
            ['Удаляемые'].concat(areaBal.map(function (a) { return a.rem; })), ['Итого'].concat(areaBal.map(function (a) { return a.total; }))], num: [1, 2, 3],
          note: 'Строка «Итого» — общая площадь на участке (вводится на вкладке «Цветники, газоны»).' },
        { caption: 'Объёмы выхода древесины и древесных отходов', head: ['Единицы измерения', 'Стволы', 'Пни', 'Ветви', 'Кустарник'],
          rows: [['складочный объём, м³'].concat(tbl.m3), ['масса, тонн'].concat(tbl.t)], num: [1, 2, 3, 4] },
        { caption: 'По типам твёрдости древесины', head: ['', 'мягкие', 'хвойные', 'твёрдые', 'итого'],
          rows: [
            ['Стволы, м³ скл. (Ø ≤ 50 см)', r4(v.le50[1]), r4(v.le50[2]), r4(v.le50[3]), r4(v.le50[1] + v.le50[2] + v.le50[3])],
            ['Стволы, м³ скл. (Ø > 50 см)', r4(v.gt50[1]), r4(v.gt50[2]), r4(v.gt50[3]), r4(v.gt50[1] + v.gt50[2] + v.gt50[3])],
            ['Стволы, м³ скл. (все)', r4(v.stemsM3[1]), r4(v.stemsM3[2]), r4(v.stemsM3[3]), r4(v.stemsM3.all)],
            ['Пни, м³ скл.', r4(v.stumpsM3[1]), r4(v.stumpsM3[2]), r4(v.stumpsM3[3]), r4(v.stumpsM3.all)],
            ['Ветви, м³ скл.', r4(v.branchesM3[1]), r4(v.branchesM3[2]), r4(v.branchesM3[3]), r4(v.branchesM3.all)],
            ['Стволы, т', r4(v.stemsT[1]), r4(v.stemsT[2]), r4(v.stemsT[3]), r4(v.stemsT.all)],
            ['Пни, т', r4(v.stumpsT[1]), r4(v.stumpsT[2]), r4(v.stumpsT[3]), r4(v.stumpsT.all)],
            ['Ветви, т', r4(v.branchesT[1]), r4(v.branchesT[2]), r4(v.branchesT[3]), r4(v.branchesT.all)]
          ], num: [1, 2, 3, 4] },
        { caption: 'Количество стволов по диаметрам, шт.', head: ['Диаметр, см'].concat(hard).concat(['итого']),
          rows: v.stemsByDiameter.map(function (b2) { return [b2.label.trim(), b2[1], b2[2], b2[3], b2.all]; }), num: [1, 2, 3, 4] }
      ]
    };
    function r4(x) { return E.xround(x, 4); }
  }
  function renderReport(rep) {
    var html = '<section class="view printing">' + head(VIEWS[view].title) +
      '<div class="report print-target" id="rep"><div class="report-tools no-print">' +
      '<button class="btn primary" type="button" id="rep-xlsx">Скачать Excel (все ведомости)</button>' +
      '<button class="btn" type="button" id="rep-copy">Копировать таблицу</button>' +
      '<button class="btn" type="button" id="rep-print">Печать</button></div>' +
      '<h3>' + esc(rep.title) + '</h3><div class="sub">' + esc(rep.sub || '') + '</div>';
    var parts = rep.parts || [{ head: rep.head, rows: rep.rows, totals: rep.totals }];
    if (rep.parts && !rep.parts.length) html += '<p class="hint">' + esc(rep.empty || 'Нет данных.') + '</p>';
    parts.forEach(function (pt) {
      var hd = pt.head || rep.head, num = pt.num || rep.num || [];
      if (pt.caption) html += '<h4 style="margin:14px 0 6px;font-size:var(--fs-m)">' + esc(pt.caption) + '</h4>';
      html += '<div class="report-scroll"><table class="rep"><thead><tr>' + hd.map(function (h) { return '<th>' + esc(h) + '</th>'; }).join('') + '</tr></thead><tbody>';
      if (!pt.rows.length) html += '<tr><td colspan="' + hd.length + '" class="notes">Нет строк.</td></tr>';
      pt.rows.forEach(function (r) { html += '<tr>' + r.map(function (c, i) { return '<td' + (num.indexOf(i) >= 0 || typeof c === 'number' ? ' class="n"' : '') + '>' + esc(xn(c)) + '</td>'; }).join('') + '</tr>'; });
      (pt.totals || []).forEach(function (r) {
        var big = /ИТОГО|Итого/.test(r.join(' '));
        html += '<tr class="tot' + (big && rep.totalsBig ? ' big' : '') + '">' + r.map(function (c) { return '<td' + (typeof c === 'number' ? ' class="n"' : '') + '>' + esc(xn(c)) + '</td>'; }).join('') + '</tr>';
      });
      html += '</tbody></table></div>';
      if (pt.note) html += '<p class="notes">' + esc(pt.note) + '</p>';
    });
    if (rep.notes && rep.notes.length) html += '<div class="notes" style="margin-top:10px"><b>Расчёт поросли (по группам коэффициентов):</b><br>' + rep.notes.map(esc).join('<br>') + '</div>';
    if (res.errors.length) html += '<p class="errtext" style="margin-top:10px">Внимание: в данных есть ошибки (' + res.errors.length + '), строки с ошибками не вошли в итоги. Список — по кнопке «Ошибок» вверху.</p>';
    html += '</div></section>';
    $('#main').innerHTML = html;
    $('#rep-xlsx').onclick = exportXlsx;
    $('#rep-print').onclick = function () { window.print(); };
    $('#rep-copy').onclick = function () { copyText(reportTsv(rep)); };
  }
  function reportTsv(rep) {
    var lines = [rep.title];
    (rep.parts || [{ head: rep.head, rows: rep.rows, totals: rep.totals }]).forEach(function (pt) {
      if (pt.caption) lines.push('', pt.caption);
      lines.push((pt.head || rep.head).join('\t'));
      pt.rows.concat(pt.totals || []).forEach(function (r) { lines.push(r.map(xn).join('\t')); });
    });
    return lines.join('\n');
  }
  function reportSheet(rep, name) {
    var rows = [[{ v: rep.title, b: true }], [rep.sub || ''], []], bold = [0];
    (rep.parts || [{ head: rep.head, rows: rep.rows, totals: rep.totals }]).forEach(function (pt) {
      if (pt.caption) { bold.push(rows.length); rows.push([pt.caption]); }
      bold.push(rows.length); rows.push(pt.head || rep.head);
      pt.rows.forEach(function (r) { rows.push(r.map(cellVal)); });
      (pt.totals || []).forEach(function (r) { rows.push(r.map(cellVal)); });
      rows.push([]);
    });
    return { name: name, rows: rows, bold: bold, widths: rep.widths || [22, 16, 16, 16, 16, 16, 16, 16] };
    function cellVal(c) { return typeof c === 'number' ? c : xn(c); }
  }
  function exportXlsx() {
    var sheets = [reportSheet(repExisting(), 'Существующие'), reportSheet(repRemoval(), 'Удаляемые'), reportSheet(repTransplant(), 'Пересаживаемые'),
      reportSheet(repAreas(), 'Цветники и газоны'), reportSheet(repBalance(), 'Баланс и древесина')];
    var data = XlsxIO.writeXlsx(sheets, project.meta.objectName || 'Таксация');
    download(data, fileBase() + ' — ведомости.xlsx', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet');
  }
  function fileBase() {
    var b = ((project.meta.objectNo || '') + ' ' + (project.meta.objectName || 'Таксация')).replace(/[\\\/:*?"<>|]+/g, ' ')
      .replace(/^[\s.…]+/, '').replace(/\s+/g, ' ').trim().slice(0, 80);
    return b || 'Таксация';
  }

  // ------------------------------------------------------------------ library
  var libFilter = '';
  function renderLibrary() {
    var p = project;
    var html = head('Справочники', 'Породы, базовые величины и тарифы — из файла ТАКСАЦИЯ-АВТО 4.2. Свои породы и новые базовые величины сохраняются в проекте.');
    html += '<div class="panel"><h3>Календарь базовых величин</h3><div class="table-wrap" style="max-height:340px"><table class="grid"><thead><tr><th>Действует с</th><th>Рублей</th><th>Постановление</th><th></th></tr></thead><tbody>' +
      p.baseValues.map(function (b, i) {
        return '<tr><td><input type="date" id="bv-d-' + i + '" data-bv="' + i + '" data-f="from" value="' + esc(b.from) + '"></td>' +
          '<td class="col-narrow"><input type="text" id="bv-r-' + i + '" data-bv="' + i + '" data-f="rub" value="' + esc(b.rub) + '"></td>' +
          '<td class="col-wide"><input type="text" id="bv-a-' + i + '" data-bv="' + i + '" data-f="act" value="' + esc(b.act) + '"></td>' +
          '<td><button class="btn small danger" type="button" data-bvdel="' + i + '" aria-label="Удалить">✕</button></td></tr>';
      }).join('') + '</tbody></table></div><div class="grid-foot" style="margin-top:8px"><button class="btn" type="button" id="bv-add">Добавить значение</button>' +
      '<button class="btn" type="button" id="bv-reset">Вернуть календарь из программы</button><span class="hint">Расчёт берёт последнее значение, действующее на дату расчёта.</span></div></div>';
    html += '<div class="panel"><h3>Свои породы</h3><p class="hint">Добавьте вид, которого нет в библиотеке. Тип породы определяет тариф и нормы посадок, тип твёрдости — таблицу объёмов древесины.</p>' +
      '<div class="table-wrap" style="max-height:none"><table class="grid"><thead><tr><th>Название</th><th>Кустарник</th><th>Тип породы</th><th>Твёрдость</th><th>Ø кома, м</th><th>h кома, м</th><th>Инвазивный</th><th></th></tr></thead><tbody>' +
      (p.customSpecies || []).map(function (s, i) {
        var typeSel = '<select id="cs-t-' + i + '" data-cs="' + i + '" data-f="type">' + E.TREE_TYPES.concat(E.SHRUB_TYPES).map(function (t) { return '<option' + (s.type === t ? ' selected' : '') + '>' + t + '</option>'; }).join('') + '</select>';
        var hardSel = '<select id="cs-h-' + i + '" data-cs="' + i + '" data-f="hard"><option value=""></option>' + ['мяг.', 'хвн.', 'твд.'].map(function (t) { return '<option' + (s.hard === t ? ' selected' : '') + '>' + t + '</option>'; }).join('') + '</select>';
        return '<tr><td class="col-wide"><input type="text" id="cs-n-' + i + '" data-cs="' + i + '" data-f="name" value="' + esc(s.name) + '"></td>' +
          '<td style="text-align:center"><input type="checkbox" id="cs-s-' + i + '" data-cs="' + i + '" data-f="shrub"' + (s.shrub ? ' checked' : '') + '></td><td>' + typeSel + '</td><td>' + hardSel + '</td>' +
          '<td class="col-narrow"><input type="text" id="cs-d-' + i + '" data-cs="' + i + '" data-f="ballD" value="' + esc(s.ballD == null ? '' : s.ballD) + '"></td>' +
          '<td class="col-narrow"><input type="text" id="cs-hh-' + i + '" data-cs="' + i + '" data-f="ballH" value="' + esc(s.ballH == null ? '' : s.ballH) + '"></td>' +
          '<td style="text-align:center"><input type="checkbox" id="cs-i-' + i + '" data-cs="' + i + '" data-f="invasive"' + (s.invasive ? ' checked' : '') + '></td>' +
          '<td><button class="btn small danger" type="button" data-csdel="' + i + '" aria-label="Удалить">✕</button></td></tr>';
      }).join('') + '</tbody></table></div><div class="grid-foot" style="margin-top:8px"><button class="btn" type="button" id="cs-add">Добавить породу</button></div></div>';
    var list = REF.species.filter(function (s) { return !libFilter || (s.name + ' ' + s.alt + ' ' + s.type).toLowerCase().indexOf(libFilter.toLowerCase()) >= 0; });
    html += '<div class="panel"><h3>Библиотека пород (' + REF.species.length + ')</h3><div class="grid-foot" style="margin-bottom:8px"><label for="lib-q" class="hint">Поиск:</label><input type="text" id="lib-q" value="' + esc(libFilter) + '" placeholder="название, латынь, тип" style="width:min(320px,100%)"></div>' +
      '<div class="table-wrap" style="max-height:420px"><table class="grid"><thead><tr><th>Вид</th><th>Второе название, латынь</th><th>Тип</th><th>Твёрдость</th><th>Ø/h кома при h&lt;1 м</th><th>Инвазивный</th></tr></thead><tbody id="lib-body">' +
      list.map(function (s) {
        return '<tr><td class="c">' + esc(s.name) + '</td><td class="c">' + esc(s.alt) + '</td><td class="c"><span class="tag' + (E.SHRUB_TYPES.indexOf(s.type) >= 0 ? ' shrub' : '') + '" title="' + esc(TYPE_NAMES[s.type] || '') + '">' + esc(s.type) + '</span></td>' +
          '<td class="c">' + esc(s.hard) + '</td><td class="c num">' + esc(s.ballD == null ? '' : xn(s.ballD) + ' / ' + xn(s.ballH)) + '</td><td class="c">' + (s.invasive ? 'да' : '') + '</td></tr>';
      }).join('') + '</tbody></table></div></div>';
    html += tariffPanel();
    $('#main').innerHTML = '<section class="view">' + html + '</section>';
    var m = $('#main');
    m.oninput = m.onchange = function (ev) {
      var t = ev.target;
      if (t.id === 'lib-q') { libFilter = t.value; if (ev.type === 'change') renderLibrary(); else { clearTimeout(m._q); m._q = setTimeout(function () { var pos = t.selectionStart; renderLibrary(); var q = $('#lib-q'); q.focus(); q.setSelectionRange(pos, pos); }, 250); } return; }
      if (t.dataset.bv !== undefined) {
        var b = project.baseValues[+t.dataset.bv];
        b[t.dataset.f] = t.dataset.f === 'rub' ? (parseFloat(t.value.replace(',', '.')) || '') : t.value;
      } else if (t.dataset.cs !== undefined) {
        var s = project.customSpecies[+t.dataset.cs], f = t.dataset.f;
        s[f] = t.type === 'checkbox' ? t.checked : (f === 'ballD' || f === 'ballH' ? (t.value === '' ? null : parseFloat(t.value.replace(',', '.'))) : t.value);
        refreshLists();
      } else return;
      scheduleRecalc();
    };
    m.onclick = function (ev) {
      var b = ev.target.closest('button');
      if (!b) return;
      if (b.id === 'bv-add') { project.baseValues.push({ from: new Date().toISOString().slice(0, 10), rub: '', act: '' }); renderLibrary(); }
      else if (b.dataset.bvdel !== undefined) { project.baseValues.splice(+b.dataset.bvdel, 1); renderLibrary(); recalc(); }
      else if (b.id === 'bv-reset') { project.baseValues = REF.baseValues.map(function (x) { return { from: x.from, rub: x.rub, act: x.act }; }); renderLibrary(); recalc(); }
      else if (b.id === 'cs-add') { project.customSpecies.push({ name: '', alt: '', shrub: false, type: 'быст.', hard: 'твд.', ballD: null, ballH: null, invasive: false, inList: false, own: null }); renderLibrary(); }
      else if (b.dataset.csdel !== undefined) { project.customSpecies.splice(+b.dataset.csdel, 1); renderLibrary(); refreshLists(); recalc(); }
    };
  }
  function tariffPanel() {
    var t = REF.tariffTrees;
    var tt = function (name, rows) {
      return '<tr><td class="c">' + esc(name) + '</td><td class="c num">' + rows.slice(1).map(function (r, i) {
        var next = rows[i + 2]; return 'от ' + xn(r[0]) + (next ? ' до ' + xn(E.xround(next[0] - 0.1, 1)) : ' и более') + ' см — ' + xn(r[1]);
      }).join('<br>') + '</td></tr>';
    };
    var sh = REF.tariffShrubs, P = REF.plots;
    return '<div class="panel"><h3>Тарифы (базовых величин за единицу)</h3><p class="hint">Из листа «УДд» оригинала, ячейки AC18:AF70. Диапазоны работают как в оригинале: значение берётся по ближайшему меньшему порогу.</p>' +
      '<div class="table-wrap" style="max-height:none"><table class="grid"><thead><tr><th>Деревья (1 ствол)</th><th>Диаметр на высоте 1,3 м → баз.вел.</th></tr></thead><tbody>' +
      tt('Хвойные', t['хвой.']) + tt('Лиственные медленнорастущие', t['медл.']) + tt('Лиственные быстрорастущие и плодовые', t['быст.']) + tt('Лиственные малоценные', t['малоц.']) +
      '<tr><td class="c">Высаженные в целях озеленения (до 1 см; малоценные до 4 см)</td><td class="c num">хвойные ' + xn(REF.plantedSmall['хвой.']) + ', медл. ' + xn(REF.plantedSmall['медл.']) + ', быст./плод. ' + xn(REF.plantedSmall['быст.']) + ', малоценные ' + xn(REF.plantedSmall['малоц.']) + '</td></tr>' +
      '<tr><td class="c">Участок поросли деревьев</td><td class="c num">' + xn(P.treePlotPerM2) + ' за 1 м²; менее 10 м² — ' + xn(P.treePlotSmall) + ' (в оригинале умножается на площадь)</td></tr>' +
      '<tr><td class="c">Кустарники: лианы / хвойные и вечнозелёные / красивоцветущие / медленнорастущие / быстрорастущие и плодовые</td><td class="c num">до 1 м: ' +
      [sh['лиан.'], sh['хвой/вечз'], sh['крас.'], sh['медл..'], sh['быст/плод']].map(function (x) { return xn(x[0][1]); }).join(' / ') + '; от 1,1 м: ' +
      [sh['лиан.'], sh['хвой/вечз'], sh['крас.'], sh['медл..'], sh['быст/плод']].map(function (x) { return xn(x[1][1]); }).join(' / ') + '</td></tr>' +
      '<tr><td class="c">Живая изгородь, за 1 п.м.</td><td class="c num">однорядная ' + xn(P.hedge1PerM) + ', двухрядная ' + xn(P.hedge2PerM) + '</td></tr>' +
      '<tr><td class="c">Участок поросли кустарников</td><td class="c num">' + xn(P.shrubPlotPerM2) + ' за 1 м²; менее 10 м² — ' + xn(P.shrubPlotSmall) + '</td></tr>' +
      '<tr><td class="c">Качественное состояние (множитель)</td><td class="c num">хор. 1; уд. 0,75; плох. 0,5; ненадл. 0,25; поросль у дорог 0,1</td></tr>' +
      '</tbody></table></div></div>';
  }

  // ------------------------------------------------------------------ help
  function renderHelp() {
    $('#main').innerHTML = '<section class="view">' + head('Справка') + '<div class="grid2">' +
      '<div class="panel"><h3>Как вносить деревья</h3><ul class="hint" style="padding-left:18px">' +
      '<li><b>Кол-во</b> — штук; для участка поросли — площадь в м², для живой изгороди — длина в п.м. Пусто — 1 шт.</li>' +
      '<li><b>№ нач.</b> — начальный номер по плану, если нумерация с разрывом. Иначе номера идут подряд.</li>' +
      '<li><b>Диаметр</b>: целое число см; многоствольное — через запятую «20,15,12»; кустарник и изгородь — «-»; поросль деревьев — «0-4»; мелкие: «до1», «до4»; высаженные для озеленения: «**до1», «**до4».</li>' +
      '<li><b>Действие</b>: вырубить / пересадить / сохранить. Для вырубки обязательна причина.</li>' +
      '<li><b>Инд. К</b>: номера коэффициентов, меняемых для строки на обратные, например «24»; или «без компенcации».</li></ul></div>' +
      '<div class="panel"><h3>Файлы</h3><ul class="hint" style="padding-left:18px">' +
      '<li><b>Сохранить</b> — файл проекта .json; его открывают веб- и настольная версии.</li>' +
      '<li><b>Импорт из ТАКСАЦИЯ-АВТО</b> — открывает .xlsm старой программы и переносит ведомость, настройки, причины, цветники и газоны.</li>' +
      '<li><b>Скачать Excel</b> — все ведомости в одной книге .xlsx.</li>' +
      '<li>Проект автоматически сохраняется в этом браузере.</li></ul></div>' +
      '<div class="panel" style="grid-column:1/-1"><h3>Особенности расчёта, перенесённые из оригинала без изменений</h3><ul class="hint" style="padding-left:18px">' +
      '<li>Участок поросли менее 10 м²: норма «менее 10 м²» (2 баз.вел.) умножается на площадь группы.</li>' +
      '<li>Поросль включается в итог выплат дважды: в строке «уч-ки поросли» и в «в т.ч. за лиственные декоративные» (по типу «поросль → тип»).</li>' +
      '<li>Коэффициент 0,1 для поросли «у дорог» не попадает в итоговый расчёт поросли.</li>' +
      '<li>Масса кустарника (тонны) считается только для строк с количеством 1 и для деревьев с суммой тонких стволов до 8 см.</li>' +
      '<li>Ошибка в одной строке у оригинала даёт #ЗНАЧ! во всех итогах; здесь строка помечается, а итоги считаются без неё.</li>' +
      '</ul></div></div></section>';
  }

  // ------------------------------------------------------------------ files
  function download(data, name, type) {
    var blob = data instanceof Blob ? data : new Blob([data], { type: type || 'application/octet-stream' });
    var a = document.createElement('a');
    a.href = URL.createObjectURL(blob); a.download = name;
    document.body.appendChild(a); a.click();
    setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 1500);
    toast('Файл «' + name + '» сохранён в загрузки браузера.');
  }
  function saveProject() {
    var p = JSON.parse(JSON.stringify(project)); delete p._example;
    download(JSON.stringify(p, null, 1), fileBase() + '.taks.json', 'application/json');
  }
  function openFile(file) {
    var name = file.name.toLowerCase();
    var reader = new FileReader();
    if (/\.(xlsm|xlsx)$/.test(name)) {
      reader.onload = function () {
        var ab = reader.result;
        toast('Читаю «' + file.name + '»…');
        XlsxIO.readZip(ab, ['xl/workbook.xml', 'xl/_rels/workbook.xml.rels']).then(function (f0) {
          var need = TaksImport.neededFiles(f0).need;
          return XlsxIO.readZip(ab, need).then(function (f) { return Object.assign(f, f0); });
        }).then(function (files) {
          var r = TaksImport.parse(files, REF);
          project = normalize(r.project);
          project.trees.push(emptyRow(), emptyRow());
          recalc(); go('trees');
          toast('Импортировано строк: ' + r.count + '. ' + r.warnings.join(' '));
        }).catch(function (e) { toast('Не удалось импортировать: ' + e.message); });
      };
      reader.readAsArrayBuffer(file);
    } else {
      reader.onload = function () {
        try {
          var p = JSON.parse(reader.result);
          if (!p.trees && !p.settings) throw new Error('это не файл проекта');
          project = normalize(p); recalc(); go(view); toast('Открыт проект «' + file.name + '».');
        } catch (e) { toast('Не удалось открыть: ' + e.message); }
      };
      reader.readAsText(file, 'utf-8');
    }
  }
  function copyText(text) {
    var done = function () { toast('Таблица скопирована — вставьте в Excel или Word.'); };
    try {
      navigator.clipboard.writeText(text).then(done, fallback);
    } catch (e) { fallback(); }
    function fallback() {
      var ta = document.createElement('textarea'); ta.value = text; document.body.appendChild(ta); ta.select();
      try { document.execCommand('copy'); done(); } catch (e2) { toast('Выделите таблицу и скопируйте вручную.'); }
      ta.remove();
    }
  }

  // ------------------------------------------------------------------ misc ui
  var toastTimer = null;
  function toast(msg) {
    var el = $('#toast');
    el.textContent = msg; el.hidden = false;
    clearTimeout(toastTimer); toastTimer = setTimeout(function () { el.hidden = true; }, 4500);
  }
  function askConfirm(sel, text, onYes) {
    var box = $(sel);
    box.innerHTML = '<div class="confirm"><span>' + esc(text) + '</span><button class="btn danger" type="button" data-yes="1">Да, удалить</button><button class="btn" type="button" data-no="1">Отмена</button></div>';
    box.onclick = function (ev) {
      var b = ev.target.closest('button'); if (!b) return;
      box.innerHTML = ''; box.onclick = null;
      if (b.dataset.yes) onYes();
    };
  }
  function refreshLists() {
    var names = REF.special.map(function (s) { return s.name; }).concat(REF.species.map(function (s) { return s.name; }))
      .concat((project.customSpecies || []).map(function (s) { return s.name; }).filter(Boolean));
    var seen = {};
    $('#dl-species').innerHTML = names.filter(function (n) { var k = n.toLowerCase(); if (seen[k]) return false; seen[k] = 1; return true; })
      .map(function (n) { return '<option value="' + esc(n) + '"></option>'; }).join('');
    $('#dl-reasons').innerHTML = project.settings.reasons.filter(function (r) { return r.name; }).map(function (r) { return '<option value="' + esc(r.name) + '"></option>'; }).join('');
    $('#dl-krepl').innerHTML = ['1', '2', '3', '4', '12', '24', '1234', E.NO_COMP].map(function (v) { return '<option value="' + esc(v) + '"></option>'; }).join('');
  }
  function showErrors() {
    if (!res.errors.length) return;
    var html = '<div class="panel" id="err-panel"><h3>Ошибки в данных</h3><ul class="errlist">' + res.errors.slice(0, 300).map(function (e) {
      var where = e.section === 'trees' ? 'строка ' + (e.row + 1) + (e.no ? ' (№ ' + e.no + ')' : '') : ({ flowers: 'цветники', lawns: 'газоны', grass: 'трав. покров' }[e.section] + ', строка ' + (e.row + 1));
      return '<li><a href="#' + (e.section === 'trees' ? 'trees' : 'areas') + '" data-goto="' + e.section + ':' + e.row + '">' + esc(where) + '</a> — ' + esc(e.msg) + '</li>';
    }).join('') + '</ul><div class="grid-foot" style="margin-top:8px"><button class="btn" type="button" id="err-close">Закрыть</button></div></div>';
    var old = $('#err-panel'); if (old) old.remove();
    $('#main').insertAdjacentHTML('afterbegin', html);
    $('#err-close').onclick = function () { $('#err-panel').remove(); };
    $('#err-panel').addEventListener('click', function (ev) {
      var a = ev.target.closest('a[data-goto]'); if (!a) return;
      ev.preventDefault();
      var pr = a.dataset.goto.split(':');
      go(pr[0] === 'trees' ? 'trees' : 'areas'); location.hash = pr[0] === 'trees' ? 'trees' : 'areas';
      setTimeout(function () {
        var el = pr[0] === 'trees' ? document.getElementById('tr-' + pr[1] + '-species') : document.getElementById('ar-' + pr[0] + '-' + pr[1] + '-area');
        if (el) { el.scrollIntoView({ block: 'center' }); el.focus(); }
      }, 30);
    });
  }

  // ------------------------------------------------------------------ boot
  function boot() {
    $('#btn-new').onclick = function () { askConfirm('#top-confirm', 'Начать новый проект? Несохранённые данные текущего проекта будут заменены.', function () { project = blankProject(); recalc(); go('object'); location.hash = 'object'; }); };
    $('#btn-example').onclick = function () { project = exampleProject(); recalc(); go('trees'); location.hash = 'trees'; toast('Загружен пример из файла ТАКСАЦИЯ-АВТО 4.2.'); };
    $('#btn-open').onclick = function () { $('#file').click(); };
    $('#file').onchange = function () { if (this.files[0]) openFile(this.files[0]); this.value = ''; };
    $('#btn-save').onclick = saveProject;
    $('#btn-xlsx').onclick = exportXlsx;
    $('#summary').onclick = function (ev) { if (ev.target.closest('#btn-errors')) showErrors(); };
    document.addEventListener('dragover', function (ev) { ev.preventDefault(); });
    document.addEventListener('drop', function (ev) { ev.preventDefault(); var f = ev.dataTransfer.files[0]; if (f) openFile(f); });
    refreshLists();
    recalc();
    var start = location.hash.slice(1);
    if (!VIEWS[start]) { try { start = localStorage.getItem(STORE_KEY + '-view') || 'trees'; } catch (e) { start = 'trees'; } }
    go(start);
    if (project._example) toast('Открыт пример (данные из исходного файла). Нажмите «Новый проект», чтобы начать свой.');
  }
  boot();
})();
