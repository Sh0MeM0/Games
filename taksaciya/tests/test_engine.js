#!/usr/bin/env node
/*
 * Проверка ядра по эталонным векторам (результаты оригинальной программы).
 * Запуск: node tests/test_engine.js [-v]
 */
'use strict';
const fs = require('fs');
const path = require('path');
const E = require('../core/engine.js');
const ref = require('../core/reference.json');
const { cellsFromResult, same } = require('./oracle_map.js');

const verbose = process.argv.includes('-v');
const dir = path.join(__dirname, 'vectors');
let fail = 0, checked = 0, origErr = 0;
for (const f of fs.readdirSync(dir).filter(f => f.endsWith('.json')).sort()) {
  const vec = JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8'));
  const res = E.compute(vec.project, ref);
  const exp = cellsFromResult(res, vec.project);
  const known = vec.knownDiffs || {};
  const diffs = [];
  for (const k of Object.keys(exp)) {
    if (exp[k] === undefined) continue;
    checked++;
    const o = vec.cells[k];
    // В оригинале ошибка одной строки (#ЗНАЧ!) обнуляет все итоги объекта; ядро помечает такую строку
    // ошибкой и считает итоги по остальным. Такие ячейки учитываются отдельно.
    if (typeof o === 'string' && o.startsWith('#ERR')) {
      if (res.errors.length) { origErr++; continue; }
      diffs.push([k, exp[k], o]); continue;
    }
    if (!same(exp[k], o) && !(k in known)) diffs.push([k, exp[k], o]);
  }
  console.log(`${f}: ${Object.keys(exp).length} ячеек, расхождений: ${diffs.length}`);
  diffs.slice(0, verbose ? 200 : 15).forEach(([k, a, b]) => console.log(`   ${k}  ядро=${JSON.stringify(a)}  оригинал=${JSON.stringify(b)}`));
  fail += diffs.length;
}
console.log(`Итого проверено ячеек: ${checked}, расхождений: ${fail}` + (origErr ? `; ячеек, где оригинал выдаёт #ЗНАЧ!/#Н/Д из-за ошибок ввода: ${origErr}` : ''));
process.exit(fail ? 1 : 0);
