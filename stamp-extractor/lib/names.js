/** Windows-safe output file names. */
export function safeName(value) {
  let name = String(value || 'объект').normalize('NFC').replace(/[<>:"/\\|?*\x00-\x1f\x7f]/g, '_')
    .replace(/^[. ]+|[. ]+$/g, '').slice(0, 100).replace(/[. ]+$/g, '');
  if (!name) name = 'объект';
  if (/^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)/i.test(name)) name = '_' + name;
  return name.replace(/\.png$/i, '') || 'объект';
}

export function isPng(bytes) {
  const b = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  const signature = [137, 80, 78, 71, 13, 10, 26, 10];
  return b.length >= 33 && signature.every((v, i) => b[i] === v) && String.fromCharCode(b[12], b[13], b[14], b[15]) === 'IHDR';
}

/** Join a directory returned by the native dialog with a file name. */
export function joinPath(directory, filename) {
  const separator = /\\/.test(directory) && !/\//.test(directory) ? '\\' : '/';
  return directory.replace(/[\\/]+$/, '') + separator + filename;
}

/** First free name: stem.png, stem_2.png, … using an async existence check. */
export async function uniqueFilename(directory, stem, exists) {
  const base = safeName(stem);
  for (let i = 0; i < 10_000; i++) {
    const filename = base + (i ? `_${i + 1}` : '') + '.png';
    if (!(await exists(joinPath(directory, filename)))) return filename;
  }
  throw new Error('Слишком много файлов с одинаковым именем. Выберите другую папку.');
}
