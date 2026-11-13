#!/usr/bin/env node
/* Builds dist/StampExtractor-Portable-x64/ (unpacked portable folder) and its ZIP.
 * Uses the official Neutralino CLI (neu build) on a staging copy, so the Windows
 * executable gets its icon and version information the standard way. */
import { createHash } from 'node:crypto';
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync, readdirSync, statSync, copyFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readZip, writeZip } from './zip.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const config = JSON.parse(readFileSync(join(root, 'neutralino.config.json'), 'utf8'));
const version = config.version, neuVersion = config.cli.binaryVersion;
const NEU_ZIP = { url: `https://github.com/neutralinojs/neutralinojs/releases/download/v${neuVersion}/neutralinojs-v${neuVersion}.zip`,
  sha256: 'e633f480737f229f582d8ec6165a52d789340950aef062560702122389f08329' };
const sha256 = data => createHash('sha256').update(data).digest('hex');
const cache = join(root, 'build', 'cache'), stage = join(root, 'build', 'neu'), dist = join(root, 'dist');
const folderName = 'StampExtractor-Portable-x64', folder = join(dist, folderName);

async function neutralinoBinaries() {
  mkdirSync(cache, { recursive: true });
  const file = join(cache, `neutralinojs-v${neuVersion}.zip`);
  if (!existsSync(file)) {
    const response = await fetch(NEU_ZIP.url);
    if (!response.ok) throw new Error(`Не удалось скачать Neutralino: HTTP ${response.status}`);
    writeFileSync(file, Buffer.from(await response.arrayBuffer()));
  }
  const data = readFileSync(file), actual = sha256(data);
  if (actual !== NEU_ZIP.sha256) throw new Error(`SHA-256 архива Neutralino не совпадает: ${actual}`);
  return readZip(data);
}

function copyTree(from, to, filter = () => true) {
  for (const name of readdirSync(from)) {
    const source = join(from, name), target = join(to, name);
    if (!filter(source)) continue;
    if (statSync(source).isDirectory()) { mkdirSync(target, { recursive: true }); copyTree(source, target, filter); }
    else { mkdirSync(dirname(target), { recursive: true }); copyFileSync(source, target); }
  }
}

const binaries = await neutralinoBinaries();
execFileSync(process.execPath, [join(root, 'scripts', 'vendor.mjs')], { stdio: 'inherit' });
rmSync(join(root, 'build', 'neu'), { recursive: true, force: true });
mkdirSync(join(stage, 'bin'), { recursive: true });
for (const name of ['neutralino-win_x64.exe', 'neutralino-linux_x64']) {
  if (!binaries[name]) throw new Error(`В архиве Neutralino нет ${name}`);
  writeFileSync(join(stage, 'bin', name), binaries[name], { mode: 0o755 });
}
const skip = path => !/\.(map|d\.ts|d\.mts|md)$/.test(path) && !/[\\/]\.|LICENSE_PDFJS_QUICKJS|quickjs/.test(path);
for (const dir of ['www', 'lib', 'vendor', 'models']) copyTree(join(root, dir), join(stage, 'app', dir), skip);
writeFileSync(join(stage, 'neutralino.config.json'), JSON.stringify({ ...config, $schema: undefined }, null, 2));
execFileSync(process.execPath, [join(root, 'node_modules', '@neutralinojs', 'neu', 'bin', 'neu.js'), 'build'], { cwd: stage, stdio: 'inherit' });

const built = join(stage, 'dist', config.cli.binaryName);
rmSync(folder, { recursive: true, force: true }); mkdirSync(folder, { recursive: true });
copyFileSync(join(built, `${config.cli.binaryName}-win_x64.exe`), join(folder, 'StampExtractor.exe'));
copyFileSync(join(built, 'resources.neu'), join(folder, 'resources.neu'));
copyFileSync(join(root, 'LICENSE'), join(folder, 'LICENSE.txt'));
copyFileSync(join(root, 'packaging', 'README.txt'), join(folder, 'README.txt'));
copyFileSync(join(root, 'packaging', 'THIRD_PARTY_NOTICES.txt'), join(folder, 'THIRD_PARTY_NOTICES.txt'));
const licenses = join(folder, 'licenses'); mkdirSync(licenses);
copyFileSync(join(root, 'licenses', 'neutralinojs-LICENSE.txt'), join(licenses, 'Neutralinojs-MIT.txt'));
copyFileSync(join(root, 'licenses', 'onnxruntime-LICENSE.txt'), join(licenses, 'ONNX-Runtime-MIT.txt'));
copyFileSync(join(root, 'node_modules', 'pdfjs-dist', 'LICENSE'), join(licenses, 'pdf.js-Apache-2.0.txt'));
for (const name of readdirSync(join(root, 'vendor', 'pdfjs', 'wasm')).filter(n => n.startsWith('LICENSE')))
  copyFileSync(join(root, 'vendor', 'pdfjs', 'wasm', name), join(licenses, `pdf.js-wasm-${name.replace('LICENSE_', '')}.txt`));
for (const name of readdirSync(join(root, 'vendor', 'pdfjs', 'standard_fonts')).filter(n => n.startsWith('LICENSE')))
  copyFileSync(join(root, 'vendor', 'pdfjs', 'standard_fonts', name), join(licenses, `pdf.js-fonts-${name.replace('LICENSE_', '')}.txt`));

// Linux binary for automated tests only; it is not part of the Windows package.
mkdirSync(join(root, 'build', 'linux'), { recursive: true });
copyFileSync(join(built, `${config.cli.binaryName}-linux_x64`), join(root, 'build', 'linux', 'StampExtractor'));
copyFileSync(join(built, 'resources.neu'), join(root, 'build', 'linux', 'resources.neu'));

const entries = [];
const walk = dir => { for (const name of readdirSync(dir).sort()) { const p = join(dir, name);
  if (statSync(p).isDirectory()) walk(p); else entries.push({ name: `${folderName}/${relative(folder, p).split('\\').join('/')}`, data: readFileSync(p) }); } };
walk(folder);
const zipName = `StampExtractor-Portable-x64-${version}.zip`;
writeFileSync(join(dist, zipName), writeZip(entries));
const sums = [zipName, ...entries.map(e => e.name)].map(name => {
  const data = name === zipName ? readFileSync(join(dist, zipName)) : entries.find(e => e.name === name).data;
  return `${sha256(data)}  ${name}`;
});
writeFileSync(join(dist, 'SHA256SUMS.txt'), sums.join('\n') + '\n');
const size = n => (n / 1024 / 1024).toFixed(2) + ' МиБ';
console.log(`Готово: dist/${zipName} — ${size(statSync(join(dist, zipName)).size)}; папка: ${size(entries.reduce((n, e) => n + e.data.length, 0))}`);
