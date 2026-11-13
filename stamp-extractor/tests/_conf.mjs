import fs from 'node:fs'; import path from 'node:path'; import { PNG } from 'pngjs';
import * as ort from '../vendor/ort/ort.wasm.min.mjs';
import { segmentImage, resizeImage, setFlatten } from '../lib/segmentation.js';
if (process.env.FLATTEN) setFlatten(true);
import { detectObjects, inkGrid } from '../lib/objects.js';
import { workingImage, regionsToSource } from '../lib/pipeline.js';
import { score } from './eval/score.mjs';
ort.env.wasm.wasmPaths = path.resolve('vendor/ort') + '/'; ort.env.wasm.numThreads = 1; ort.env.logLevel = 'error';
const session = await ort.InferenceSession.create(fs.readFileSync('models/ink-seg.onnx'));
function stats(maps, ink) {
  let n = 0, conf = 0, print = 0, obj = 0;
  for (let i = 0; i < ink.grid.length; i++) if (ink.grid[i]) { n++; let m = 0, b = -1;
    for (let c = 0; c < 5; c++) if (maps.planes[c][i] > m) { m = maps.planes[c][i]; b = c; }
    if (m > 178) { conf++; if (b === 0) print++; else if (b >= 2) obj++; } }
  return { conf: conf / n, print: print / n, obj: obj / n };
}
const [set, step] = process.argv.slice(2);
const dir = '/home/user/data/eval/' + set, gt = JSON.parse(fs.readFileSync(dir + '/gt.json')).filter((_, i) => i % Number(step) === 0);
const res = { 1: [], 0.5: [] };
for (const item of gt) {
  const png = PNG.sync.read(fs.readFileSync(path.join(dir, item.file)));
  const source = { width: png.width, height: png.height, data: new Uint8ClampedArray(png.data) };
  const { work, scale } = workingImage(source); const line = [item.file];
  for (const f of [1, 0.5]) {
    const w = f === 1 ? work : resizeImage(work, Math.round(work.width * f), Math.round(work.height * f));
    const maps = await segmentImage(session, ort, w), ink = inkGrid(w, {}), s = stats(maps, ink);
    const found = detectObjects(w, maps, { ink, sensitivity: 0.4, tuning: { stampMinMean: 0.7 } });
    const regions = regionsToSource(found.regions, scale * f, source);
    res[f].push({ file: item.file, regions });
    const sc = score([item], [{ file: item.file, regions }]).stats;
    line.push(`s${f} conf ${s.conf.toFixed(2)} print ${s.print.toFixed(2)} obj ${s.obj.toFixed(2)} st ${sc.stamp.tp}/${sc.stamp.fp}/${sc.stamp.fn} sg ${sc.signature.tp}/${sc.signature.fp}/${sc.signature.fn}`);
  }
  console.log(line.join(' | '));
}
for (const f of [1, 0.5]) { const s = score(gt, res[f]).stats; console.log('scale', f, JSON.stringify(Object.fromEntries(Object.entries(s).map(([k, v]) => [k, [v.tp, v.fp, v.fn]])))); }
