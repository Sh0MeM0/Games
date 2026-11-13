#!/usr/bin/env node
// Development/test server: serves the application like the Neutralino resource
// server does (same paths and MIME types), without native functions.
import http from 'node:http';
import { createReadStream, statSync } from 'node:fs';
import { dirname, extname, join, normalize } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const types = { '.html': 'text/html', '.js': 'application/javascript', '.mjs': 'application/javascript', '.css': 'text/css',
  '.wasm': 'application/wasm', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml' };
const allowed = ['www/', 'lib/', 'vendor/', 'models/'];

export function startServer(port = 0) {
  const server = http.createServer((req, res) => {
    let path = decodeURIComponent(new URL(req.url, 'http://x').pathname).replace(/^\/+/, '');
    if (!path) { res.writeHead(302, { Location: '/www/' }); return res.end(); }
    if (path.endsWith('/')) path += 'index.html';
    const file = normalize(join(root, path));
    if (!file.startsWith(root) || !allowed.some(prefix => path.startsWith(prefix))) { res.writeHead(404); return res.end(); }
    try { if (!statSync(file).isFile()) throw new Error(); } catch { res.writeHead(404); return res.end(); }
    res.writeHead(200, { 'Content-Type': types[extname(file)] || 'application/octet-stream' });
    createReadStream(file).pipe(res);
  });
  return new Promise(resolve => server.listen(port, '127.0.0.1', () => resolve(server)));
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const server = await startServer(Number(process.argv[2]) || 8080);
  console.log(`http://127.0.0.1:${server.address().port}/www/`);
}
