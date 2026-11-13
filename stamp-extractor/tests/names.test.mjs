import test from 'node:test';
import assert from 'node:assert/strict';
import { safeName, isPng, joinPath, uniqueFilename } from '../lib/names.js';

const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVQIHWP4//8/AAX+Av7czFnnAAAAAElFTkSuQmCC', 'base64');

test('names cannot traverse directories or use Windows device names', () => {
  for (const name of ['../../secret', 'C:\\file', 'a/b', 'a\u0000b', 'CON', 'PRN.txt', 'NUL.png', 'LPT1']) {
    const sanitized = safeName(name);
    assert.ok(!/[\\/\x00]/.test(sanitized)); assert.ok(!/^(CON|PRN|AUX|NUL|LPT1)(\.|$)/i.test(sanitized));
  }
  assert.equal(safeName('Печать 1.png'), 'Печать 1');
  assert.equal(safeName('   '), 'объект');
});

test('PNG signature check rejects other data', () => {
  assert.ok(isPng(png)); assert.ok(!isPng(Buffer.from('not a png at all, definitely not')));
});

test('paths use the separator of the chosen folder', () => {
  assert.equal(joinPath('C:\\Users\\me\\Out\\', 'a.png'), 'C:\\Users\\me\\Out\\a.png');
  assert.equal(joinPath('C:/Users/me/Out', 'a.png'), 'C:/Users/me/Out/a.png');
  assert.equal(joinPath('/tmp/out/', 'a.png'), '/tmp/out/a.png');
});

test('an existing file is never overwritten: the next free suffix is used', async () => {
  const existing = new Set(['/out/Печать.png', '/out/Печать_2.png']);
  assert.equal(await uniqueFilename('/out', 'Печать', async p => existing.has(p)), 'Печать_3.png');
  assert.equal(await uniqueFilename('/out', 'Подпись', async () => false), 'Подпись.png');
});
