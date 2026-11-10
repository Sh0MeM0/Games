/*
 * Таксация Лайт — расчётное ядро.
 *
 * Независимая реализация расчётов программы «ТАКСАЦИЯ-АВТО 4.2» (файл .xlsm),
 * повторяющая её формулы. Возле каждого шага указано, какую ячейку или
 * формулу исходника он повторяет: «УДд!AR3», «СУЩд!O3» и т.п.
 * Справочники (породы, тарифы, таблицы объёмов, базовые величины) берутся
 * из reference.json, выгруженного из того же файла.
 *
 * Работает в браузере (глобальный объект TaksEngine) и в Node.js (module.exports).
 */
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else root.TaksEngine = factory();
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  // породы!H3:H7 — типы деревьев, породы!H9:H17 — типы кустарников
  var TREE_TYPES = ['хвой.', 'медл.', 'быст.', 'плод.', 'малоц.'];
  var SHRUB_TYPES = ['лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..', 'ж.и.1', 'ж.и.2'];
  // породы!A2:A7
  var SP = {
    treePlot: 'Уч-к поросли дер.', shrubPlot: 'Уч-к поросли куст.',
    treePlotRoad: '*Уч-к поросли дер. у дорог', shrubPlotRoad: '*Уч-к поросли куст. у дорог',
    hedge1: 'Однорядная живая изгородь', hedge2: 'Двухрядная живая изгородь'
  };
  var NO_COMP = 'без компенcации';            // СУЩд!V15 (в оригинале латинская «c»)
  var NO_COMP_RE = /без\s*компен[сc]ации/i;
  var ERR_SHRUB_D = 'ОШИБКА: Ø куста = только "-"';   // СУЩд!V59
  var ERR_TREE_D = 'ОШИБКА: Ø дерева ≠ "-"';          // СУЩд!V62
  var LABELS = ['Д.хвой/вечз. ', 'Д.лис.м.р. ', 'Д.лис.б.р. ', 'К.древ.лиан. ', 'К.хвой/вечз. ',
    'К.лис.крас.цв. ', 'К.лис.м.р. ', 'К.л.б.р./плод. ', 'жив.изг.1р. ', 'жив.изг.2р. ']; // УДд!Z3
  var LABELS_G = ['Д.хв./вчз. ', 'Д.мр. ', 'Д.бр. ', 'К.дл. ', 'К.хв./вчз. ', 'К.кц. ', 'К.мр. ',
    'К.бр./пл. ', 'ж.изг.1р. ', 'ж.изг.2р. '];                                          // УДд!G3
  var UNITS = ['баз.в.', 'баз.в. (руб.)', 'руб.'];                                    // СУЩд!V3:V5

  // ---------------------------------------------------------------- utils
  function lc(s) { return String(s == null ? '' : s).toLowerCase(); }
  function isBlank(v) { return v === null || v === undefined || v === ''; }
  function eqi(a, b) { return lc(a) === lc(b); }               // сравнение «=» в Excel без учёта регистра
  function has(s, sub) { return String(s == null ? '' : s).indexOf(String(sub)) >= 0; }

  /** Округление как ROUND в Excel (половина — от нуля). */
  function xround(v, n) {
    var p = Math.pow(10, n || 0), s = v < 0 ? -1 : 1, x = Math.abs(v);
    return s * Math.round(Number((x * p).toPrecision(15))) / p;
  }
  function xroundup(v, n) {
    var p = Math.pow(10, n || 0), s = v < 0 ? -1 : 1, x = Math.abs(v);
    return s * Math.ceil(Number((x * p).toPrecision(15)) - 1e-9) / p;
  }
  /** Число → текст, как при склейке «&» в русском Excel (общий формат, запятая). */
  function xnum(v) {
    if (typeof v !== 'number') return v == null ? '' : String(v);
    if (!isFinite(v)) return String(v);
    var s = String(Number(v.toPrecision(15)));
    if (/e/i.test(s)) s = v.toFixed(12).replace(/0+$/, '').replace(/\.$/, '');
    return s.replace('.', ',');
  }
  /** FIXED(v, n) — с разделителем тысяч (неразрывный пробел) и запятой. */
  function xfixed(v, n) {
    if (n === undefined) n = 2;
    var r = xround(v, n), neg = r < 0, s = Math.abs(r).toFixed(n), parts = s.split('.');
    parts[0] = parts[0].replace(/\B(?=(\d{3})+(?!\d))/g, '\u00a0');
    return (neg ? '-' : '') + parts.join(',');
  }
  /** VALUE(text) в русской локали: «12», «0,8», « 4», «50%». Возвращает NaN, если не число. */
  function ruValue(t) {
    if (typeof t === 'number') return t;
    var s = String(t == null ? '' : t).replace(/[\s\u00a0]/g, '');
    if (s === '') return NaN;
    var pct = false;
    if (s.charAt(s.length - 1) === '%') { pct = true; s = s.slice(0, -1); }
    if (!/^[-+]?(\d+([.,]\d*)?|[.,]\d+)$/.test(s)) return NaN;
    var v = parseFloat(s.replace(',', '.'));
    return pct ? v / 100 : v;
  }
  function num(v) { var x = ruValue(v); return isNaN(x) ? 0 : x; }
  /** Число из поля ввода (кол-во, высота, площадь); пустое → null. */
  function inNum(v) {
    if (isBlank(v)) return null;
    var x = ruValue(v);
    return isNaN(x) ? null : x;
  }
  /** Текст ячейки так, как его видит формула (число → «20», «0,8»). */
  function cellText(v) { return typeof v === 'number' ? xnum(v) : (v == null ? '' : String(v)); }
  /** MATCH(v, thresholds, 1): индекс последнего порога ≤ v, иначе -1. */
  function matchApprox(arr, v) {
    var idx = -1;
    for (var i = 0; i < arr.length; i++) { if (arr[i] <= v) idx = i; else break; }
    return idx;
  }
  function sum(a) { var s = 0; for (var i = 0; i < a.length; i++) s += a[i] || 0; return s; }

  // ---------------------------------------------------------------- reference helpers
  function prepareRef(ref) {
    if (ref._prepared) return ref;
    var byName = {};
    ref.species.forEach(function (s) { byName[lc(s.name)] = s; });
    var special = {};
    ref.special.forEach(function (s, i) { special[lc(s.name)] = { name: s.name, type: s.type, pos: i + 1 }; });
    var typeK = {};
    ref.types.forEach(function (t) { typeK[t.type] = t.k; });
    ref._byName = byName; ref._special = special; ref._typeK = typeK;
    ref._prepared = true;
    return ref;
  }
  /** Тип породы, как породы!C:C (служебные строки + библиотека). */
  function speciesInfo(ref, name) {
    var k = lc(name), sp = ref._special[k], lib = ref._byName[k];
    if (sp) return { found: true, special: true, type: sp.type, lib: lib || null };
    if (lib) return { found: true, special: false, type: lib.type, lib: lib };
    return { found: false, special: false, type: '', lib: null };
  }

  /** Базовая величина на дату: INDEX(базовая!B:B, MATCH(дата, базовая!A:A, 1)). */
  function baseValueFor(date, table) {
    var rows = (table || []).filter(function (r) { return r && r.from && !isBlank(r.rub); })
      .slice().sort(function (a, b) { return a.from < b.from ? -1 : a.from > b.from ? 1 : 0; });
    var d = String(date || ''), found = null;
    rows.forEach(function (r) { if (r.from <= d) found = r; });
    return found;
  }

  function defaultSettings(ref) {
    var d = ref.defaults || {};
    var compMap = {};
    TREE_TYPES.concat(SHRUB_TYPES).forEach(function (t) { compMap[t] = ''; });
    Object.keys(ref.compMap || {}).forEach(function (k) { compMap[k] = ref.compMap[k]; });
    return {
      date: d.date || new Date().toISOString().slice(0, 10),
      baseValue: null,
      k1: d.K1 !== 'нет', k2: d.K2 === 'да', k3: d.K3 === 'да', k4: d.K4 === 'да',
      money: true,
      unit: d.unit || 'баз.в. (руб.)',
      view: d.formulaView || 'формула + итог',
      defaultCond: d.defaultCondition || 'уд.',
      shrubMassKg: d.shrubMassKg != null ? d.shrubMassKg : 5,
      showPlots: d.showPlots !== 'нет', hedgePieces: d.hedgeAsPieces !== 'нет',
      flowerMode: d.flowerOffset || 'метрами',
      reasons: (ref.reasons || []).map(function (r) { return { name: r.name, group: r.group || '', replace: r.replace || '' }; }),
      compMap: compMap
    };
  }

  // ---------------------------------------------------------------- coefficients K1..K4
  /** СУЩд!O3 / ЦвГаТр!R2 — строка коэффициентов «   1; 0,5;   1;   1». */
  function coefficients(settings, reasonName, kRepl) {
    var reason = null;
    (settings.reasons || []).forEach(function (r) { if (!reason && eqi(r.name, reasonName)) reason = r; });
    var rep = reason ? String(reason.replace == null ? '' : reason.replace) : '';
    var ind = kRepl == null ? '' : String(kRepl);
    if (NO_COMP_RE.test(ind) || NO_COMP_RE.test(rep)) {
      return { k: [0, 0, 0, 0], zero: true, str: '   0;   0;   0;   0', reason: reason };
    }
    function same(d) { return has(ind, d) === has(rep, d); }
    var k1 = settings.k1 ? (same(1) ? 2 : 1) : (same(1) ? 1 : 2);
    var k2 = settings.k2 ? (same(2) ? 0.5 : 1) : (same(2) ? 1 : 0.5);
    var k3 = settings.k3 ? (same(3) ? 0.5 : 1) : (same(3) ? 1 : 0.5);
    var k4 = settings.k4 ? (same(4) ? 0.1 : 1) : (same(4) ? 1 : 0.1);
    var f = function (v, last) { return (v === 1 ? '   1' : v === 2 ? '   2' : v === 0.5 ? ' 0,5' : ' 0,1') + (last ? '' : ';'); };
    return { k: [k1, k2, k3, k4], zero: false, str: f(k1) + f(k2) + f(k3) + f(k4, true), reason: reason };
  }

  // ---------------------------------------------------------------- stems (УДд!AT3:BU3)
  /** Разбор диаметра как УДд!AT3 + AU3:BU3 (до 27 стволов). Пустые позиции — null. */
  function parseStems(diam) {
    var out0 = [];
    if (typeof diam === 'number') {                 // числовой диаметр — один ствол (в т.ч. дробный)
      out0.push(diam); while (out0.length < 27) out0.push(null);
      return out0;
    }
    var s = cellText(diam).replace(/ /g, '');
    var letters = 'abcdefghijklmnopqrstuvwxyz';
    for (var i = 0; i < 26; i++) s = s.replace(',', letters.charAt(i));      // SUBSTITUTE(...,",",x,1)
    s = s.split('-').join('  ').split('*').join('').split('до1').join('0,8').split('до4').join('3,8');
    var seps = letters.split('').concat(['.']);                                  // AU1..BU1 = a..z, «.»
    var out = [];
    for (var k = 0; k < 27; k++) {
      var seg;
      if (k === 0) {
        var p0 = s.indexOf(seps[0]);
        seg = p0 >= 0 ? s.slice(0, p0) : s;
      } else {
        var a = s.indexOf(seps[k - 1]);
        if (a < 0) { out.push(null); continue; }
        var b = s.indexOf(seps[k]);
        seg = b >= 0 ? s.slice(a + 1, b) : s.slice(a + 1);
        if (b >= 0 && b < a) { out.push(null); continue; }
      }
      var v = ruValue(seg.slice(-3));                                             // VALUE(RIGHT(seg,3))
      out.push(isNaN(v) ? null : v);
    }
    return out;
  }

  // ---------------------------------------------------------------- per-row type code (УДд!J3)
  function typeCode(name, qty, diamRaw, N, shrubRow) {
    var lname = lc(name);
    var E = cellText(diamRaw).replace(/ /g, '');
    if (!shrubRow) {
      if (has(lname, lc(SP.treePlot))) return qty > 9.99 ? 60 : 70;
      var numericE = typeof diamRaw === 'number';
      if (!has(E, 'до1') || (numericE && diamRaw > 0.99)) {
        if (N === 'хвой.') return 10;
        if (N === 'медл.') return 20;
        if (N === 'быст.' || N === 'плод.') return 30;
        if (N === 'малоц.') {
          var t = E.split('1,').join('').split('2,').join('').split('3,').join('');
          var v = numericE ? diamRaw : ruValue(t); if (isNaN(v)) v = 4;
          if (!has(E, 'до4') && v > 3.99) return 40;
          if (has(E, '**до4')) return 50;
        }
        return 1;
      }
      if ((N === 'хвой.' || N === 'медл.' || N === 'быст.' || N === 'плод.') && has(E, '**до1')) return 50;
      return 1;
    }
    if (has(lname, lc(SP.shrubPlot))) return qty > 9.99 ? 18 : 19;
    if (has(lname, lc(SP.hedge1))) return 16;
    if (has(lname, lc(SP.hedge2))) return 17;
    if (N === 'лиан.') return 11;
    if (N === 'хвой..' || N === 'вечз.') return 12;
    if (N === 'крас.') return 13;
    if (N === 'медл..') return 14;
    if (N === 'быст..' || N === 'плод..') return 15;
    return 1;
  }

  // ---------------------------------------------------------------- tariffs
  function treeTable(ref, J) {   // CHOOSE(MATCH(J,{10,20,30,40}),...)
    var t = ref.tariffTrees;
    return J === 10 ? t['хвой.'] : J === 20 ? t['медл.'] : J === 30 ? t['быст.'] : J === 40 ? t['малоц.'] : null;
  }
  function tariffLookup(table, d) {
    var i = matchApprox(table.map(function (r) { return r[0]; }), d);
    return i < 0 ? null : table[i][1];
  }
  var SHRUB_H_KEY = { 11: 'лиан.', 12: 'хвой/вечз', 13: 'крас.', 14: 'медл..', 15: 'быст/плод' };

  /** УДд!AR3 и текст УДд!AS3: стоимость удаляемого растения в базовых величинах (до коэффициентов). */
  function tariff(ref, J, stems, AQ, height, N) {
    if (J === 1) return { v: 0, text: '0' };
    var tbl = treeTable(ref, J), i, parts, total, d, x;
    if (AQ !== '' && AQ > 1) {                       // многоствольное: сумма по стволам ≥ 12 см
      if (!tbl) return { v: null, text: '===', err: 'многоствольное дерево вне таблицы' };
      parts = []; total = 0;
      for (i = 0; i < stems.length; i++) {
        d = stems[i];
        if (d === null || d < 12) continue;
        x = tariffLookup(tbl, d);
        if (x === null) continue;
        total += x; parts.push(xnum(x));
      }
      return { v: total, text: parts.join('+') };
    }
    var P = ref.plots;
    var fixed = { 60: P.treePlotPerM2, 70: P.treePlotSmall, 16: P.hedge1PerM, 17: P.hedge2PerM, 18: P.shrubPlotPerM2, 19: P.shrubPlotSmall };
    if (fixed[J] !== undefined) return { v: fixed[J], text: xnum(fixed[J]) };
    if (J === 50) {
      x = ref.plantedSmall[N];
      return x === undefined ? { v: null, text: '___', err: 'нет тарифа' } : { v: x, text: xnum(x) };
    }
    if (SHRUB_H_KEY[J]) {
      var h = ref.tariffShrubs[SHRUB_H_KEY[J]];
      if (typeof height !== 'number') return { v: null, text: '___', err: 'не указана высота куста' };
      i = matchApprox(h.map(function (r) { return r[0]; }), height);
      if (i < 0) return { v: null, text: '___', err: 'нет тарифа' };
      return { v: h[i][1], text: xnum(h[i][1]) };
    }
    if (tbl) {
      var mx = 0;
      stems.forEach(function (s) { if (s !== null && s > mx) mx = s; });
      x = tariffLookup(tbl, mx);
      return x === null ? { v: null, text: '___', err: 'нет тарифа' } : { v: x, text: xnum(x) };
    }
    return { v: null, text: '___', err: 'нет тарифа' };
  }

  /** Тип для компенсационных посадок: УДд!M3. */
  function compType(settings, J, N, shrubRow) {
    var map = settings.compMap || {};
    if (J === 1) return N;
    if (J === 60 || J === 70) return map.plotTree || '';
    if (J === 18 || J === 19) return map.plotShrub || '';
    if (map[N]) return map[N];
    if (N === 'крас.' || N === 'лиан.') return shrubRow ? N : 'медл.';
    return N;
  }

  /** УДд!O3:X3 — количество саженцев взамен по колонкам 10 типов. */
  function plantCols(ref, J, M, K, L) {
    var c = [null, null, null, null, null, null, null, null, null, null];
    var pt = ref.plantTrees[String(J)], ps = ref.plantShrubs[String(J)];
    function tv(col) { if (!pt) throw new Error('нет нормы посадок'); return pt[col]; }
    function sv(col) { if (!ps) throw new Error('нет нормы посадок'); return ps[col]; }
    if (K !== null) {
      if (M === 'хвой.') c[0] = J === 1 ? 0 : K * tv('хвой.');
      if (M === 'медл.') c[1] = J === 1 ? 0 : K * tv('медл.');
      c[2] = M === 'быст.' ? (J === 1 ? 0 : (J === 70 ? 1 : K) * tv('быст.')) : 0;
    }
    if (L !== null) {
      if (M === 'лиан.') c[3] = J === 1 ? 0 : L * sv('лиан.');
      if (M === 'хвой..' || M === 'вечз.') c[4] = J === 1 ? 0 : L * sv('хвой/вечз');
      if (M === 'крас.') c[5] = J === 1 ? 0 : L * sv('крас.');
      if (M === 'медл..') c[6] = J === 1 ? 0 : L * sv('медл..');
      if (M === 'быст..' || M === 'плод..') c[7] = J === 1 ? 0 : (J === 19 ? 1 : L) * sv('быст/плод');
      if (M === 'ж.и.1') c[8] = J === 1 ? 0 : L * sv('ж.и.1');
      if (M === 'ж.и.2') c[9] = J === 1 ? 0 : L * sv('ж.и.2');
    }
    return c;
  }
  function maxOf(cols) {        // MAX(O3:X3) и позиция первого максимума (MATCH(MAX,...,0))
    var mx = null, pos = -1;
    cols.forEach(function (v) { if (v !== null && (mx === null || v > mx)) mx = v; });
    if (mx === null) mx = 0;
    for (var i = 0; i < cols.length; i++) if (cols[i] !== null && cols[i] === mx) { pos = i; break; }
    return { max: mx, pos: pos };
  }

  // ---------------------------------------------------------------- wood volume (УДд!BV3:EG3)
  function volRow(ref, ce, d, H) {     // CF/DG: «всего» и «ствол» для одного ствола
    var V = ref.volumes, soft = V.soft, idx = matchApprox(soft.map(function (r) { return r[0]; }), d);
    if (idx < 0) return null;
    var t, s, row;
    function cell(r, c) { var v = r[c - 1]; return v === null || v === undefined ? 0 : v; }
    function le(a, h) { return typeof h !== 'number' ? true : a <= h; }        // число <= текст → ИСТИНА
    if (ce === 1) {
      row = soft[idx];
      if (le(cell(row, 2), H)) { t = cell(row, 4); s = cell(row, 5); } else { t = cell(row, 8); s = cell(row, 9); }
    } else if (ce === 2) {
      row = V.conif[idx];
      if (le(cell(row, 1), H)) { t = cell(row, 3); s = cell(row, 4); }
      else if (le(cell(row, 5), H)) { t = cell(row, 7); s = cell(row, 8); }
      else { t = cell(row, 11); s = cell(row, 12); }
    } else {
      row = V.hard[idx];
      if (le(cell(row, 1), H)) { t = cell(row, 3); s = cell(row, 4); } else { t = cell(row, 7); s = cell(row, 8); }
    }
    return { total: t, stem: s };
  }
  function hardnessIndex(info) {        // УДд!CE3
    if (info.special) return 1;
    var h = info.lib ? info.lib.hard : '';
    return h === 'мяг.' ? 1 : h === 'хвн.' ? 2 : h === 'твд.' ? 3 : null;
  }
  function percentFromNote(note) {      // VALUE(MID(K,2+SEARCH("V",K),...)) из УДд!BV3
    var s = note == null ? '' : String(note), i = s.search(/v/i);
    if (i < 0) return 1;
    var p = s.indexOf('%', i);
    if (p < 0) return 1;
    var len = p - i - 1;
    if (len < 0) return 1;
    var v = ruValue(s.substr(i + 2, len));
    return isNaN(v) ? 1 : v;
  }

  function wood(ref, settings, info, row, K, L, stems, H, qty) {
    var res = { ce: 0, BV: null, BW: null, BX: null, BY: null, BZ: null, CA: 0, CB: null, CC: null, CD: null, perStem: [], err: null };
    var CA = 0;
    stems.forEach(function (d) { if (d !== null && d < 8) CA += d; });
    res.CA = CA;
    var hh = row.height, Lone = (L === 1);
    if ((CA > 0.1 && CA < 8) || Lone) {                     // УДд!BV3 — масса «кустарника», т
      var h = typeof hh === 'number' ? hh : 0;
      var hk = h > 1 ? h + (h - 1) / 3 : h;
      res.BV = hk * (settings.shrubMassKg * percentFromNote(row.note)) * qty / 1000;
    }
    if (K === null) return res;
    var ce = hardnessIndex(info);
    if (ce === null) { res.err = 'у породы не задан тип твёрдости древесины'; return res; }
    res.ce = ce;
    var dens = ce === 1 ? ref.volumes.density.soft : ce === 2 ? ref.volumes.density.conif : ref.volumes.density.hard;
    var totals = 0, stemsV = 0, bz15 = 0, bz11 = 0, bz3 = 0;
    stems.forEach(function (d) {
      if (d === null) { res.perStem.push(null); return; }
      var v = volRow(ref, ce, d, H);
      res.perStem.push(v ? { d: d, total: v.total, stem: v.stem } : { d: d, total: null, stem: null });
      if (!v) return;
      totals += v.total; stemsV += v.stem;
      if (d >= 15) bz15 += v.stem; else if (d >= 11) bz11 += v.stem; else if (d >= 3) bz3 += v.stem;
    });
    var extra = 0;
    if (CA >= 8) { var vc = volRow(ref, ce, CA, H); extra = vc.total - vc.stem; }
    res.CC = totals + extra;                                    // УДд!CC3
    res.CD = stemsV;                                            // УДд!CD3
    res.CB = res.CC < res.CD ? null : res.CC - res.CD;          // УДд!CB3 — ветви, м³
    res.BX = res.CD * dens * K / 1000;                          // стволы, т
    res.BW = res.CB === null ? null : res.CB * dens * K / 1000; // ветви, т
    res.BY = res.CB === null ? null : (res.CB / 0.1) * K;       // ветви, м³ складочные
    res.BZ = K * (bz15 * (ce === 2 ? 1.49 : 1.47) + bz11 / 0.67 + bz3 / 0.6); // стволы, м³ складочные
    return res;
  }

  // ---------------------------------------------------------------- row numbering (СУЩд!B3, D3)
  function columnA(row) {
    var c = isBlank(row.count) ? '' : row.count, s = isBlank(row.start) ? '' : row.start;
    if (!isBlank(row.a)) return row.a;                  // «сырое» значение колонки A, если задано
    if (s !== '') return (c !== '' ? cellText(c) : '') + '№' + cellText(s);
    return c;
  }
  function qtyFromA(A) {
    if (isBlank(A)) return 1;
    var t = cellText(A);
    if (t.charAt(0) === '№') return 1;
    var p = t.indexOf('№');
    var v = ruValue(p >= 0 ? t.slice(0, p) : t);
    return isNaN(v) ? null : v;
  }
  function numberFromA(A, prevB) {
    var t = cellText(A), p = t.indexOf('№');
    if (!isBlank(A) && p >= 0) {
      var st = t.slice(p + 1, p + 11), stv = ruValue(st), cnt = ruValue(t.slice(0, p));
      var tail = (!isNaN(stv) && !isNaN(cnt)) ? '-' + xnum(cnt - 1 + stv) : '';
      return st + tail;
    }
    var pb = cellText(prevB), q = pb.indexOf('-');
    var prevEnd = ruValue(q >= 0 ? pb.slice(q + 1, q + 11) : pb);
    var first = isNaN(prevEnd) ? 1 : prevEnd + 1;
    if (isBlank(A)) return xnum(first);
    var av = ruValue(A);
    return xnum(first) + (isNaN(av) ? '' : '-' + xnum(av - 1 + first));
  }

  // ---------------------------------------------------------------- main: trees and shrubs
  function computeTrees(project, ref, S) {
    var rows = [], prevB = 'Номер по плану';
    (project.trees || []).forEach(function (src, i) {
      if (isBlank(src.species)) return;
      var r = { idx: i, src: src, errors: [], warnings: [] };
      var A = columnA(src);
      r.A = A;
      r.qty = qtyFromA(A);
      if (r.qty === null) { r.errors.push('количество не распознано'); r.qty = 0; }
      r.no = numberFromA(A, prevB); prevB = r.no;                         // СУЩд!B
      r.species = String(src.species).trim();
      r.info = speciesInfo(ref, r.species);
      r.N = r.info.type;                                                  // СУЩд!N
      if (!r.info.found) r.errors.push('породы нет в справочнике');
      r.height = inNum(src.height);
      r.heightRaw = src.height;
      var diam = src.diam;
      if (typeof diam === 'string' && /^\s*\d+(\.\d+)?\s*$/.test(diam)) diam = ruValue(diam);   // «20», «12.5» → число
      r.diam = diam;
      r.diamText = cellText(diam);
      r.cond = isBlank(src.cond) ? S.defaultCond : src.cond;              // СУЩд!G (по умолчанию J1)
      r.action = lc(src.action);                                          // «в» / «п» / ''
      r.note = src.note || '';
      r.reason = src.reason || '';
      r.kRepl = src.kRepl == null ? '' : src.kRepl;
      var shrubType = SHRUB_TYPES.indexOf(r.N) >= 0;
      // СУЩд!C — наименование в ведомости
      var title = r.species, nst = typeof diam === 'number' ? -1 : r.diamText.indexOf(',', 1);
      if (nst >= 1) title += ' (' + (1 + r.diamText.split(',').length - 1) + ' ств.' + (r.note ? ', ' + r.note : '') + ')';
      else if (r.note) title += ' (' + r.note + ')';
      if (has(r.species, 'поросли')) title += ', м2 ';
      else if (has(r.species, 'рядная живая')) title += ', п.м. ';
      r.title = title;
      // СУЩд!H — примечание
      if (shrubType && r.diamText !== '-') r.H = ERR_SHRUB_D;
      else if (!shrubType && r.diamText === '-') r.H = ERR_TREE_D;
      else r.H = r.action === 'в' ? 'вырубить (' + r.reason + ')' : r.action === 'п' ? 'пересадить (' + r.reason + ')' : 'сохранить';
      if (r.H === ERR_SHRUB_D || r.H === ERR_TREE_D) r.errors.push(r.H);
      r.removed = lc(r.H).indexOf('вырубить') === 0;
      r.transplanted = lc(r.H).indexOf('пересадить') === 0;
      // СУЩд!O — коэффициенты
      r.coef = null;
      if (!isBlank(r.reason) && r.action === 'в' && r.info.found) r.coef = coefficients(S, r.reason, r.kRepl);
      r.O = r.coef ? r.coef.str : '';
      if (r.removed && !r.coef) r.errors.push('не указана причина вырубки');
      rows.push(r);
    });
    return rows;
  }

  function removalRow(r, ref, S) {
    var shrubRow = r.diamText === '-';
    var K = shrubRow ? null : r.qty, L = shrubRow ? r.qty : null;
    var J = typeCode(r.species, r.qty, r.diam, r.N, shrubRow);           // УДд!J3
    var isPlot = J === 60 || J === 70 || J === 18 || J === 19;
    var F = isPlot ? '-' : r.cond;                                       // УДд!F3
    var lname = lc(r.species), Ncoef;
    if (has(lname, lc(SP.treePlotRoad)) || has(lname, lc(SP.shrubPlotRoad))) Ncoef = 0.1;  // УДд!N3
    else if (eqi(F, 'хор.') || F === '-') Ncoef = 1;
    else if (eqi(F, 'уд.')) Ncoef = 0.75;
    else if (eqi(F, 'плох.')) Ncoef = 0.5;
    else if (eqi(F, 'ненадл.')) Ncoef = 0.25;
    else { Ncoef = null; r.errors.push('неизвестное качественное состояние'); }
    var M = compType(S, J, r.N, shrubRow);                               // УДд!M3
    var stems = parseStems(r.diam);                                      // УДд!AU3:BU3
    var AQ = K !== null ? Math.max(1, stems.filter(function (d) { return d !== null && d > 11.9; }).length) : '';
    var AO = (K !== null && !has(lname, 'ч-к поросли')) ? r.N : '';     // УДд!AO3
    var invasive = !!(r.info.lib && r.info.lib.invasive && !r.info.special);
    var Hn = r.height === null ? 0 : r.height;                           // пустая ячейка в формуле Excel = 0
    var tr = tariff(ref, J, stems, AQ, Hn, r.N);                         // УДд!AR3 / AS3
    var cols;
    try { cols = plantCols(ref, J, M, K, L); } catch (e) { cols = null; }
    var mx = cols ? maxOf(cols) : { max: null, pos: -1 };
    var k = r.coef ? r.coef.k : null;
    var Y, Yerr = null;
    if (k && !r.coef.zero && isPlot) Y = '-';                            // поросль — считается отдельно
    else if (invasive) Y = 0;
    else if (!k || Ncoef === null) { Y = null; Yerr = 'нет коэффициентов'; }
    else if (S.money) {
      if (tr.v === null) { Y = null; Yerr = tr.err || 'нет тарифа'; }
      else Y = xround(tr.v * (Math.max(r.qty, 0) > 1 ? r.qty : 1) * Ncoef * k[0] * k[1] * k[2] * k[3], 3);
    } else {
      if (!cols) { Y = null; Yerr = 'нет нормы посадок'; }
      else Y = xround(mx.max * (String(AO).length < 2 ? 1 : AQ) * Ncoef * k[0] * k[1] * k[2] * k[3], 3);
    }
    if (Yerr) r.errors.push(Yerr);
    var Z = (mx.pos >= 0 ? LABELS[mx.pos] : '') + (Y === null ? '' : cellText(Y));   // УДд!Z3
    // УДд!G3 — текст «Компенсационные посадки (выплаты)»
    var G;
    if (Y === 0) G = 'Без компенсации';
    else if (Y === null) G = 'ОШИБКА: ' + (Yerr || '');
    else if (S.view === 'итог') G = S.money ? '(' + xnum(tr.v) + ') баз.в.' : Z;
    else if (S.money) {
      G = '(' + tr.text + ') баз.в. ' + (Math.max(r.qty, 0) > 1 ? ' х' + xnum(r.qty) + 'шт. ' : '') +
        'х' + xnum(Ncoef) + 'х' + xnum(k[0]) + 'х' + xnum(k[1]) + 'х' + xnum(k[2]) + 'х' + xnum(k[3]) +
        ' =' + (Y === '-' ? ' + др. уч-ки поросли' : xnum(Y));
    } else {
      G = xnum(mx.max) + ' ' + (mx.pos >= 0 ? LABELS_G[mx.pos] : '') + (AQ !== '' && AQ >= 2 ? 'х' + AQ + 'ств.' : '') +
        'х' + xnum(Ncoef) + 'х' + xnum(k[0]) + 'х' + xnum(k[1]) + 'х' + xnum(k[2]) + 'х' + xnum(k[3]) +
        ' =' + (Y === '-' ? ' + др. уч-ки поросли' : xnum(Y));
    }
    var w = wood(ref, S, r.info, r, K, L, stems, Hn, r.qty);
    if (w.err) r.warnings.push(w.err);
    return {
      J: J, K: K, L: L, F: isPlot ? '-' : r.cond, N: Ncoef, M: M, isPlot: isPlot, AO: AO, AQ: AQ,
      invasive: invasive, stems: stems, AR: tr.v, AS: tr.text, cols: cols, maxPlant: mx.max, Y: Y, Z: Z, G: G,
      E: r.diamText === '0-4' ? '-' : r.diamText,                       // УДд1!E
      Hnote: '(' + r.reason + ')', wood: w
    };
  }

  // ---------------------------------------------------------------- plots aggregation (УДд!AF2011:AJ2047)
  function plotGroups(rows, S, ref, shrub) {
    var groups = {}, order = [];
    var pattern = shrub ? 'уч-к поросли куст' : 'уч-к поросли дер';
    rows.forEach(function (r) {
      if (r.action !== 'в' || !r.coef) return;
      if (!has(lc(r.species), pattern)) return;
      var key = r.coef.str;
      if (!groups[key]) { groups[key] = { str: key, k: r.coef.k, area: 0 }; order.push(key); }
      groups[key].area += r.qty;
    });
    var AG = shrub ? (S.compMap.plotShrub || '') : (S.compMap.plotTree || '');
    var total = 0;
    var list = order.map(function (key) {
      var g = groups[key], AH;
      if (S.money) AH = shrub ? (g.area > 9.99 ? ref.plots.shrubPlotPerM2 : ref.plots.shrubPlotSmall)
                              : (g.area > 9.99 ? ref.plots.treePlotPerM2 : ref.plots.treePlotSmall);
      else if (!shrub) {
        var col = { 'хвой.': 'хвой.', 'медл.': 'медл.', 'быст.': 'быст.' }[AG];
        AH = col ? ref.plantTrees[g.area > 9.99 ? '60' : '70'][col] : null;
      } else {
        var map = { 'лиан.': 'лиан.', 'хвой..': 'хвой/вечз', 'вечз.': 'хвой/вечз', 'крас.': 'крас.', 'медл..': 'медл..',
          'быст..': 'быст/плод', 'плод..': 'быст/плод', 'ж.и.1': 'ж.и.1', 'ж.и.2': 'ж.и.2' }[AG];
        AH = map ? ref.plantShrubs[g.area > 9.99 ? '18' : '19'][map] : null;
      }
      var AF = AH === null ? 0 : g.area * AH * g.k[0] * g.k[1] * g.k[2] * g.k[3];   // ×площадь, как в оригинале
      total += AF;
      return { str: g.str, area: g.area, norm: AH, value: AF };
    });
    return { type: AG, groups: list, total: total };
  }

  // ---------------------------------------------------------------- removal totals
  function removalTotals(rows, S, ref, base) {
    var rem = rows.filter(function (r) { return r.removed; });
    var Ysum = function (pred) { return sum(rem.filter(function (r) { return pred(r) && typeof r.rm.Y === 'number'; }).map(function (r) { return r.rm.Y; })); };
    var Csum = function (pred) { return sum(rem.filter(pred).map(function (r) { return r.qty; })); };
    var tp = plotGroups(rows, S, ref, false), sp = plotGroups(rows, S, ref, true);
    var t = { treePlots: tp, shrubPlots: sp };
    var AF_t = function (type) { return tp.type === type ? tp.total : 0; };
    var AF_s = function (type) { return sp.type === type ? sp.total : 0; };
    // money (AP2011...)
    var AP = {};
    ['хвой.', 'медл.', 'быст.'].forEach(function (ty) { AP[ty] = Ysum(function (r) { return r.rm.AO === ty; }) + AF_t(ty); });
    ['плод.', 'малоц.'].forEach(function (ty) { AP[ty] = Ysum(function (r) { return r.rm.AO === ty; }); });
    AP.treePlots = tp.total;                                                  // AP2016
    AP.trees = AP['хвой.'] + AP['медл.'] + AP['быст.'] + AP['плод.'] + AP['малоц.'];  // AP2017
    ['лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..', 'ж.и.1', 'ж.и.2'].forEach(function (ty) {
      AP[ty] = Ysum(function (r) { return r.N === ty; }) + AF_s(ty);
    });
    AP.shrubs = AP['лиан.'] + AP['хвой..'] + AP['вечз.'] + AP['крас.'] + AP['медл..'] + AP['быст..'] + AP['плод..'];  // AP2042
    AP.shrubPlots = sp.total;                                                 // AP2043
    AP.total = AP.treePlots + AP.trees + AP['ж.и.1'] + AP['ж.и.2'] + AP.shrubs + AP.shrubPlots;  // G2048
    t.money = AP;
    // planting (AN2011...)
    var Zsum = function (prefix) { return Ysum(function (r) { return r.rm.Z.indexOf(prefix) === 0; }); };
    var AN = {};
    AN['хвой.'] = Zsum('Д.хв') + AF_t('хвой.');
    AN['медл.'] = Zsum('Д.лис.м') + AF_t('медл.');
    AN['быст.'] = Zsum('Д.лис.б.р') + AF_t('быст.');
    AN.trees = AN['хвой.'] + AN['медл.'] + AN['быст.'];                     // AN2017
    AN['лиан.'] = Zsum('К.древ.лиан') + AF_s('лиан.');
    AN['хвой/вечз'] = Zsum('К.хв') + AF_s('хвой..') + AF_s('вечз.');
    AN['крас.'] = Zsum('К.лис.кр') + AF_s('крас.');
    AN['медл..'] = Zsum('К.лис.м.р') + AF_s('медл..');
    AN['быст/плод'] = Zsum('К.л.б.р./плод') + AF_s('быст..') + AF_s('плод..');
    AN.hedge1 = Zsum('жив.изг.1р');
    AN.hedge2 = Zsum('жив.изг.2р');
    AN.shrubs = AN['лиан.'] + AN['хвой/вечз'] + AN['крас.'] + AN['медл..'] + AN['быст/плод'];  // AN2042
    AN.shrubsAll = AN.hedge1 + AN.hedge2 + AN.shrubs;                        // G2042 (planting)
    AN.hedge1Pcs = xroundup(AN.hedge1 * 3, 3);                               // G2048 (planting)
    AN.hedge2Pcs = xroundup(AN.hedge2 * 5, 3);                               // G2049 (planting)
    t.plant = AN;
    // counts (C2038...)
    var C = {};
    ['хвой.', 'медл.', 'быст.', 'плод.', 'малоц.'].forEach(function (ty) { C[ty] = Csum(function (r) { return r.rm.AO === ty; }); });
    C.trees = C['хвой.'] + C['медл.'] + C['быст.'] + C['плод.'] + C['малоц.'];   // AP2009
    C.leafy = C.trees - C['хвой.'] - C['плод.'];
    C.treePlots = Csum(function (r) { return has(lc(r.species), 'уч-к поросли дер') && r.rm.K !== null; });   // AP2008
    C.shrubPlots = Csum(function (r) { return has(lc(r.species), 'уч-к поросли куст.') && r.rm.L !== null; }); // AP2030
    C.hedge1 = Csum(function (r) { return r.N === 'ж.и.1'; });
    C.hedge2 = Csum(function (r) { return r.N === 'ж.и.2'; });
    var sumK = Csum(function (r) { return r.rm.K !== null; }), sumL = Csum(function (r) { return r.rm.L !== null; });
    var plotsD = Csum(function (r) { return has(lc(r.species), 'ч-к поросли дер') && r.rm.K !== null; });
    var plotsK = Csum(function (r) { return has(lc(r.species), 'ч-к поросли куст') && r.rm.L !== null; });
    var h1 = Csum(function (r) { return has(lc(r.species), 'днорядная живая изгород') && r.rm.L !== null; });
    var h2 = Csum(function (r) { return has(lc(r.species), 'вухрядная живая изгород') && r.rm.L !== null; });
    C.treesC2035 = sumK - plotsD;                                             // C2035 (до «+»)
    C.shrubsC2035 = sumL - plotsK - h1 - h2;                                  // C2035 (после «+»)
    C.plotsD = plotsD; C.plotsK = plotsK; C.h1 = h1; C.h2 = h2;
    C.shrubTypes = {};
    ['лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..'].forEach(function (ty) { C.shrubTypes[ty] = Csum(function (r) { return r.N === ty; }); });
    t.count = C;
    // presentation
    t.unit = S.unit;
    t.base = base;
    return t;
  }

  /** Подпись суммы как в УДд!G2038: «243,375 (10951,875)». unit='' — без пересчёта (УДд!H2035 пуст). */
  function moneyText(v, S, base, unit) {
    var u = unit === undefined ? S.unit : unit;
    if (u === 'руб.') return xnum(v * base);
    if (u === 'баз.в. (руб.)') return xnum(v) + ' (' + xnum(v * base) + ')';
    return xnum(v);
  }

  /** Итоговые строки ведомости удаляемых (УДд!A2037:H2049, режим «итоги построчно»). */
  function removalReport(res) {
    var S = res.settings, T = res.removal, AP = T.money, AN = T.plant, C = T.count, base = res.baseValue;
    var plant = !S.money;
    var H2035 = plant ? '' : (AN.shrubs + AN.trees === 0 ? '' : S.unit);
    var unitLbl = S.unit;                       // УДд!H2038: подпись = СУЩд!L1
    var m = function (v) { return moneyText(v, S, base, H2035); };
    var nz = function (v) { return v === 0 ? '' : v; };
    var AF28 = T.treePlots.total, AF48 = T.shrubPlots.total, AG27 = T.treePlots.type, AG48 = T.shrubPlots.type;
    var rows = [];
    function row(A, B, Cc, D, E, F, G, H) {
      var cEmpty = Cc === '' || Cc === null || Cc === undefined, gEmpty = G === '' || G === null || G === undefined;
      rows.push([cEmpty ? '' : A, cEmpty ? '' : B, cEmpty ? '' : Cc, cEmpty ? '' : D,
        gEmpty ? '' : E, gEmpty ? '' : F, gEmpty ? '' : G, gEmpty ? '' : H]);
    }
    rows.push(['-------', '-- Удаление --', '-------', '-------', '-------', plant ? '--- Компенсация ' : '-------',
      plant ? '--------------' : 'Компенсация  выплатами -----', '--------------']);
    var plotNote = function (t) { return AG27 === t ? ' (' + xnum(AF28) + ')' : ''; };
    var plotNoteS = function (ts) { return ts.indexOf(AG48) >= 0 ? ' (' + xnum(AF48) + ')' : ''; };
    row('Итого', '_________деревьев:', nz(C.trees), 'шт.', 'Итого', plant ? '_______деревьев:' : 'за деревья:',
      plant ? nz(AN.trees) : (AP.trees === 0 ? '' : m(AP.trees)), plant ? 'шт. саженцев' : unitLbl);
    row('в т.ч.', 'лиственные декор-е:', nz(C.trees - C['хвой.'] - C['плод.']), 'шт.', plant ? 'в т.ч.' : '--- в т.ч. за',
      plant ? 'хвойн./ вечнозел.:' : 'лиственные декор-е:',
      plant ? (AN['хвой.'] === 0 ? '' : xnum(AP['хвой.']) + plotNote('хвой.'))
            : (AP['медл.'] + AP['быст.'] + AP['малоц.'] === 0 ? '' : m(AP['медл.'] + AP['быст.'] + AP['малоц.'])),
      plant ? 'шт. ' + (AG27 === 'хвой.' ? '(в т.ч. за поросль)' : '') : unitLbl);
    row('в т.ч.', 'хвойные и вечнозел.:', nz(C['хвой.']), 'шт.', plant ? 'в т.ч.' : '--- в т.ч. за',
      plant ? 'листв. медл-раст-х:' : 'хвойные и вечнозел.:',
      plant ? (AN['медл.'] === 0 ? '' : xnum(AN['медл.']) + plotNote('медл.')) : (AP['хвой.'] === 0 ? '' : m(AP['хвой.'])),
      plant ? 'шт. ' + (AG27 === 'медл.' ? '(в т.ч. за поросль)' : '') : unitLbl);
    row('в т.ч.', 'плодовые:', nz(C['плод.']), 'шт.', plant ? 'в т.ч.' : '--- в т.ч. за',
      plant ? 'листв. быст-раст-х:' : 'плодовые:',
      plant ? (AN['быст.'] === 0 ? '' : xnum(AN['быст.']) + plotNote('быст.')) : (AP['плод.'] === 0 ? '' : m(AP['плод.'])),
      plant ? 'шт. ' + (AG27 === 'быст.' ? '(в т.ч. за поросль)' : '') : unitLbl);
    var c42 = nz(C.treePlots);
    row('Итого', 'уч-ки поросли дер-ев:', c42, 'м²', plant ? 'Итого' : '--- Итого за',
      plant ? '_____кустарников:' : 'уч-ки поросли дер-ев:',
      plant ? (AN.shrubsAll === 0 ? '' : xnum(AN.shrubsAll)) : (c42 === '' ? '' : m(AP.treePlots)),
      plant ? 'шт. (в т.ч. в жив. изг.)' : unitLbl);
    row('', '', '', '', plant ? 'в т.ч.' : '', plant ? 'древесных лиан:' : '',
      plant ? (AP['лиан.'] + AN['лиан.'] === 0 ? '' : xnum(AN['лиан.']) + plotNoteS(['лиан.'])) : '',
      plant ? 'шт. ' : '');
    var shrubsCnt = C.shrubTypes ? (C.hedge1 + C.hedge2 + C.shrubPlots + sum(Object.keys(C.shrubTypes).map(function (k) { return C.shrubTypes[k]; }))) : 0;
    row('Итого', '_______кустарников:', shrubsCnt === 0 ? '' : C.shrubsC2035, 'шт.', plant ? 'в т.ч.' : '--- Итого за',
      plant ? 'хвойных/вечноз-х:' : 'кустарники:',
      plant ? (AN['хвой/вечз'] === 0 ? '' : xnum(AN['хвой/вечз']) + plotNoteS(['хвой..', 'вечз.'])) : (AP.shrubs === 0 ? '' : m(AP.shrubs)),
      plant ? 'шт. ' : unitLbl);
    row('Итого', 'уч-ки поросли куст-ов:', nz(C.shrubPlots), 'м²', plant ? 'в т.ч.' : '--- Итого за',
      plant ? 'красивоцветущих:' : 'поросль куст.',
      plant ? (AN['крас.'] === 0 ? '' : xnum(AN['крас.']) + plotNoteS(['крас.'])) : (AP.shrubPlots === 0 ? '' : m(AP.shrubPlots)),
      plant ? 'шт. ' : unitLbl);
    row('Итого', '1-рядн. жив. изгородь:', nz(C.hedge1), 'п.м.', plant ? 'в т.ч.' : '--- Итого за',
      plant ? 'медл-растущих:' : '1-рядн. ж.изг.',
      plant ? (AN['медл..'] === 0 ? '' : xnum(AN['медл..']) + plotNoteS(['медл..'])) : (AP['ж.и.1'] === 0 ? '' : m(AP['ж.и.1'])),
      plant ? 'шт. ' : unitLbl);
    row('Итого', '2-рядн. жив. изгородь:', nz(C.hedge2), 'п.м.', plant ? 'в т.ч.' : '--- Итого за',
      plant ? 'быстр-х/плод-х:' : '2-рядн. ж.изг.',
      plant ? (AN['быст/плод'] === 0 ? '' : xnum(AN['быст/плод']) + plotNoteS(['быст..', 'плод..'])) : (AP['ж.и.2'] === 0 ? '' : m(AP['ж.и.2'])),
      plant ? 'шт. ' : unitLbl);
    var g48 = plant ? (AN.hedge1 === 0 ? '' : xnum(AN.hedge1Pcs) + plotNoteS(['ж.и.1'])) : m(AP.total);
    rows.push(['', '', '', '', plant ? (g48 === '' ? '' : 'в т.ч.') : 'ИТОГО всех', plant ? (g48 === '' ? '' : '1-рядн. жив. изг.:') : 'выплат:',
      g48, g48 === '' ? '' : (plant ? 'шт. ' : unitLbl)]);
    var g49 = plant ? (AN.hedge2 === 0 ? '' : xnum(AN.hedge2Pcs) + plotNoteS(['ж.и.2'])) : '';
    rows.push(['', '', '', '', g49 === '' ? '' : 'в т.ч.', g49 === '' ? '' : '2-рядн. жив. изг.:', g49, g49 === '' ? '' : 'шт. ']);
    return rows;
  }

  /** Таблица БАЛАНСЫ!B5:H8 в виде текстов, как в оригинале. */
  function balanceReport(res) {
    var b = res.balance, P = b.showPlots, Hp = b.hedgePieces;
    function tr(o) { return P ? xnum(o.trees) + ' + ' + xnum(o.treePlots) : xnum(o.trees); }
    function sh(o) { return P ? xnum(o.shrubs) + ' + ' + xnum(o.shrubPlots) : xnum(o.shrubs); }
    function h1(o) { return Hp ? xnum(o.hedge1) + ' / ' + xnum(o.hedge1 * 3) : xnum(o.hedge1); }
    function h2(o) { return Hp ? xnum(o.hedge2) + ' / ' + xnum(o.hedge2 * 5) : xnum(o.hedge2); }
    return [['Сохраняемые', b.kept], ['Пересаживаемые', b.transplanted], ['Удаляемые', b.removed], ['Итого', b.total]].map(function (x) {
      var o = x[1];
      return [x[0], tr(o), o.leafy, o.fruit, o.conif, sh(o), h1(o), h2(o)];
    });
  }

  // ---------------------------------------------------------------- volumes summary (УДд!AE72:AM83, БАЛАНСЫ)
  function volumeTotals(rows, ref) {
    var rem = rows.filter(function (r) { return r.removed; });
    var by = { 1: {}, 2: {}, 3: {} };
    var acc = function (key) {
      var o = { 1: 0, 2: 0, 3: 0, all: 0 };
      rem.forEach(function (r) {
        var w = r.rm.wood, v = w[key];
        if (typeof v === 'number') { o.all += v; if (w.ce) o[w.ce] += v; }
      });
      return o;
    };
    var BZ = acc('BZ'), BX = acc('BX'), BY = acc('BY'), BW = acc('BW'), BV = acc('BV');
    // > 50 см (УДд!AF73): объёмы ствола без умножения на количество, как в оригинале
    var big = { 1: 0, 2: 0, 3: 0 };
    rem.forEach(function (r) {
      var w = r.rm.wood;
      if (!w.ce) return;
      w.perStem.forEach(function (p) { if (p && p.d > 50 && typeof p.stem === 'number') big[w.ce] += p.stem; });
    });
    big[1] *= 1.47; big[2] *= 1.49; big[3] *= 1.47;           // УДд!AF73:AH73 — ×1,47 / ×1,49
    var buckets = [['до 12', null, 12], [' 12-16', 12, 16], [' 16-20', 16, 20], [' 20-24', 20, 24], [' 24-28', 24, 28], [' 28-32', 28, 32], ['более 32', 32, null]];
    var stemsTable = buckets.map(function (b, bi) {
      var o = { label: b[0], 1: 0, 2: 0, 3: 0 };
      rem.forEach(function (r) {
        var w = r.rm.wood;
        if (!w.ce || r.rm.K === null) return;
        r.rm.stems.forEach(function (d, si) {
          if (d === null) return;
          if (bi === 0 && si === 0 && !(r.rm.J < 59)) return;
          var ok = (b[1] === null || d > b[1]) && (b[2] === null || d <= b[2]);
          if (ok) o[w.ce] += r.rm.K;
        });
      });
      o.all = o[1] + o[2] + o[3];
      return o;
    });
    var dens = ref.volumes.density.soft;
    return {
      stemsM3: BZ, stemsT: BX, branchesM3: BY, branchesT: BW, shrubT: BV,
      stumpsM3: { 1: BZ[1] * 0.2, 2: BZ[2] * 0.2, 3: BZ[3] * 0.2, all: BZ.all * 0.2 },
      stumpsT: { 1: BX[1] * 0.2, 2: BX[2] * 0.2, 3: BX[3] * 0.2, all: BX.all * 0.2 },
      shrubM3: ((BV.all * 1000) / dens) / 0.1,
      le50: { 1: BZ[1] - big[1], 2: BZ[2] - big[2], 3: BZ[3] - big[3] }, gt50: big,
      stemsByDiameter: stemsTable,
      // БАЛАНСЫ!K9:N10 (с округлением как в оригинале)
      table: {
        m3: [xround(BZ.all, 2), xround(BZ.all * 0.2, 2), xround(BY.all, 2), xround(((BV.all * 1000) / dens) / 0.1, 2)],
        t: [xround(BX.all, 3), xround(BX.all * 0.2, 2), xround(BW.all, 2), xround(BV.all, 2)]
      }
    };
  }

  // ---------------------------------------------------------------- transplant (ПЕРЕд)
  function ballSize(ref, info, height) {     // ПЕРЕд!G3 и Библ.ОРМ!K:P
    var lib = info.lib;
    if (!lib) return '';
    var h = typeof height === 'number' ? height : (isBlank(height) ? 0 : Infinity);
    var col = h > 5 ? 6 : h > 4 ? 5 : h > 2 ? 4 : h > 1.5 ? 3 : h > 0.99 ? 2 : 1;
    var F = lib.ballD, G = lib.ballH;
    if (col === 1) return 'Ø' + xnum(F == null ? '' : F) + '; h' + xnum(G == null ? '' : G);
    var k = ref._typeK[lib.type];
    if (!k) return '';
    var kd = k[(col - 2) * 2], kh = k[(col - 2) * 2 + 1];
    return 'Ø' + xnum(xround((F || 0) * kd, 1)) + '; h' + xnum(xround((G || 0) * kh, 1));
  }
  function workGroup(S, reason) {
    var g = '';
    (S.reasons || []).forEach(function (r) { if (!g && eqi(r.name, reason)) g = r.group || ''; });
    return g;
  }
  function transplantTotals(rows, ref, S) {
    var tr = rows.filter(function (r) { return r.transplanted; });
    tr.forEach(function (r) {
      r.ball = ballSize(ref, r.info, r.height);
      r.group = workGroup(S, r.reason);
      r.trType = (r.action === 'п' && r.diamText !== '-' && !has(r.species, 'ч-к поросли')) ? r.N : '';   // ПЕРЕд!J3
    });
    function calc(list) {
      var s = function (pred) { return sum(list.filter(pred).map(function (r) { return r.qty; })); };
      var h1 = s(function (r) { return has(lc(r.species), 'днорядная живая изгород'); });
      var h2 = s(function (r) { return has(lc(r.species), 'вухрядная живая изгород'); });
      var shrubs = s(function (r) { return r.diamText === '-'; }) - h1 - h2;
      var trees = s(function () { return true; }) - shrubs - h1 - h2 -
        s(function (r) { return has(lc(r.species), 'ч-к поросли дер'); }) - s(function (r) { return has(lc(r.species), 'ч-к поросли куст'); });
      return { trees: trees, shrubs: shrubs, hedge1: h1, hedge2: h2 };
    }
    var tot = calc(tr);
    var byType = {};
    TREE_TYPES.forEach(function (ty) { byType[ty] = sum(tr.filter(function (r) { return r.trType === ty; }).map(function (r) { return r.qty; })); });
    tot.byType = byType;
    var groups = [];
    (S.reasons || []).forEach(function (rs) {
      if (!rs.group || groups.some(function (g) { return g.group === rs.group; })) return;
      var list = tr.filter(function (r) { return r.group === rs.group; });
      if (list.length) { var c = calc(list); c.group = rs.group; groups.push(c); }
    });
    tot.groups = groups;
    tot.rows = tr;
    return tot;
  }

  // ---------------------------------------------------------------- existing statement totals (СУЩд «Итого:»)
  function existingTotals(rows) {
    var s = function (pred) { return sum(rows.filter(pred).map(function (r) { return r.qty; })); };
    var plotsD = s(function (r) { return has(lc(r.species), 'ч-к поросли дер'); });
    var plotsK = s(function (r) { return has(lc(r.species), 'ч-к поросли куст'); });
    var dash = s(function (r) { return r.diamText === '-'; });
    var hedgesAll = s(function (r) { return has(lc(r.species), 'рядная живая'); });
    return {
      trees: s(function () { return true; }) - dash - plotsD,
      shrubs: dash - plotsK - hedgesAll,
      treePlots: plotsD, shrubPlots: plotsK,
      hedge1: s(function (r) { return has(lc(r.species), 'днорядная живая'); }),
      hedge2: s(function (r) { return has(lc(r.species), 'вухрядная живая'); })
    };
  }

  // ---------------------------------------------------------------- balance (БАЛАНСЫ!A2:H8)
  function balance(rows, ex, rmT, trT, S) {
    var kept = rows.filter(function (r) { return r.action === ''; });
    var keptOf = function (ty) {          // СУЩд!P26 / S26
      return sum(kept.filter(function (r) { return r.N === ty && r.qty > 0.5 && r.diamText !== '-'; }).map(function (r) { return r.qty; }));
    };
    var C = rmT.count;
    var total = { trees: ex.trees, treePlots: ex.treePlots, shrubs: ex.shrubs, shrubPlots: ex.shrubPlots, hedge1: ex.hedge1, hedge2: ex.hedge2 };
    var removed = { trees: C.treesC2035, treePlots: C.plotsD, shrubs: C.shrubsC2035, shrubPlots: C.plotsK, hedge1: C.h1, hedge2: C.h2,
      conif: C['хвой.'], fruit: C['плод.'] };
    removed.leafy = C.trees - removed.fruit - removed.conif;
    var trans = { trees: trT.trees, treePlots: 0, shrubs: trT.shrubs, shrubPlots: 0, hedge1: trT.hedge1, hedge2: trT.hedge2,
      conif: trT.byType['хвой.'], fruit: trT.byType['плод.'] };
    trans.leafy = trT.trees - trans.fruit - trans.conif;
    var keptB = {};
    ['trees', 'treePlots', 'shrubs', 'shrubPlots', 'hedge1', 'hedge2'].forEach(function (k) { keptB[k] = total[k] - trans[k] - removed[k]; });
    keptB.conif = keptOf('хвой.'); keptB.fruit = keptOf('плод.');
    keptB.leafy = keptB.trees - keptB.conif - keptB.fruit;
    total.conif = keptB.conif + trans.conif + removed.conif;
    total.fruit = keptB.fruit + trans.fruit + removed.fruit;
    total.leafy = keptB.leafy + trans.leafy + removed.leafy;
    return { kept: keptB, transplanted: trans, removed: removed, total: total, showPlots: S.showPlots, hedgePieces: S.hedgePieces };
  }

  // ---------------------------------------------------------------- flowers, lawns, grass (ЦвГаТр)
  function qualityK2(c) { return eqi(c, 'хор.') ? 1 : eqi(c, 'уд.') ? 0.75 : eqi(c, 'плох.') ? 0.5 : 0; }
  function areaSection(list, tariffs, projectedArea, projectedValue, S, base, leftoverStart) {
    var rows = [], Tprev = null, P15 = 0, Q15 = 0;
    (list || []).forEach(function (src, i) {
      var kind = src.kind;
      if (isBlank(kind) && isBlank(src.area) && isBlank(src.cond)) return;
      var r = { idx: i, src: src, kind: kind, errors: [] };
      r.area = inNum(src.area) || 0;
      r.cond = src.cond || '';
      r.transplant = !!src.transplant;
      r.reason = src.reason || '';
      r.group = workGroup(S, r.reason);
      var tariff = tariffs[kind];
      if (tariff === undefined) r.errors.push('вид не найден в справочнике');
      // T/U — зачёт проектируемой площади («метрами»)
      var T;
      if (leftoverStart !== undefined) {           // травяной покров: ЦвГаТр!T73
        T = Tprev === null ? r.area - leftoverStart : (Tprev > 0 ? r.area : r.area + Tprev);
      } else if (Tprev === null) T = (r.transplant ? 0 : r.area) - projectedArea;
      else T = Tprev > 0 ? r.area : (r.transplant ? Tprev : r.area + Tprev);
      Tprev = T;
      r.T = T;
      r.U = T < 0 ? r.area : (T === r.area ? 0 : r.area - T);
      r.F = r.transplant ? 'Пересадить (' + r.reason + ')' : '(' + r.reason + ') ' + (src.note || '');
      r.coef = isBlank(r.reason) ? null : coefficients(S, r.reason, src.kRepl);
      if (!r.coef && !r.transplant) r.errors.push('не указана причина');
      r.q = qualityK2(r.cond);
      if (r.transplant) { r.P = null; r.Q = null; r.E = '-'; }
      else if (!r.coef || tariff === undefined) { r.P = null; r.Q = null; r.E = 'ОШИБКА'; }
      else {
        var paid = S.flowerMode === 'стоимостью' ? r.area : Math.max(T, 0);
        var k = r.coef.k;
        r.P = paid * tariff * r.q * k[0] * k[1] * k[2] * k[3];
        r.Q = r.P * base;
        var O = unitText(r.P, r.Q, S);
        r.E = S.view === 'итог' ? O : O + ' = ' + xnum(r.area) + (S.flowerMode === 'стоимостью' ? '' : (r.U > 0 ? '-' + xnum(r.U) : '')) +
          'м² x' + xnum(tariff) + 'б.в. x' + xnum(r.q) + 'x' + xnum(k[0]) + 'х' + xnum(k[1]) + 'х' + xnum(k[2]) + 'х' + xnum(k[3]);
        P15 += r.P; Q15 += r.Q;
      }
      rows.push(r);
    });
    var R18 = S.flowerMode === 'стоимостью' ? projectedValue : 0;
    var R19 = P15 - R18, R20 = R19 > 0 ? R19 : 0;
    var allTransplanted = rows.length > 0 && rows.every(function (r) { return r.transplant; });
    var groups = [];
    (S.reasons || []).forEach(function (rs) {
      if (!rs.group || groups.some(function (g) { return g.group === rs.group; })) return;
      var lst = rows.filter(function (r) { return r.group === rs.group; });
      if (!lst.length) return;
      var share = P15 === 0 ? 0 : sum(lst.map(function (r) { return r.P || 0; })) * ((P15 - R18) / P15);
      groups.push({ group: rs.group, area: sum(lst.map(function (r) { return r.area; })), value: share });
    });
    return { rows: rows, area: sum(rows.map(function (r) { return r.area; })), P: P15, Q: Q15, projectedValue: R18,
      rest: R19, result: R20, allTransplanted: allTransplanted, groups: groups, lastT: Tprev };
  }
  function unitText(bv, rub, S) {          // ЦвГаТр!O2
    if (S.unit === 'баз.в.') return xfixed(bv, 0) + ' баз.в.';
    if (S.unit === 'руб.') return xfixed(rub) + ' руб.';
    return xfixed(bv, 0) + ' баз.в.(' + xfixed(rub) + ' руб.)';
  }

  function computeAreas(project, ref, S, base) {
    var pr = project.projected || {};
    var fl = ref.flowers, lw = ref.lawns;
    var prPer = inNum(pr.perennial) || 0, prAnn = inNum(pr.annual) || 0;
    var flowers = areaSection(project.flowers, fl, prPer + prAnn,
      prPer * fl['Композиция из многолетников'] + prAnn * fl['Композиция из 1-2-летников'], S, base);
    var lawnKinds = ['Спортивный', 'Партерный', 'Обыкновенный', 'Луговой', 'Мавританский', 'Специального назначения'];
    var prL = pr.lawns || {}, prLawnArea = 0, prLawnValue = 0;
    lawnKinds.forEach(function (k) { var a = inNum(prL[k]) || 0; prLawnArea += a; prLawnValue += a * lw[k]; });
    var lawns = areaSection(project.lawns, lw, prLawnArea, prLawnValue, S, base);
    // остаток проектируемого газона для травяного покрова (ЦвГаТр!T73, R91)
    var lawnTs = lawns.rows.map(function (r) { return r.T; });
    var nEmpty = Math.max(0, 17 - lawns.rows.length);   // пустые строки таблицы газонов несут последний T
    var lastT = lawns.rows.length ? lawns.lastT : -prLawnArea;
    for (var i = 0; i < nEmpty; i++) lawnTs.push(lastT);
    var anyPos = lawnTs.some(function (t) { return t > 0; });
    var leftover = anyPos ? 0 : -Math.max.apply(null, lawnTs.concat([lawns.rows.length ? -Infinity : -prLawnArea]));
    var grassList = (project.grass || []).map(function (g) {   // ЦвГаТр!B73: вид появляется, если задано состояние или площадь
      var o = {}; for (var k in g) o[k] = g[k];
      o.kind = (isBlank(g.area) && isBlank(g.cond)) ? '' : 'Иной травяной покров'; return o;
    });
    var grass = areaSection(grassList, { 'Иной травяной покров': ref.grass['Иной травяной покров'] }, 0, 0, S, base,
      S.flowerMode === 'стоимостью' ? 0 : leftover);
    var R61 = lawns.P - lawns.projectedValue;
    var R91 = -R61 < 0 ? 0 : -R61;
    if (S.flowerMode === 'стоимостью') {
      grass.projectedValue = R91; grass.rest = grass.P - R91; grass.result = grass.rest > 0 ? grass.rest : 0;
    }
    grass.lawnLeftoverArea = leftover;
    return { flowers: flowers, lawns: lawns, grass: grass };
  }

  // ---------------------------------------------------------------- public compute
  /** Справочник с добавленными пользователем породами (project.customSpecies). */
  var mergeCache = { key: null, ref: null };
  function mergedRef(project, ref) {
    var extra = project.customSpecies || [];
    if (!extra.length) return prepareRef(ref);
    var key = JSON.stringify(extra);
    if (mergeCache.key === key && mergeCache.base === ref) return mergeCache.ref;
    var r2 = {}, k;
    for (k in ref) if (k.charAt(0) !== '_') r2[k] = ref[k];
    var names = {};
    ref.species.forEach(function (s) { names[lc(s.name)] = true; });
    r2.species = ref.species.concat(extra.filter(function (s) { return s && s.name && !names[lc(s.name)]; }));
    mergeCache = { key: key, base: ref, ref: prepareRef(r2) };
    return mergeCache.ref;
  }

  function compute(project, ref) {
    prepareRef(ref);
    ref = mergedRef(project, ref);
    var S = {}, d = defaultSettings(ref), k;
    for (k in d) S[k] = d[k];
    var ps = project.settings || {};
    for (k in ps) if (ps[k] !== undefined) S[k] = ps[k];
    S.compMap = {}; for (k in d.compMap) S.compMap[k] = d.compMap[k];
    if (ps.compMap) for (k in ps.compMap) S.compMap[k] = ps.compMap[k];
    var bvRow = baseValueFor(S.date, project.baseValues || ref.baseValues);
    var base = !isBlank(S.baseValue) ? Number(S.baseValue) : (bvRow ? bvRow.rub : 0);

    var rows = computeTrees(project, ref, S);
    rows.forEach(function (r) { if (r.removed) r.rm = removalRow(r, ref, S); });
    var rmT = removalTotals(rows, S, ref, base);
    var vol = volumeTotals(rows, ref);
    var trT = transplantTotals(rows, ref, S);
    var ex = existingTotals(rows);
    var bal = balance(rows, ex, rmT, trT, S);
    var areas = computeAreas(project, ref, S, base);

    var errors = [];
    rows.forEach(function (r) { r.errors.forEach(function (e) { errors.push({ section: 'trees', row: r.idx, no: r.no, msg: e }); }); });
    ['flowers', 'lawns', 'grass'].forEach(function (sec) {
      areas[sec].rows.forEach(function (r) { r.errors.forEach(function (e) { errors.push({ section: sec, row: r.idx, msg: e }); }); });
    });
    var res = {
      settings: S, baseValue: base, baseValueRow: bvRow,
      rows: rows, existing: ex, removal: rmT, volume: vol, transplant: trT, balance: bal,
      flowers: areas.flowers, lawns: areas.lawns, grass: areas.grass, errors: errors,
      moneyText: function (v) { return moneyText(v, S, base); }
    };
    res.removalReport = removalReport(res);
    res.balanceReport = balanceReport(res);
    return res;
  }

  return {
    compute: compute, baseValueFor: baseValueFor, defaultSettings: defaultSettings, prepareRef: prepareRef,
    speciesInfo: function (ref, n) { return speciesInfo(prepareRef(ref), n); },
    parseStems: parseStems, coefficients: coefficients, xnum: xnum, xround: xround, xfixed: xfixed, ruValue: ruValue,
    moneyText: moneyText, unitText: unitText,
    TREE_TYPES: TREE_TYPES, SHRUB_TYPES: SHRUB_TYPES, SPECIAL: SP, NO_COMP: NO_COMP, UNITS: UNITS
  };
});
