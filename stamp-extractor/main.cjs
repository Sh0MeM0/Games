const { app, BrowserWindow, ipcMain, dialog, Menu, shell } = require('electron');
const path = require('node:path');
const os = require('node:os');
const fs = require('node:fs');
const { randomUUID } = require('node:crypto');
const { pathToFileURL } = require('node:url');
const { writeUniquePng } = require('./lib/export.cjs');
const { OfflineOcr } = require('./lib/ocr.cjs');

// Portable: Chromium profile and cache live in TEMP, not beside the original images.
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'StampExtractor-'));
for (const key of ['userData', 'sessionData', 'logs']) {
  const directory = path.join(temporary, key); fs.mkdirSync(directory); app.setPath(key, directory);
}
app.setAppUserModelId('org.stampextractor.desktop');
let window, lastDirectory;
const sessions = new Map();
const indexUrl = pathToFileURL(path.join(__dirname, 'renderer/index.html')).href;
const ocr = new OfflineOcr({ modelDirectory: app.isPackaged
  ? path.join(process.resourcesPath, 'ocr') : path.join(__dirname, 'resources', 'ocr') });

function message(error) {
  if (error.code === 'ENOSPC') return 'На диске недостаточно места. Выберите другой диск.';
  if (['EACCES', 'EPERM', 'EROFS'].includes(error.code)) return 'Нет прав на запись. Выберите другую папку.';
  if (['ENOENT', 'ENOTDIR'].includes(error.code)) return 'Папка больше недоступна. Выберите её заново.';
  return error.message || 'Не удалось сохранить изображение.';
}
function handle(channel, action) {
  ipcMain.handle(channel, async (event, payload) => {
    try {
      if (!window || event.senderFrame !== window.webContents.mainFrame || event.senderFrame.url !== indexUrl) {
        throw new Error('Недопустимый источник запроса.');
      }
      return { ok: true, ...(await action(payload)) };
    } catch (error) { return { ok: false, error: message(error) }; }
  });
}
handle('export:begin', async () => {
  const chosen = await dialog.showOpenDialog(window, { title: 'Папка для отдельных PNG', properties: ['openDirectory', 'createDirectory'] });
  if (chosen.canceled) return { canceled: true };
  const token = randomUUID();
  for (const [key, value] of sessions) if (Date.now() - value.created > 30 * 60_000) sessions.delete(key);
  sessions.set(token, { directory: chosen.filePaths[0], created: Date.now(), count: 0 });
  return { token };
});
handle('export:write', async payload => {
  const session = sessions.get(payload?.token);
  if (!session || session.count >= 80 || Date.now() - session.created > 30 * 60_000) throw new Error('Выберите папку сохранения заново.');
  if (!(payload.bytes instanceof Uint8Array) && !Buffer.isBuffer(payload.bytes)) throw new Error('Некорректные данные изображения.');
  const filename = await writeUniquePng(session.directory, payload.name, payload.bytes);
  session.count++; lastDirectory = session.directory;
  return { filename, count: session.count };
});
handle('export:end', async payload => {
  const session = sessions.get(payload?.token); sessions.delete(payload?.token);
  return { count: session?.count || 0 };
});
handle('export:open', async () => {
  if (!lastDirectory) throw new Error('Сначала сохраните изображения.');
  const error = await shell.openPath(lastDirectory);
  if (error) throw new Error('Не удалось открыть папку результатов.');
  return {};
});
handle('ocr:recognize', async payload => ocr.recognize(payload?.buffer));

app.whenReady().then(() => {
  Menu.setApplicationMenu(null);
  window = new BrowserWindow({ width: 1440, height: 940, minWidth: 1080, minHeight: 720,
    title: 'Печати и подписи', backgroundColor: '#f3f5f8', show: false,
    webPreferences: { preload: path.join(__dirname, 'preload.cjs'), contextIsolation: true,
      nodeIntegration: false, sandbox: true, webSecurity: true, spellcheck: false }
  });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', event => event.preventDefault());
  window.webContents.session.setPermissionRequestHandler((_webContents, _permission, callback) => callback(false));
  window.webContents.session.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*', 'ws://*/*', 'wss://*/*'] }, (_details, callback) => callback({ cancel: true }));
  window.once('ready-to-show', () => window.show());
  window.loadFile(path.join(__dirname, 'renderer/index.html'));
  window.on('closed', () => { window = undefined; sessions.clear(); });
});
app.on('window-all-closed', () => app.quit());
app.on('will-quit', () => { ocr.close(); try { fs.rmSync(temporary, { recursive: true, force: true }); } catch {} });
