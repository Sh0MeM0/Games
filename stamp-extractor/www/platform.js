/* Native functions through Neutralino (folder dialog, file writes, opening the
 * result folder). Without Neutralino (development in a browser) results are
 * offered as ordinary downloads. */
import { isPng, joinPath, uniqueFilename } from '../lib/names.js';

const N = globalThis.Neutralino;
export const isNative = !!(N && globalThis.NL_PORT);

export async function initPlatform() {
  if (!isNative) return;
  N.init();
  N.events.on('windowClose', () => N.app.exit());
}

function nativeError(error) {
  const code = error?.code || '';
  if (code === 'NE_FS_FILWRER' || code === 'NE_FS_FILOPER') return 'Нет прав на запись или диск заполнен. Выберите другую папку.';
  if (code === 'NE_FS_NOPATHE') return 'Папка больше недоступна. Выберите её заново.';
  return error?.message || String(error || 'Не удалось сохранить изображение.');
}

let lastDirectory = null;

export async function chooseOutput() {
  if (!isNative) return { directory: null, browser: true };
  const directory = await N.os.showFolderDialog('Папка для отдельных PNG');
  return directory ? { directory } : { canceled: true };
}

async function exists(path) {
  try { await N.filesystem.getStats(path); return true; }
  catch (error) { if (error?.code === 'NE_FS_NOPATHE') return false; throw error; }
}

/** Writes a PNG without overwriting existing files; returns the file name. */
export async function writePng(target, stem, bytes) {
  if (!isPng(bytes) || bytes.byteLength > 100 * 1024 * 1024) throw new Error('Некорректный PNG или размер превышает 100 МБ.');
  if (target.browser) {
    const filename = stem.replace(/[<>:"/\\|?*\x00-\x1f]/g, '_') + '.png';
    const url = URL.createObjectURL(new Blob([bytes], { type: 'image/png' }));
    const link = Object.assign(document.createElement('a'), { href: url, download: filename });
    document.body.append(link); link.click(); link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 30_000);
    return filename;
  }
  try {
    const filename = await uniqueFilename(target.directory, stem, exists);
    await N.filesystem.writeBinaryFile(joinPath(target.directory, filename), bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength));
    lastDirectory = target.directory;
    return filename;
  } catch (error) { throw new Error(nativeError(error)); }
}

export function hasOutputFolder() { return isNative && !!lastDirectory; }

export async function openOutput() {
  if (!lastDirectory) throw new Error('Сначала сохраните изображения.');
  await N.os.open(globalThis.NL_OS === 'Windows' ? lastDirectory.replace(/\//g, '\\') : lastDirectory);
}
