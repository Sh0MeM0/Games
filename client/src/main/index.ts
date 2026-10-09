import { app, BrowserWindow, dialog, ipcMain, safeStorage, shell } from 'electron';
import fs from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import type { Action, ArchiveNode, EditStatus, Result, User } from '../shared/types';
import { Api, normalizeServer } from './api';
import { safeLocalName, sha256, SyncManager } from './sync';

let window: BrowserWindow;
let currentUser: User | null = null;
let api: Api;
let sync: SyncManager;
let quitting = false;

const uuid = (value: string) => {
  if (typeof value !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)) {
    throw new Error('Некорректный идентификатор.');
  }
  return value;
};
const positive = (value: number) => { if (!Number.isSafeInteger(value) || value < 1) throw new Error('Некорректный номер версии.'); return value; };
function text(value: string, max = 180) {
  if (typeof value !== 'string' || !value.trim() || value.length > max) throw new Error('Проверьте заполненные поля.');
  return value;
}

async function persistCredentials(credentials: unknown) {
  const filename = path.join(app.getPath('userData'), 'credentials.dat');
  if (!credentials) { await fs.rm(filename, { force: true }); return; }
  if (!safeStorage.isEncryptionAvailable()) throw new Error('Недоступно защищённое хранилище Windows. Вход отменён.');
  await fs.writeFile(filename + '.tmp', safeStorage.encryptString(JSON.stringify(credentials)), { mode: 0o600 });
  await fs.rename(filename + '.tmp', filename);
}

async function rememberServer(server: string) {
  await fs.writeFile(path.join(app.getPath('userData'), 'settings.json'), JSON.stringify({ server }), { mode: 0o600 });
}

async function protect<T>(callback: () => Promise<T>): Promise<Result<T>> {
  try { return { ok: true, value: await callback() }; }
  catch (error) {
    if (error instanceof Error && 'code' in error) {
      const code = String(error.code);
      return { ok: false, error: code === 'ENOSPC' ? 'Недостаточно места на диске. Освободите место и повторите.'
        : ['EACCES', 'EPERM', 'EBUSY'].includes(code) ? 'Файл занят или нет доступа. Закройте документ и проверьте права на папку.'
        : 'Локальный файл недоступен. Проверьте файл и рабочую папку.' };
    }
    return { ok: false, error: error instanceof Error ? error.message : 'Не удалось выполнить действие.' };
  }
}

async function selectedFile(extensions: string[]) {
  const selected = await dialog.showOpenDialog(window, { properties: ['openFile'], filters: [{ name: 'Документы', extensions }] });
  if (selected.canceled) return null;
  const filename = selected.filePaths[0];
  const stats = await fs.stat(filename);
  if (stats.size > 500 * 1024 * 1024) throw new Error('Файл превышает 500 МБ. Лимит сервера может быть меньше.');
  return { filename, content: await fs.readFile(filename) };
}

async function downloadFile(id: string, suggested: string, version?: number) {
  if (!safeLocalName(suggested)) throw new Error('Недопустимое имя файла.');
  const result = await dialog.showSaveDialog(window, { defaultPath: suggested });
  if (result.canceled || !result.filePath) return null;
  const response = await api.raw(`/api/files/${uuid(id)}/download` + (version ? `?version=${positive(version)}` : ''));
  const content = Buffer.from(await response.arrayBuffer());
  if (sha256(content) !== response.headers.get('X-File-SHA256')) throw new Error('Контрольная сумма файла не совпадает.');
  await fs.writeFile(result.filePath, content);
  return { saved: true };
}

async function action(data: Action): Promise<unknown> {
  if (!data || typeof data.type !== 'string') throw new Error('Некорректная команда.');
  if (!currentUser) throw new Error('Войдите в систему.');
  if ('id' in data) uuid(data.id);
  if ('parentId' in data && data.parentId) uuid(data.parentId);
  switch (data.type) {
    case 'list': {
      const query = new URLSearchParams();
      if (data.parentId) query.set('parent_id', data.parentId);
      if (data.search) query.set('search', text(data.search));
      return api.request('/api/nodes?' + query);
    }
    case 'details': return api.request(`/api/nodes/${data.id}`);
    case 'folder': return api.post('/api/folders', { name: text(data.name), parent_id: data.parentId || null });
    case 'rename': return api.request(`/api/nodes/${data.id}`, { method: 'PATCH', body: JSON.stringify({ name: text(data.name) }) });
    case 'delete': return api.request(`/api/nodes/${data.id}`, { method: 'DELETE' });
    case 'trash': return api.request('/api/trash');
    case 'restore': return api.post(`/api/nodes/${data.id}/restore`);
    case 'versions': return api.request(`/api/files/${data.id}/versions`);
    case 'force-unlock': return api.post(`/api/files/${data.id}/force-unlock`);
    case 'upload': {
      const selected = await selectedFile(['xlsx', 'xls', 'docx', 'doc']);
      if (!selected) return null;
      const form = new FormData();
      form.set('file', new Blob([new Uint8Array(selected.content)]), path.basename(selected.filename));
      if (data.parentId) form.set('parent_id', data.parentId);
      return api.request('/api/files', { method: 'POST', body: form });
    }
    case 'download': return downloadFile(data.id, text(data.name), data.version);
    case 'read': {
      const node = await api.request<ArchiveNode>(`/api/nodes/${data.id}`);
      if (!safeLocalName(node.name)) throw new Error('Недопустимое имя файла.');
      const folder = path.join(app.getPath('userData'), 'read-cache', randomUUID());
      await fs.mkdir(folder, { recursive: true });
      const filename = path.join(folder, node.name);
      const response = await api.raw(`/api/files/${data.id}/download?version=${node.version}`);
      const content = Buffer.from(await response.arrayBuffer());
      if (sha256(content) !== response.headers.get('X-File-SHA256')) throw new Error('Контрольная сумма файла не совпадает.');
      await fs.writeFile(filename, content, { mode: 0o444 });
      await fs.chmod(filename, 0o444); // Windows: marks file read-only; never uploaded.
      const error = await shell.openPath(filename);
      if (error) throw new Error('Не удалось открыть документ. Установите Word/Excel: ' + error);
      return { opened: true };
    }
    case 'edit': return sync.start(data.id, currentUser.id, filename => shell.openPath(filename));
    case 'finish': return sync.finish(data.id);
    case 'discard': {
      const answer = await dialog.showMessageBox(window, { type: 'warning', buttons: ['Отмена', 'Прекратить'], defaultId: 0, cancelId: 0,
        message: 'Прекратить работу с этой копией?', detail: 'Изменения, которые ещё не отправлены, не попадут на сервер. Сначала сохраните копию отдельно. Локальный файл останется в рабочем каталоге.' });
      if (answer.response === 1) await sync.discard(data.id);
      return null;
    }
    case 'copy-local': {
      const filename = sync.localPath(data.id);
      const result = await dialog.showSaveDialog(window, { defaultPath: path.basename(filename) });
      if (!result.canceled && result.filePath) await fs.copyFile(filename, result.filePath);
      return null;
    }
    case 'show-local': shell.showItemInFolder(sync.localPath(data.id)); return null;
    case 'auto-close': sync.setAutoClose(data.id, Boolean(data.enabled)); return null;
    case 'rollback': {
      if (sync.list().some(edit => edit.id === data.id)) throw new Error('Сначала завершите редактирование этой рабочей копии.');
      const lock = await api.post<{ lock_token: string; version: number }>(`/api/files/${data.id}/checkout`);
      try { return await api.post(`/api/files/${data.id}/rollback`, { lock_token: lock.lock_token, base_version: lock.version, version: positive(data.version) }); }
      finally { await api.post(`/api/files/${data.id}/release`, { lock_token: lock.lock_token }); }
    }
    case 'users': return api.request('/api/admin/users');
    case 'user-create': return api.post('/api/admin/users', {
      username: text(data.username, 64), full_name: text(data.full_name, 160), password: text(data.password, 256), role: data.role,
    });
    case 'user-active': return api.request(`/api/admin/users/${data.id}`, { method: 'PATCH', body: JSON.stringify({ active: Boolean(data.active) }) });
    case 'user-password': return api.post(`/api/admin/users/${data.id}/password`, { password: text(data.password, 256) });
    case 'user-import': {
      const selected = await selectedFile(['csv']);
      if (!selected) return null;
      if (selected.content.length > 1024 * 1024) throw new Error('CSV должен быть меньше 1 МБ.');
      const form = new FormData(); form.set('file', new Blob([new Uint8Array(selected.content)]), 'users.csv');
      return api.request('/api/admin/users/import', { method: 'POST', body: form });
    }
    case 'audit': return api.request('/api/admin/audit' + (data.before ? `?before=${positive(data.before)}` : ''));
    default: throw new Error('Неизвестная команда.');
  }
}

function registerIpc() {
  const handler = (channel: string, callback: (...args: any[]) => Promise<unknown>) => {
    ipcMain.handle(channel, (event, ...args) => protect(async () => {
      if (event.sender !== window.webContents || event.senderFrame !== window.webContents.mainFrame) throw new Error('Недопустимый источник команды.');
      return callback(...args);
    }));
  };
  handler('settings', async () => ({ server: api.server, user: currentUser }));
  handler('edits', async () => sync.list());
  handler('login', async (server: string, username: string, password: string) => {
    const validated = normalizeServer(server);
    if (sync.list().length && (validated !== api.server || username.trim().toLowerCase() !== currentUser?.username)) {
      throw new Error('Есть рабочие копии. Войдите под той же учётной записью или сначала сохраните их отдельно.');
    }
    currentUser = await api.login(validated, text(username, 64), text(password, 256));
    await rememberServer(validated); await sync.initialize(currentUser.id);
    return currentUser;
  });
  handler('logout', async () => {
    if (sync.list().length) throw new Error('Сначала завершите редактирование или сохраните копии и прекратите их обработку.');
    await api.post('/api/auth/logout');
    await sync.shutdown(); api.credentials = null; currentUser = null; await persistCredentials(null);
    return null;
  });
  handler('action', action);
}

async function startup() {
  await fs.mkdir(app.getPath('userData'), { recursive: true });
  api = new Api(persistCredentials);
  sync = new SyncManager(api, path.join(app.getPath('userData'), 'edit-cache'), (edits: EditStatus[]) => {
    if (window && !window.isDestroyed()) window.webContents.send('edits-changed', edits);
  });
  try {
    const settings = JSON.parse(await fs.readFile(path.join(app.getPath('userData'), 'settings.json'), 'utf8'));
    api.server = normalizeServer(settings.server);
    if (safeStorage.isEncryptionAvailable()) {
      api.credentials = JSON.parse(safeStorage.decryptString(await fs.readFile(path.join(app.getPath('userData'), 'credentials.dat'))));
      currentUser = await api.request<User>('/api/auth/me');
      await sync.initialize(currentUser.id);
    }
  } catch { currentUser = null; /* Login remains available; preserved edit copies load after login. */ }
  window = new BrowserWindow({ width: 1200, height: 800, minWidth: 950, minHeight: 650,
    title: 'Общий файловый архив', backgroundColor: '#f5f7fb', autoHideMenuBar: true,
    webPreferences: { preload: path.join(__dirname, '../preload/index.js'), contextIsolation: true,
      nodeIntegration: false, sandbox: true, webSecurity: true, spellcheck: false } });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', event => event.preventDefault());
  window.webContents.session.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
  registerIpc();
  window.on('close', event => {
    if (quitting || !sync.list().length) return;
    event.preventDefault();
    void dialog.showMessageBox(window, { type: 'warning', buttons: ['Остаться', 'Выйти, сохранив копии'], cancelId: 0, defaultId: 0,
      message: 'Есть открытые рабочие копии', detail: 'Сохраните документы в Word/Excel. После выхода автозагрузка остановится, блокировки истекут по тайм-ауту. Локальные файлы останутся и будут доступны при следующем входе.' }).then(async result => {
      if (result.response === 1) { quitting = true; await sync.shutdown(); app.quit(); }
    });
  });
  await window.loadFile(path.join(__dirname, '../../dist/index.html'));
}

if (!app.requestSingleInstanceLock()) app.quit();
else {
  app.on('second-instance', () => { if (window) { if (window.isMinimized()) window.restore(); window.focus(); } });
  app.whenReady().then(startup).catch(error => {
    dialog.showErrorBox('Не удалось запустить архив', error instanceof Error ? error.message : 'Ошибка запуска'); app.quit();
  });
  app.on('window-all-closed', () => app.quit());
}
