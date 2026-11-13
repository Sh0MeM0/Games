#!/usr/bin/env node
// Copies the exact runtime files of pinned npm dependencies into vendor/.
import { cpSync, mkdirSync, rmSync, readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const modules = join(root, 'node_modules'), vendor = join(root, 'vendor');
const files = [
  ['onnxruntime-web/dist/ort.wasm.min.mjs', 'ort/ort.wasm.min.mjs'],
  ['onnxruntime-web/dist/ort-wasm-simd-threaded.mjs', 'ort/ort-wasm-simd-threaded.mjs'],
  ['onnxruntime-web/dist/ort-wasm-simd-threaded.wasm', 'ort/ort-wasm-simd-threaded.wasm'],
  ['../licenses/onnxruntime-LICENSE.txt', 'ort/LICENSE'],
  ['pdfjs-dist/legacy/build/pdf.min.mjs', 'pdfjs/build/pdf.min.mjs'],
  ['pdfjs-dist/legacy/build/pdf.worker.min.mjs', 'pdfjs/build/pdf.worker.min.mjs'],
  ['pdfjs-dist/cmaps', 'pdfjs/cmaps'],
  ['pdfjs-dist/standard_fonts', 'pdfjs/standard_fonts'],
  ['pdfjs-dist/iccs', 'pdfjs/iccs'],
  ['pdfjs-dist/LICENSE', 'pdfjs/LICENSE'],
  ['@neutralinojs/lib/dist/neutralino.js', 'neutralino/neutralino.js'],
  ['@neutralinojs/lib/LICENSE', 'neutralino/LICENSE']
];
for (const name of ['openjpeg.wasm', 'jbig2.wasm', 'qcms_bg.wasm', 'LICENSE_OPENJPEG', 'LICENSE_PDFJS_OPENJPEG', 'LICENSE_JBIG2',
  'LICENSE_PDFJS_JBIG2', 'LICENSE_QCMS', 'LICENSE_PDFJS_QCMS']) files.push([`pdfjs-dist/wasm/${name}`, `pdfjs/wasm/${name}`]);

rmSync(vendor, { recursive: true, force: true });
const versions = {};
for (const [from, to] of files) {
  const source = join(modules, from);
  if (!existsSync(source)) throw new Error(`Не найден ${from}. Выполните npm ci.`);
  mkdirSync(dirname(join(vendor, to)), { recursive: true });
  cpSync(source, join(vendor, to), { recursive: true });
  if (from.startsWith('../')) continue;
  const pkg = from.startsWith('@') ? from.split('/').slice(0, 2).join('/') : from.split('/')[0];
  versions[pkg] ||= JSON.parse(readFileSync(join(modules, pkg, 'package.json'), 'utf8')).version;
}
console.log('vendor:', Object.entries(versions).map(([k, v]) => `${k}@${v}`).join(', '));
