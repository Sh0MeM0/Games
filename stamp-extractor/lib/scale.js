/** Stroke-width estimate used to bring a page to the scale the model was trained on. */
import { prepareInk } from './processing.js';
import { components } from './objects.js';

/** Median over connected ink components of each component's median
 * min(horizontal run, vertical run). Large blobs (a pen, a shadow) count once. */
export function strokeWidth(image) {
  const { width, height } = image;
  const { mask } = prepareInk(image, { threshold: 18, speckle: 2, mode: 'all', contrast: 1 });
  const hrun = new Uint16Array(mask.length), vrun = new Uint16Array(mask.length);
  for (let y = 0; y < height; y++) {
    let x = 0;
    while (x < width) {
      if (!mask[y * width + x]) { x++; continue; }
      let e = x; while (e < width && mask[y * width + e]) e++;
      for (let k = x; k < e; k++) hrun[y * width + k] = Math.min(65535, e - x);
      x = e;
    }
  }
  for (let x = 0; x < width; x++) {
    let y = 0;
    while (y < height) {
      if (!mask[y * width + x]) { y++; continue; }
      let e = y; while (e < height && mask[e * width + x]) e++;
      for (let k = y; k < e; k++) vrun[k * width + x] = Math.min(65535, e - y);
      y = e;
    }
  }
  const { labels, boxes } = components(mask, width, height);
  const perComponent = boxes.map(() => []);
  for (let i = 0; i < mask.length; i++) if (labels[i]) {
    const list = perComponent[labels[i] - 1]; if (list.length < 400) list.push(Math.min(hrun[i], vrun[i]));
  }
  const medians = [];
  boxes.forEach((box, k) => {
    if (box.area < 6) return;
    const v = perComponent[k].sort((a, b) => a - b); medians.push(v[v.length >> 1]);
  });
  if (medians.length < 5) return null;
  medians.sort((a, b) => a - b);
  return { width: medians[medians.length >> 1], components: medians.length };
}
