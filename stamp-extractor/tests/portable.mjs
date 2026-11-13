#!/usr/bin/env node
// Runs the built package with the real Neutralino runtime (Linux binary of the
// same release, browser mode) and drives it in Chromium. Saving goes through the
// Neutralino filesystem API; only the folder dialog is automated.
// Requires `npm run build` first.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { runUi } from './ui.mjs';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');
const linux = path.join(root, 'build', 'linux');
const port = await new Promise(resolve => { const s = net.createServer().listen(0, () => { const p = s.address().port; s.close(() => resolve(p)); }); });
const dist = path.join(root, 'dist', 'StampExtractor-Portable-x64', 'resources.neu');
if (fs.readFileSync(dist).compare(fs.readFileSync(path.join(linux, 'resources.neu'))) !== 0) throw new Error('resources.neu differs from the Windows package');
const child = spawn(path.join(linux, 'StampExtractor'), ['--mode=browser', `--port=${port}`], { cwd: linux, stdio: 'ignore' });
const saveDir = fs.mkdtempSync(path.join(os.tmpdir(), 'stamp-portable-'));
try {
  for (let i = 0; i < 50; i++) { try { if ((await fetch(`http://127.0.0.1:${port}/www/`)).ok) break; } catch {} await new Promise(r => setTimeout(r, 200)); }
  const headers = (await fetch(`http://127.0.0.1:${port}/vendor/ort/ort-wasm-simd-threaded.wasm`)).headers;
  if (headers.get('content-type') !== 'application/wasm') throw new Error('wrong WASM MIME type: ' + headers.get('content-type'));
  await runUi(`http://127.0.0.1:${port}/www/`, { native: true, saveDir });
  console.log('Portable package test passed with Neutralino', JSON.parse(fs.readFileSync(path.join(root, 'neutralino.config.json'), 'utf8')).cli.binaryVersion);
} finally {
  child.kill(); fs.rmSync(saveDir, { recursive: true, force: true });
}
