#!/usr/bin/env python3
"""Rotated and perspective variants of an evaluation set, with transformed boxes.

    python tests/eval/make_angles.py <set dir>... --out <new set dir>

Variants: rot90, rot180, rot270 (exact), tilt12 (12° counter-clockwise) and
persp (camera-like perspective). A transformed box is the bounding box of its
four transformed corners, so tilted boxes are slightly larger than the object.
"""
import argparse, json, math
from pathlib import Path
import numpy as np
from PIL import Image


def transform(img, matrix, size):
    """Projective warp with white fill; matrix maps source → destination."""
    inv = np.linalg.inv(matrix); coeffs = (inv / inv[2, 2]).flatten()[:8]
    return img.transform(size, Image.PERSPECTIVE, tuple(coeffs), Image.BICUBIC, fillcolor=(255, 255, 255))


def box_after(matrix, o):
    pts = np.array([[o['x'], o['y'], 1], [o['x'] + o['w'], o['y'], 1], [o['x'] + o['w'], o['y'] + o['h'], 1], [o['x'], o['y'] + o['h'], 1]], float).T
    q = matrix @ pts; q = q[:2] / q[2]
    x0, y0 = q.min(1); x1, y1 = q.max(1)
    return {**o, 'x': int(round(x0)), 'y': int(round(y0)), 'w': int(round(x1 - x0)), 'h': int(round(y1 - y0))}


def variants(w, h):
    def rot(deg):
        a = math.radians(deg); c, s = math.cos(a), math.sin(a)
        corners = np.array([[0, 0], [w, 0], [w, h], [0, h]], float)
        r = np.array([[c, s], [-s, c]]); q = corners @ r.T
        mn = q.min(0); W, H = np.ceil(q.max(0) - mn).astype(int)
        m = np.array([[c, s, -mn[0]], [-s, c, -mn[1]], [0, 0, 1]], float)
        return m, (int(W), int(H))
    out = {name: rot(deg) for name, deg in [('rot90', 90), ('rot180', 180), ('rot270', 270), ('tilt12', 12)]}
    src = np.float32([[0, 0], [w, 0], [w, h], [0, h]])
    dst = np.float32([[w * .08, h * .05], [w * .95, 0], [w, h], [0, h * .93]])
    a = []; b = []
    for (x, y), (u, v) in zip(src, dst):
        a += [[x, y, 1, 0, 0, 0, -u * x, -u * y], [0, 0, 0, x, y, 1, -v * x, -v * y]]; b += [u, v]
    p = np.linalg.solve(np.array(a), np.array(b)); out['persp'] = (np.append(p, 1).reshape(3, 3), (w, h))
    return out


def main():
    ap = argparse.ArgumentParser(); ap.add_argument('sets', nargs='+'); ap.add_argument('--out', required=True)
    args = ap.parse_args(); out = Path(args.out); out.mkdir(parents=True, exist_ok=True); items = []
    for set_dir in map(Path, args.sets):
        for item in json.loads((set_dir / 'gt.json').read_text()):
            img = Image.open(set_dir / item['file']).convert('RGB')
            for name, (m, size) in variants(*img.size).items():
                fname = f'{set_dir.name}_{Path(item["file"]).stem}_{name}.png'
                transform(img, m, size).save(out / fname)
                items.append({'file': fname, 'width': size[0], 'height': size[1], 'variant': name,
                              'objects': [box_after(m, o) for o in item['objects']], 'ignore': [box_after(m, o) for o in item.get('ignore', [])]})
    (out / 'gt.json').write_text(json.dumps(items, indent=1))
    print(len(items), 'images')


if __name__ == '__main__':
    main()
