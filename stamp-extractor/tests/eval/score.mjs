#!/usr/bin/env node
// Object-level precision/recall of stamps and signatures against gt.json.
// A prediction matches a ground-truth box of the same kind when IoU ≥ 0.3
// (one-to-one, greedy by IoU). Predictions on partially visible ("ignore")
// objects are not counted as false positives.
import fs from 'node:fs';
import path from 'node:path';

export function iou(a, b) {
  const ix = Math.max(0, Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x)), iy = Math.max(0, Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y));
  const inter = ix * iy; return inter / (a.w * a.h + b.w * b.h - inter || 1);
}
export function score(gtItems, results, kinds = ['stamp', 'signature'], threshold = 0.3) {
  const byFile = new Map(results.map(r => [r.file, r])), stats = Object.fromEntries(kinds.map(k => [k, { tp: 0, fp: 0, fn: 0 }]));
  let images = 0;
  for (const item of gtItems) {
    const result = byFile.get(item.file); if (!result) continue; images++;
    for (const kind of kinds) {
      const truth = item.objects.filter(o => o.kind === kind), predicted = result.regions.filter(r => r.kind === kind);
      const pairs = [];
      truth.forEach((t, i) => predicted.forEach((p, j) => { const v = iou(t, p); if (v >= threshold) pairs.push([v, i, j]); }));
      pairs.sort((a, b) => b[0] - a[0]);
      const usedT = new Set(), usedP = new Set();
      for (const [, i, j] of pairs) if (!usedT.has(i) && !usedP.has(j)) { usedT.add(i); usedP.add(j); }
      const ignored = predicted.filter((p, j) => !usedP.has(j) && (item.ignore || []).some(g => g.kind === kind && iou(g, p) >= threshold)).length;
      stats[kind].tp += usedT.size; stats[kind].fn += truth.length - usedT.size; stats[kind].fp += predicted.length - usedP.size - ignored;
    }
  }
  for (const s of Object.values(stats)) {
    s.precision = s.tp + s.fp ? s.tp / (s.tp + s.fp) : null; s.recall = s.tp + s.fn ? s.tp / (s.tp + s.fn) : null;
    s.f1 = s.precision && s.recall ? 2 * s.precision * s.recall / (s.precision + s.recall) : 0;
  }
  return { images, stats };
}
if (process.argv[1] && import.meta.url.endsWith(path.basename(process.argv[1]))) {
  const [setDir, resultFile] = process.argv.slice(2);
  const gt = JSON.parse(fs.readFileSync(path.join(setDir, 'gt.json'), 'utf8'));
  const res = JSON.parse(fs.readFileSync(resultFile, 'utf8'));
  const { images, stats } = score(gt, res);
  const pct = v => v == null ? '—' : (v * 100).toFixed(1) + '%';
  console.log(`${path.basename(setDir)} · ${path.basename(resultFile)} · изображений: ${images}`);
  for (const [kind, s] of Object.entries(stats)) console.log(`  ${kind.padEnd(9)} TP ${s.tp} FP ${s.fp} FN ${s.fn} · точность ${pct(s.precision)} · полнота ${pct(s.recall)} · F1 ${pct(s.f1)}`);
}
