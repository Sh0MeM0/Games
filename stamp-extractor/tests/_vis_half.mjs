import fs from 'node:fs'; import path from 'node:path'; import { PNG } from 'pngjs';
import * as ort from '../vendor/ort/ort.wasm.min.mjs';
import { segmentImage, resizeImage, setFlatten } from '../lib/segmentation.js';
if (process.env.FLATTEN) setFlatten(true);
import { inkGrid } from '../lib/objects.js'; import { workingImage } from '../lib/pipeline.js';
ort.env.wasm.wasmPaths = path.resolve('vendor/ort') + '/'; ort.env.wasm.numThreads = 1; ort.env.logLevel = 'error';
const session = await ort.InferenceSession.create(fs.readFileSync('models/ink-seg.onnx'));
const files = process.argv.slice(3), f = Number(process.argv[2]); const tiles = [];
for (const file of files) {
  const png = PNG.sync.read(fs.readFileSync(file));
  let { work } = workingImage({ width: png.width, height: png.height, data: new Uint8ClampedArray(png.data) });
  work = resizeImage(work, Math.round(work.width * f), Math.round(work.height * f));
  const maps = await segmentImage(session, ort, work), ink = inkGrid(work, {}), colors = [[0,0,0],[150,150,150],[0,160,0],[235,120,0],[30,70,230]];
  const t = new PNG({ width: maps.width * 2, height: maps.height });
  for (let y = 0; y < maps.height; y++) for (let x = 0; x < maps.width; x++) { const i = y * maps.width + x; let best = -1, v = 127;
    for (let c = 0; c < 5; c++) if (maps.planes[c][i] > v) { v = maps.planes[c][i]; best = c; }
    const wp = ((y * 2) * work.width + x * 2) * 4;
    t.data.set([work.data[wp], work.data[wp+1], work.data[wp+2], 255], (y * t.width + x) * 4);
    t.data.set(ink.grid[i] && best >= 0 ? [...colors[best], 255] : ink.grid[i] ? [255, 0, 255, 255] : [255, 255, 255, 255], (y * t.width + x + maps.width) * 4); }
  tiles.push(t);
}
const out = new PNG({ width: tiles[0].width, height: tiles.reduce((n, t) => n + t.height, 0) }); let y0 = 0;
for (const t of tiles) { t.data.copy(out.data, y0 * out.width * 4); y0 += t.height; }
fs.writeFileSync('/tmp/claude-0/-home-user-Games/f6f498fe-6ace-560d-a25b-9aa117e5bc91/scratchpad/vis_half.png', PNG.sync.write(out));
