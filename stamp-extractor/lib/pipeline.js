/** Shared analysis steps used by the app worker and the evaluation scripts. */
import { workingScale, resizeImage } from './segmentation.js';

export function workingImage(source) {
  const scale = workingScale(source.width, source.height);
  const work = resizeImage(source, Math.max(1, Math.round(source.width * scale)), Math.max(1, Math.round(source.height * scale)));
  return { scale, work };
}

/** Converts regions from working-image pixels back to source pixels. */
export function regionsToSource(regions, scale, source) {
  return regions.map(r => {
    const x = Math.max(0, Math.floor(r.x / scale)), y = Math.max(0, Math.floor(r.y / scale));
    return { ...r, x, y, w: Math.min(source.width - x, Math.ceil(r.w / scale)), h: Math.min(source.height - y, Math.ceil(r.h / scale)) };
  });
}
