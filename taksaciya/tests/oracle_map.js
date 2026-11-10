/*
 * Сопоставление результатов ядра с ячейками оригинального файла «ТАКСАЦИЯ-АВТО 4.2».
 * Используется тестами: tests/vectors/*.json содержит входные данные и значения ячеек,
 * пересчитанные в оригинале (LibreOffice, локаль ru-RU).
 */
'use strict';

function cellsFromResult(res, project) {
  const E = require('../core/engine.js');
  const x = E.xnum;
  const c = {};
  const set = (k, v) => { c[k] = v; };
  const n = res.rows.length;
  res.rows.forEach((r, i) => {
    const row = 3 + i;
    set(`СУЩд!B${row}`, r.no);
    set(`СУЩд!C${row}`, r.title);
    set(`СУЩд!D${row}`, r.qty);
    set(`СУЩд!H${row}`, r.H);
    set(`СУЩд!N${row}`, r.N || '');
    if (r.coef) set(`СУЩд!O${row}`, r.O);
    if (r.removed && r.rm) {
      const m = r.rm;
      set(`УДд!A${row}`, r.no);
      set(`УДд!B${row}`, r.title);
      set(`УДд!J${row}`, m.J);
      set(`УДд!N${row}`, m.N === null ? undefined : m.N);
      set(`УДд!AR${row}`, m.AR === null ? undefined : m.AR);
      if (typeof m.Y === 'number' || m.Y === '-') { set(`УДд!Y${row}`, m.Y); set(`УДд!G${row}`, m.G); set(`УДд!Z${row}`, m.Z); }
      set(`УДд!AQ${row}`, m.AQ === '' ? '' : m.AQ);
      set(`УДд!M${row}`, m.M);
      const w = m.wood;
      set(`УДд!CE${row}`, w.ce || 0);
      ['BV', 'BW', 'BX', 'BY', 'BZ', 'CB', 'CC', 'CD'].forEach(k => { set(`УДд!${k}${row}`, w[k] === null ? '' : w[k]); });
    } else {
      set(`УДд!A${row}`, '');
    }
    if (r.transplanted) set(`ПЕРЕд!G${row}`, r.ball);
  });
  const it = 3 + n, ex = res.existing;
  set(`СУЩд!C${it}`, 'Итого:');
  set(`СУЩд!D${it}`, 'Дер. ' + x(ex.trees));
  set(`СУЩд!E${it}`, 'Кус. ' + x(ex.shrubs));
  set(`СУЩд!F${it}`, 'Дер.поросль ' + x(ex.treePlots) + '_м²');
  set(`СУЩд!G${it}`, 'Кус.поросль ' + x(ex.shrubPlots) + '_м²');
  set(`СУЩд!H${it}`, 'ж.изг.1ряд_' + x(ex.hedge1) + '_п.м. ж.изг.2ряд_' + x(ex.hedge2) + '_п.м.');
  // totals
  const T = res.removal, AP = T.money, AN = T.plant, C = T.count;
  [['2003', C['хвой.']], ['2004', C['медл.']], ['2005', C['быст.']], ['2006', C['плод.']], ['2007', C['малоц.']],
   ['2008', C.treePlots], ['2009', C.trees],
   ['2011', AP['хвой.']], ['2012', AP['медл.']], ['2013', AP['быст.']], ['2014', AP['плод.']], ['2015', AP['малоц.']],
   ['2016', AP.treePlots], ['2017', AP.trees],
   ['2020', C.shrubTypes['лиан.']], ['2021', C.shrubTypes['хвой..']], ['2022', C.shrubTypes['вечз.']], ['2023', C.shrubTypes['крас.']],
   ['2024', C.shrubTypes['медл..']], ['2025', C.shrubTypes['быст..']], ['2026', C.shrubTypes['плод..']],
   ['2027', C.hedge1], ['2028', C.hedge2], ['2030', C.shrubPlots],
   ['2033', AP['лиан.']], ['2034', AP['хвой..']], ['2035', AP['вечз.']], ['2036', AP['крас.']], ['2037', AP['медл..']],
   ['2038', AP['быст..']], ['2039', AP['плод..']], ['2040', AP['ж.и.1']], ['2041', AP['ж.и.2']], ['2042', AP.shrubs], ['2043', AP.shrubPlots]
  ].forEach(([r, v]) => set(`УДд!AP${r}`, v));
  [['2011', AN['хвой.']], ['2012', AN['медл.']], ['2013', AN['быст.']], ['2017', AN.trees], ['2033', AN['лиан.']],
   ['2034', AN['хвой/вечз']], ['2035', AN['крас.']], ['2036', AN['медл..']], ['2037', AN['быст/плод']],
   ['2040', AN.hedge1], ['2041', AN.hedge2], ['2042', AN.shrubs]].forEach(([r, v]) => set(`УДд!AN${r}`, v));
  set('УДд!AF2028', T.treePlots.total); set('УДд!AF2048', T.shrubPlots.total);
  set('УДд!C2035', x(C.treesC2035) + '+ ' + x(C.shrubsC2035));
  set('УДд!D2035', x(C.plotsD) + '+ ' + x(C.plotsK));
  set('УДд!E2035', x(C.h1) + '+ ' + x(C.h2));
  res.removalReport.forEach((rowv, i) => {
    'ABCDEFGH'.split('').forEach((col, j) => set(`УДд!${col}${2037 + i}`, rowv[j]));
  });
  // volumes
  const V = res.volume;
  ['AF', 'AG', 'AH'].forEach((col, j) => {
    const ce = j + 1;
    set(`УДд!${col}74`, V.stemsM3[ce]); set(`УДд!${col}75`, V.stemsT[ce]);
    set(`УДд!${col}78`, V.stumpsM3[ce]); set(`УДд!${col}79`, V.stumpsT[ce]);
    set(`УДд!${col}82`, V.branchesM3[ce]); set(`УДд!${col}83`, V.branchesT[ce]);
    set(`УДд!${col}73`, V.gt50[ce]); set(`УДд!${col}72`, V.le50[ce]);
  });
  V.stemsByDiameter.forEach((b, i) => { ['AK', 'AL', 'AM'].forEach((col, j) => set(`УДд!${col}${74 + i}`, b[j + 1])); });
  set('УДд!BV1', V.shrubT.all); set('УДд!BW1', V.branchesT.all); set('УДд!BX1', V.stemsT.all);
  set('УДд!BY1', V.branchesM3.all); set('УДд!BZ1', V.stemsM3.all);
  ['K', 'L', 'M', 'N'].forEach((col, j) => { set(`БАЛАНСЫ!${col}9`, V.table.m3[j]); set(`БАЛАНСЫ!${col}10`, V.table.t[j]); });
  // balance
  res.balanceReport.forEach((rowv, i) => {
    ['B', 'C', 'D', 'E', 'F', 'G', 'H'].forEach((col, j) => set(`БАЛАНСЫ!${col}${5 + i}`, rowv[j + 1]));
  });
  // transplant totals
  const tr = res.transplant;
  set('ПЕРЕд!K2003', tr.byType['хвой.']); set('ПЕРЕд!K2006', tr.byType['плод.']);
  if (tr.rows.length) {
    set('ПЕРЕд!C2004', tr.trees); set('ПЕРЕд!D2004', tr.shrubs); set('ПЕРЕд!E2004', tr.hedge1); set('ПЕРЕд!F2004', tr.hedge2);
  }
  // flowers / lawns / grass
  const sec = [['flowers', res.flowers, 2, 15], ['lawns', res.lawns, 36, 53], ['grass', res.grass, 73, 90]];
  sec.forEach(([name, s, r0, tot]) => {
    s.rows.forEach((r, i) => {
      const row = r0 + i;
      set(`ЦвГаТр!T${row}`, r.T); set(`ЦвГаТр!U${row}`, r.U);
      if (r.coef) set(`ЦвГаТр!R${row}`, r.coef.str);
      if (r.P !== null) { set(`ЦвГаТр!P${row}`, r.P); set(`ЦвГаТр!Q${row}`, r.Q); set(`ЦвГаТр!E${row}`, r.E); }
      else if (r.transplant) set(`ЦвГаТр!E${row}`, '-');
    });
    set(`ЦвГаТр!P${tot}`, s.P); set(`ЦвГаТр!Q${tot}`, s.Q);
  });
  set('ЦвГаТр!R18', res.flowers.projectedValue); set('ЦвГаТр!R19', res.flowers.rest); set('ЦвГаТр!R20', res.flowers.result);
  set('ЦвГаТр!R60', res.lawns.projectedValue); set('ЦвГаТр!R61', res.lawns.rest); set('ЦвГаТр!R62', res.lawns.result);
  set('ЦвГаТр!R91', res.grass.projectedValue); set('ЦвГаТр!R92', res.grass.rest); set('ЦвГаТр!R93', res.grass.result);
  return c;
}

function same(a, b) {
  const blank = v => v === undefined || v === null || v === '';
  if (blank(a) && blank(b)) return true;
  if (blank(a) || blank(b)) {
    const o = blank(a) ? b : a;
    return o === 0 || o === '0';
  }
  // число или текст с десятичной запятой («17,5» в склейке Excel) — сравниваем как числа
  const toNum = v => typeof v === 'number' ? v
    : /^[-+]?\d*\.?\d+(e[-+]?\d+)?$/i.test(String(v)) ? Number(v)
    : /^[-+]?\d+,\d+$/.test(String(v)) ? Number(String(v).replace(',', '.')) : NaN;
  const na = toNum(a), nb = toNum(b);
  if (!isNaN(na) && !isNaN(nb)) return Math.abs(na - nb) <= 1e-9 * Math.max(1, Math.abs(na));
  return String(a) === String(b);
}

module.exports = { cellsFromResult, same };
