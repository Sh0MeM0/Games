const path = require('node:path');
const fs = require('node:fs/promises');
const PNG_SIGNATURE = Buffer.from([137,80,78,71,13,10,26,10]);

function safeName(value) {
  let name = String(value || 'объект').normalize('NFC').replace(/[<>:"/\\|?*\x00-\x1f\x7f]/g, '_')
    .replace(/^[. ]+|[. ]+$/g, '').slice(0, 100).replace(/[. ]+$/g, '');
  if (!name) name = 'объект';
  if (/^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)/i.test(name)) name = '_' + name;
  return name.replace(/\.png$/i, '') || 'объект';
}

async function writeUniquePng(directory, name, bytes) {
  const buffer = Buffer.from(bytes);
  if (buffer.length < 33 || buffer.length > 100 * 1024 * 1024 || !buffer.subarray(0, 8).equals(PNG_SIGNATURE) ||
      buffer.toString('ascii', 12, 16) !== 'IHDR') throw new Error('Некорректный PNG или размер превышает 100 МБ.');
  const stem = safeName(name);
  for (let i = 0; i < 10_000; i++) {
    const filename = stem + (i ? `_${i + 1}` : '') + '.png';
    const destination = path.join(directory, filename);
    let handle;
    try {
      handle = await fs.open(destination, 'wx');
      await handle.writeFile(buffer); await handle.close();
      return filename;
    } catch (error) {
      if (handle) { await handle.close().catch(() => {}); await fs.unlink(destination).catch(() => {}); }
      if (error.code === 'EEXIST') continue;
      throw error;
    }
  }
  throw new Error('Слишком много файлов с одинаковым именем. Выберите другую папку.');
}

module.exports = { safeName, writeUniquePng };
