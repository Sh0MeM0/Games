#!/usr/bin/env python3
"""Train the bundled ink segmentation network on synthetic documents.

    python model/train.py generate --count 3000 --out build/dataset
    python model/train.py train --data build/dataset --steps 6000
    python model/train.py export --checkpoint build/ink.pt --out resources/model/ink-seg.onnx

The network predicts five independent (multi-label) channels at half resolution:
printed text, printed form line, handwritten text, signature and stamp.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import time
from multiprocessing import Pool
from pathlib import Path

import numpy as np

CLASSES = ['print', 'line', 'hand', 'signature', 'stamp']
SIZE = 640


def _generate(args):
    index, seed = args
    import synth
    img, lab = synth.document(seed, (SIZE, SIZE))
    return index, img, lab


def generate(out: Path, count: int, seed: int, workers: int):
    out.mkdir(parents=True, exist_ok=True)
    images = np.lib.format.open_memmap(out / 'images.npy', 'w+', np.uint8, (count, SIZE, SIZE, 3))
    labels = np.lib.format.open_memmap(out / 'labels.npy', 'w+', np.uint8, (count, SIZE, SIZE))
    start = time.time()
    with Pool(workers) as pool:
        for n, (i, img, lab) in enumerate(pool.imap_unordered(_generate, [(i, seed + i) for i in range(count)], chunksize=8)):
            images[i] = img; labels[i] = lab
            if n % 250 == 0:
                print(f'{n}/{count} {time.time() - start:.0f}s', flush=True)
    images.flush(); labels.flush()
    (out / 'meta.json').write_text(json.dumps({'count': count, 'seed': seed, 'size': SIZE}))


def build_model():
    import torch
    from torch import nn
    import torch.nn.functional as F

    class Block(nn.Module):
        def __init__(self, cin, cout, stride=1, dilation=1):
            super().__init__()
            self.dw = nn.Conv2d(cin, cin, 3, stride, padding=dilation, dilation=dilation, groups=cin, bias=False)
            self.bn1 = nn.BatchNorm2d(cin)
            self.pw = nn.Conv2d(cin, cout, 1, bias=False)
            self.bn2 = nn.BatchNorm2d(cout)
            self.residual = stride == 1 and cin == cout

        def forward(self, x):
            y = F.relu(self.bn1(self.dw(x)))
            y = self.bn2(self.pw(y))
            return F.relu(y + x if self.residual else y)

    def fuse(cin, cout):
        return nn.Sequential(nn.Conv2d(cin, cout, 1, bias=False), nn.BatchNorm2d(cout), nn.ReLU())

    class InkSeg(nn.Module):
        def __init__(self):
            super().__init__()
            c1, c2, c3, c4 = 24, 40, 72, 112
            self.stem = nn.Sequential(nn.Conv2d(3, c1, 3, 2, 1, bias=False), nn.BatchNorm2d(c1), nn.ReLU(), Block(c1, c1))
            self.e2 = nn.Sequential(Block(c1, c2, 2), Block(c2, c2))
            self.e3 = nn.Sequential(Block(c2, c3, 2), Block(c3, c3), Block(c3, c3, 1, 2))
            self.e4 = nn.Sequential(Block(c3, c4, 2), Block(c4, c4, 1, 2), Block(c4, c4, 1, 4), Block(c4, c4, 1, 8), Block(c4, c4))
            self.d3 = nn.Sequential(fuse(c4 + c3, c3), Block(c3, c3))
            self.d2 = nn.Sequential(fuse(c3 + c2, c2), Block(c2, c2))
            self.d1 = nn.Sequential(fuse(c2 + c1, c1), Block(c1, c1))
            self.head = nn.Conv2d(c1, len(CLASSES), 1)

        def forward(self, x):
            s1 = self.stem(x)
            s2 = self.e2(s1)
            s3 = self.e3(s2)
            s4 = self.e4(s3)
            up = lambda t: F.interpolate(t, scale_factor=2.0, mode='bilinear', align_corners=False)
            y = self.d3(torch.cat([up(s4), s3], 1))
            y = self.d2(torch.cat([up(y), s2], 1))
            y = self.d1(torch.cat([up(y), s1], 1))
            return self.head(y)

    return InkSeg()


def normalize(images):
    """Per-image paper white balance, identical to lib/segmentation.js."""
    x = images.astype(np.float32)
    flat = x.reshape(x.shape[0], -1, 3)
    paper = np.percentile(flat[:, ::7], 97, axis=1)[:, None, None, :]
    paper = np.maximum(paper, 40)
    return np.clip(x / paper, 0, 1.25) - .5


def label_planes(labels):
    bits = np.array([1, 2, 4, 8, 16], np.uint8)
    return ((labels[..., None] & bits) > 0).astype(np.float32)


def batches(images, labels, batch, crop, rng):
    n = images.shape[0]
    while True:
        idx = rng.integers(0, n, batch)
        xs, ys = [], []
        for i in idx:
            img = images[i]; lab = labels[i]
            # Bias crops towards objects so rare classes are seen often.
            for _ in range(4):
                y0 = rng.integers(0, SIZE - crop + 1); x0 = rng.integers(0, SIZE - crop + 1)
                window = lab[y0:y0 + crop, x0:x0 + crop]
                if (window & 28).any() or rng.random() < .3:
                    break
            xs.append(img[y0:y0 + crop, x0:x0 + crop]); ys.append(window)
        xs = np.stack(xs); ys = np.stack(ys)
        x = normalize(xs).transpose(0, 3, 1, 2)
        planes = label_planes(ys)
        # Target at half resolution: a class is present if any of the 2×2 pixels carries it.
        t = planes.reshape(batch, crop // 2, 2, crop // 2, 2, len(CLASSES)).max(axis=(2, 4)).transpose(0, 3, 1, 2)
        yield np.ascontiguousarray(x), np.ascontiguousarray(t)


def train(data: Path, steps: int, out: Path, batch: int, crop: int, lr: float, resume: Path | None):
    import torch
    torch.set_num_threads(os.cpu_count())
    images = np.load(data / 'images.npy', mmap_mode='r'); labels = np.load(data / 'labels.npy', mmap_mode='r')
    n = images.shape[0]; val = max(50, n // 20)
    model = build_model()
    if resume:
        model.load_state_dict(torch.load(resume))
    params = sum(p.numel() for p in model.parameters())
    print('parameters', params, flush=True)
    opt = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.OneCycleLR(opt, max_lr=lr, total_steps=steps, pct_start=.05)
    rng = np.random.default_rng(1)
    gen = batches(images[val:], labels[val:], batch, crop, rng)
    pos_weight = torch.tensor([2., 3., 4., 4., 3.]).view(1, -1, 1, 1)
    start = time.time()
    for step in range(1, steps + 1):
        x, t = next(gen)
        x = torch.from_numpy(x); t = torch.from_numpy(t)
        logits = model(x)
        bce = torch.nn.functional.binary_cross_entropy_with_logits(logits, t, pos_weight=pos_weight)
        p = torch.sigmoid(logits)
        inter = (p * t).sum((0, 2, 3)); dice = 1 - (2 * inter + 1) / (p.sum((0, 2, 3)) + t.sum((0, 2, 3)) + 1)
        loss = bce + .5 * dice.mean()
        opt.zero_grad(); loss.backward(); opt.step(); sched.step()
        if step % 50 == 0:
            print(f'step {step} loss {loss.item():.4f} bce {bce.item():.4f} dice {dice.detach().numpy().round(3)} '
                  f'{(time.time() - start) / step:.2f}s/step', flush=True)
        if step % 250 == 0 or step == steps:
            torch.save(model.state_dict(), out)
            (out.parent / (out.stem + '.step')).write_text(str(step))
        if step % 1000 == 0 or step == steps:
            evaluate(model, images[:val], labels[:val])
            model.train()


def evaluate(model, images, labels, crop=512):
    import torch
    model.eval()
    inter = np.zeros(len(CLASSES)); union = np.zeros(len(CLASSES))
    with torch.no_grad():
        for i in range(0, images.shape[0], 8):
            xs = np.asarray(images[i:i + 8, :crop, :crop]); ys = np.asarray(labels[i:i + 8, :crop, :crop])
            x = torch.from_numpy(normalize(xs).transpose(0, 3, 1, 2).copy())
            p = torch.sigmoid(model(x)).numpy()
            p = p.repeat(2, axis=2).repeat(2, axis=3) > .5
            t = label_planes(ys).transpose(0, 3, 1, 2) > .5
            ink = (ys > 0)[:, None]  # score on labelled ink pixels and paper alike
            inter += (p & t).sum((0, 2, 3)); union += (p | t).sum((0, 2, 3))
    iou = inter / np.maximum(union, 1)
    print('val IoU', dict(zip(CLASSES, iou.round(3))), flush=True)
    return iou


def export(checkpoint: Path, out: Path):
    import torch
    model = build_model(); model.load_state_dict(torch.load(checkpoint)); model.eval()
    x = torch.zeros(1, 3, 256, 256)
    out.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(model, x, str(out), input_names=['image'], output_names=['logits'], opset_version=17,
                      dynamic_axes={'image': {2: 'height', 3: 'width'}, 'logits': {2: 'h2', 3: 'w2'}}, dynamo=False)
    import onnx
    m = onnx.load(str(out)); onnx.checker.check_model(m)
    import onnxruntime as ort
    sess = ort.InferenceSession(str(out))
    test = np.random.default_rng(0).random((1, 3, 320, 448), dtype=np.float32) - .5
    ref = model(torch.from_numpy(test)).detach().numpy()
    got = sess.run(None, {'image': test})[0]
    print('export max abs diff', float(np.abs(ref - got).max()), 'bytes', out.stat().st_size)


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest='cmd', required=True)
    g = sub.add_parser('generate'); g.add_argument('--out', type=Path, required=True); g.add_argument('--count', type=int, default=3000)
    g.add_argument('--seed', type=int, default=1000); g.add_argument('--workers', type=int, default=os.cpu_count())
    t = sub.add_parser('train'); t.add_argument('--data', type=Path, required=True); t.add_argument('--steps', type=int, default=6000)
    t.add_argument('--out', type=Path, default=Path('build/ink.pt')); t.add_argument('--batch', type=int, default=12)
    t.add_argument('--crop', type=int, default=256); t.add_argument('--lr', type=float, default=3e-3); t.add_argument('--resume', type=Path)
    e = sub.add_parser('export'); e.add_argument('--checkpoint', type=Path, required=True); e.add_argument('--out', type=Path, required=True)
    a = ap.parse_args()
    if a.cmd == 'generate':
        generate(a.out, a.count, a.seed, a.workers)
    elif a.cmd == 'train':
        a.out.parent.mkdir(parents=True, exist_ok=True)
        train(a.data, a.steps, a.out, a.batch, a.crop, a.lr, a.resume)
    else:
        export(a.checkpoint, a.out)


if __name__ == '__main__':
    main()
