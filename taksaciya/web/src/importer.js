/*
 * Импорт проекта из файла оригинальной программы «ТАКСАЦИЯ-АВТО» (.xlsm/.xlsx).
 * Читаются только исходные данные: ведомость СУЩд, настройки К1–К4, причины, цветники/газоны,
 * сведения об объекте, календарь базовых величин и собственные породы библиотеки.
 */
(function (root) {
  'use strict';
  function unxml(s) {
    return s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'")
      .replace(/&#(\d+);/g, function (m, d) { return String.fromCharCode(+d); })
      .replace(/&#x([0-9a-f]+);/gi, function (m, h) { return String.fromCharCode(parseInt(h, 16)); })
      .replace(/&amp;/g, '&');
  }
  function sharedStrings(xml) {
    var out = [];
    if (!xml) return out;
    var re = /<si>([\s\S]*?)<\/si>/g, m;
    while ((m = re.exec(xml))) {
      var t = '', r2 = /<t[^>]*>([\s\S]*?)<\/t>/g, mm;
      // фонетические подсказки <rPh> не нужны
      var body = m[1].replace(/<rPh[\s\S]*?<\/rPh>/g, '');
      while ((mm = r2.exec(body))) t += unxml(mm[1]);
      out.push(t);
    }
    return out;
  }
  /** Ячейки листа: {ref: {v, f: есть формула}}; maxRow ограничивает чтение. */
  function sheetCells(xml, ss, maxRow, cols) {
    var cells = {};
    if (!xml) return cells;
    var rowRe = /<row r="(\d+)"[^>]*?(?:\/>|>([\s\S]*?)<\/row>)/g, m;
    while ((m = rowRe.exec(xml))) {
      var r = +m[1];
      if (r > maxRow) break;
      if (!m[2]) continue;
      var cRe = /<c r="([A-Z]+)(\d+)"([^>]*?)(?:\/>|>([\s\S]*?)<\/c>)/g, c;
      while ((c = cRe.exec(m[2]))) {
        if (cols && cols.indexOf(c[1]) < 0) continue;
        var attrs = c[3], inner = c[4] || '', t = (attrs.match(/ t="([^"]+)"/) || [])[1];
        var vm = inner.match(/<v>([\s\S]*?)<\/v>/), v = null;
        if (t === 's' && vm) v = ss[+vm[1]];
        else if (t === 'inlineStr') { var im = inner.match(/<t[^>]*>([\s\S]*?)<\/t>/); v = im ? unxml(im[1]) : ''; }
        else if (t === 'str' || t === 'e') v = vm ? unxml(vm[1]) : '';
        else if (t === 'b') v = vm ? vm[1] === '1' : null;
        else if (vm) v = Number(vm[1]);
        cells[c[1] + c[2]] = { v: v, f: inner.indexOf('<f') >= 0 };
      }
    }
    return cells;
  }
  function sheetMap(files) {
    var wb = files['xl/workbook.xml'] || '', rels = files['xl/_rels/workbook.xml.rels'] || '';
    var relMap = {}, m, re = /<Relationship [^>]*?Id="([^"]+)"[^>]*?Target="([^"]+)"/g;
    while ((m = re.exec(rels))) relMap[m[1]] = m[2];
    re = /<Relationship [^>]*?Target="([^"]+)"[^>]*?Id="([^"]+)"/g;
    while ((m = re.exec(rels))) if (!relMap[m[2]]) relMap[m[2]] = m[1];
    var out = {}, sre = /<sheet [^>]*?name="([^"]+)"[^>]*?r:id="([^"]+)"/g;
    while ((m = sre.exec(wb))) {
      var tgt = relMap[m[2]] || '';
      tgt = tgt.replace(/^\/?xl\//, '').replace(/^\//, '');
      out[unxml(m[1])] = 'xl/' + tgt;
    }
    return out;
  }
  function excelDate(serial) {
    var d = new Date(Date.UTC(1899, 11, 30) + Math.round(serial) * 86400000);
    return d.toISOString().slice(0, 10);
  }
  var str = function (c) { return c && c.v !== null && c.v !== undefined ? c.v : ''; };
  var yes = function (c) { return String(str(c)).trim().toLowerCase() === 'да'; };

  /** Шаг 1: какие файлы нужны (по workbook.xml). */
  function neededFiles(files) {
    var map = sheetMap(files), need = ['xl/sharedStrings.xml'];
    ['СУЩд', 'имяПРОЕКТА', 'ЦвГаТр', 'базовая', 'БАЛАНСЫ', 'УДд1', 'Библ.ОРМ'].forEach(function (n) { if (map[n]) need.push(map[n]); });
    return { map: map, need: need };
  }

  /** Шаг 2: разбор. files — {путь: текст}. */
  function parse(files, ref) {
    var map = sheetMap(files);
    if (!map['СУЩд']) throw new Error('В книге нет листа «СУЩд» — это не файл программы ТАКСАЦИЯ-АВТО.');
    var ss = sharedStrings(files['xl/sharedStrings.xml']);
    var S = sheetCells(files[map['СУЩд']], ss, 2010, ['A', 'C', 'E', 'F', 'G', 'I', 'J', 'K', 'L', 'M', 'N', 'P', 'Q', 'S', 'U']);
    var warnings = [];
    var settings = {
      k1: !(String(str(S.N1)).trim().toLowerCase() === 'нет'), k2: yes(S.P1), k3: yes(S.N2), k4: yes(S.P2),
      defaultCond: str(S.J1) || 'уд.', unit: str(S.L1) || 'баз.в. (руб.)', view: str(S.M1) || 'формула + итог'
    };
    if (typeof str(S.I1) === 'number') settings.date = excelDate(str(S.I1));
    var reasons = [];
    for (var r = 4; r <= 19; r++) {
      var nm = str(S['P' + r]);
      if (nm !== '') reasons.push({ name: String(nm), group: String(str(S['Q' + r]) || ''), replace: String(str(S['S' + r]) || '') });
    }
    settings.reasons = reasons;
    var types = ['хвой.', 'медл.', 'быст.', 'плод.', 'малоц.', null, 'лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..', 'ж.и.1', 'ж.и.2'];
    var comp = {};
    types.forEach(function (t, i) { if (t) comp[t] = String(str(S['U' + (3 + i)]) || ''); });
    comp.plotTree = String(str(S.U19) || ''); comp.plotShrub = String(str(S.U20) || '');
    settings.compMap = comp;
    var trees = [];
    for (r = 3; r <= 2002; r++) {
      if (str(S['C' + r]) === 'Итого:') break;
      var sp = str(S['J' + r]);
      if (sp === '' || S['J' + r].f) break;
      var a = str(S['A' + r]), row = { species: String(sp).trim() };
      if (a !== '') {
        var t = String(a), p = t.indexOf('№');
        if (p >= 0) { row.count = t.slice(0, p); row.start = t.slice(p + 1); }
        else row.count = a;
      }
      row.height = str(S['E' + r]);
      row.diam = str(S['F' + r]);
      if (S['G' + r] && !S['G' + r].f && str(S['G' + r]) !== '') row.cond = str(S['G' + r]);
      row.action = String(str(S['I' + r]) || '').toUpperCase();
      row.note = String(str(S['K' + r]) || '');
      row.reason = String(str(S['L' + r]) || '');
      row.kRepl = str(S['M' + r]) === '' ? '' : String(str(S['M' + r]));
      trees.push(row);
    }
    var project = { settings: settings, trees: trees, flowers: [], lawns: [], grass: [], projected: { lawns: {} }, meta: {} };
    // сведения об объекте
    if (map['имяПРОЕКТА']) {
      var I = sheetCells(files[map['имяПРОЕКТА']], ss, 30);
      project.meta = { org: str(I.B2), objectNo: str(I.B5), objectName: str(I.B8), stage: str(I.D11),
        author: [str(I.C14), str(I.C15), str(I.C16)].filter(function (x) { return x !== '' && x !== '…'; }).join(' ') };
    }
    if (map['УДд1']) { var U = sheetCells(files[map['УДд1']], ss, 1); project.settings.money = !(String(str(U.L1)).toLowerCase() === 'нет'); }
    if (map['БАЛАНСЫ']) {
      var B = sheetCells(files[map['БАЛАНСЫ']], ss, 13);
      if (typeof str(B.L12) === 'number') project.settings.shrubMassKg = str(B.L12);
      project.settings.showPlots = !(String(str(B.F12)).toLowerCase() === 'нет');
      project.settings.hedgePieces = !(String(str(B.H12)).toLowerCase() === 'нет');
    }
    if (map['ЦвГаТр']) {
      var C = sheetCells(files[map['ЦвГаТр']], ss, 90);
      project.settings.flowerMode = str(C.T1) || 'метрами';
      var area = function (r0, cnt, withKind) {
        var out = [];
        for (var i = 0; i < cnt; i++) {
          var rr = r0 + i, kind = withKind ? str(C['B' + rr]) : 'Иной травяной покров';
          var areaV = str(C['D' + rr]), cond = str(C['C' + rr]);
          if ((withKind && kind === '') || (!withKind && areaV === '' && cond === '')) continue;
          out.push({ kind: kind, cond: cond, area: areaV, transplant: String(str(C['G' + rr])).toLowerCase() === 'п',
            reason: String(str(C['H' + rr]) || ''), note: String(str(C['I' + rr]) || ''), kRepl: str(C['S' + rr]) === '' ? '' : String(str(C['S' + rr])) });
        }
        return out;
      };
      project.flowers = area(2, 13, true);
      project.lawns = area(36, 17, true);
      project.grass = area(73, 17, false).map(function (g) { delete g.kind; return g; });
      project.projected = { perennial: str(C.G17), annual: str(C.G19), lawns: {
        'Спортивный': str(C.G55), 'Партерный': str(C.G57), 'Обыкновенный': str(C.G59), 'Луговой': str(C.G61),
        'Мавританский': str(C.G63), 'Специального назначения': str(C.G65) } };
    }
    if (map['базовая']) {
      var Z = sheetCells(files[map['базовая']], ss, 400, ['A', 'B', 'C']), bv = [];
      for (r = 2; r <= 400; r++) {
        if (!Z['A' + r] || typeof str(Z['A' + r]) !== 'number' || str(Z['B' + r]) === '') continue;
        bv.push({ from: excelDate(str(Z['A' + r])), rub: Number(str(Z['B' + r])), act: String(str(Z['C' + r]) || '') });
      }
      if (bv.length) project.baseValues = bv;
    }
    // собственные породы пользователя (нет в справочнике Таксации Лайт)
    if (map['Библ.ОРМ'] && ref) {
      var L = sheetCells(files[map['Библ.ОРМ']], ss, 1200, ['B', 'C', 'D', 'E', 'F', 'G', 'H', 'I']), custom = [];
      var known = {};
      ref.species.forEach(function (s) { known[s.name.toLowerCase()] = true; });
      (ref.special || []).forEach(function (s) { known[s.name.toLowerCase()] = true; });
      for (r = 2; r <= 1200; r++) {
        var n2 = str(L['B' + r]);
        if (n2 === '' || known[String(n2).toLowerCase()]) continue;
        custom.push({ name: String(n2), alt: String(str(L['C' + r]) || ''), shrub: Number(str(L['D' + r])) === 1, type: String(str(L['E' + r]) || ''),
          ballD: str(L['F' + r]) === '' ? null : Number(str(L['F' + r])), ballH: str(L['G' + r]) === '' ? null : Number(str(L['G' + r])),
          invasive: !!str(L['H' + r]) && str(L['H' + r]) !== 0, hard: String(str(L['I' + r]) || ''), inList: false, own: null });
      }
      if (custom.length) { project.customSpecies = custom; warnings.push('Добавлено собственных пород из файла: ' + custom.length + '.'); }
    }
    return { project: project, warnings: warnings, count: trees.length };
  }

  root.TaksImport = { neededFiles: neededFiles, parse: parse, sheetCells: sheetCells, sharedStrings: sharedStrings, sheetMap: sheetMap };
})(typeof self !== 'undefined' ? self : this);
