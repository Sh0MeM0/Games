import fs from 'node:fs'; import { score } from './score.mjs';
const [gtFile, ...results] = process.argv.slice(2); const gt = JSON.parse(fs.readFileSync(gtFile, 'utf8'));
for (const r of results) { const res = JSON.parse(fs.readFileSync(r, 'utf8')); const line = [r.split('/').pop()];
  for (const v of ['rot90', 'rot180', 'rot270', 'tilt12', 'persp']) { const s = score(gt.filter(g => g.variant === v), res).stats;
    line.push(`${v}: st ${s.stamp.tp}/${s.stamp.fp}/${s.stamp.fn} sg ${s.signature.tp}/${s.signature.fp}/${s.signature.fn}`); }
  console.log(line.join(' | ')); }
