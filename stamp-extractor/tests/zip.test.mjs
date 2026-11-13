import test from 'node:test';
import assert from 'node:assert/strict';
import { readZip, writeZip } from '../scripts/zip.mjs';

test('ZIP writer round-trips binary and Cyrillic names', () => {
  const entries = [{ name: 'Папка/ПРОЧТИ.txt', data: Buffer.from('Печати и подписи\n'.repeat(50)) }, { name: 'bin/a.bin', data: Buffer.from([0, 1, 2, 255]) }];
  const files = readZip(writeZip(entries));
  assert.deepEqual(Object.keys(files).sort(), entries.map(e => e.name).sort());
  for (const e of entries) assert.deepEqual(files[e.name], e.data);
});
