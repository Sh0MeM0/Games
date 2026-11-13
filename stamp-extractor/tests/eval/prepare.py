#!/usr/bin/env python3
"""Builds evaluation sets as PNG + gt.json (boxes of stamps and signatures).

  fixtures   — the authored v3 examples with their recorded boxes (repository);
  signatures — real photographs from the Ultralytics "signature" dataset
               (AGPL-3.0, downloaded separately, never committed here);
  synthetic  — new pages from model/synth.py with seeds not used for training.
"""
import json, sys
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
OUT = Path(sys.argv[1] if len(sys.argv) > 1 else ROOT / 'build' / 'eval')


def save(set_name, name, image, objects):
    d = OUT / set_name; d.mkdir(parents=True, exist_ok=True)
    image.convert('RGB').save(d / (name + '.png'))
    return {'file': name + '.png', 'width': image.width, 'height': image.height, 'objects': objects}


def fixtures():
    items = []
    box = lambda o: {'kind': o['kind'], 'x': o['bbox']['x'], 'y': o['bbox']['y'], 'w': o['bbox']['width'], 'h': o['bbox']['height']}
    doc = json.load(open(ROOT / 'tests/fixtures/document.json'))
    for name, gt in [('document.png', 'document'), ('document-gray.png', 'document'), ('document-photo.jpg', 'document'),
                     ('document-overlap.png', 'document-overlap'), ('document-rotated.png', 'document-rotated')]:
        meta = json.load(open(ROOT / f'tests/fixtures/{gt}.json'))
        items.append(save('fixtures', Path(name).stem, Image.open(ROOT / 'examples' / name), [box(o) for o in meta['objects']]))
    for name in ['form-signature', 'rectangular', 'rectangular-clean', 'text-only', 'text-only-blue']:
        meta = json.load(open(ROOT / f'tests/fixtures-v3/failures-{name}.json'))
        items.append(save('fixtures', 'failures-' + name, Image.open(ROOT / 'examples' / f'failures-{name}.png'),
                          [box(o) for o in meta['objects'] if o['kind'] in ('stamp', 'signature')]))
    (OUT / 'fixtures' / 'gt.json').write_text(json.dumps(items, ensure_ascii=False, indent=1))


def signatures(source):
    items = []
    for image_path in sorted(Path(source).glob('images/*/*.jpg')):
        label = image_path.parent.parent.parent / 'labels' / image_path.parent.name / (image_path.stem + '.txt')
        im = Image.open(image_path); W, H = im.size; objects = []
        for line in label.read_text().split('\n') if label.exists() else []:
            if not line.strip(): continue
            c, cx, cy, w, h = map(float, line.split())
            objects.append({'kind': 'signature', 'x': round((cx - w / 2) * W), 'y': round((cy - h / 2) * H), 'w': round(w * W), 'h': round(h * H)})
        items.append(save('signatures', image_path.parent.name + '_' + image_path.stem, im, objects))
    (OUT / 'signatures' / 'gt.json').write_text(json.dumps(items, indent=1))


def synthetic(count=120, seed=900000):
    sys.path.insert(0, str(ROOT / 'model'))
    import synth
    items = []
    for i in range(count):
        objects = []
        img, _ = synth.document(seed + i, (1600, 1200), objects)
        keep = [{'kind': k, 'x': round(x0), 'y': round(y0), 'w': round(x1 - x0), 'h': round(y1 - y0)} for k, x0, y0, x1, y1, v in objects if v >= .6]
        ignore = [{'kind': k, 'x': round(x0), 'y': round(y0), 'w': round(x1 - x0), 'h': round(y1 - y0)} for k, x0, y0, x1, y1, v in objects if v < .6]
        item = save('synthetic', f'syn_{i:03d}', Image.fromarray(img), keep); item['ignore'] = ignore; items.append(item)
    (OUT / 'synthetic' / 'gt.json').write_text(json.dumps(items, indent=1))


if __name__ == '__main__':
    fixtures()
    if len(sys.argv) > 2: signatures(sys.argv[2])
    synthetic()
    print('done', OUT)
