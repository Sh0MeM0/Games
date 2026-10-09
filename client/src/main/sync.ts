import { createHash, randomUUID } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import fs from 'node:fs/promises';
import path from 'node:path';
import type { ArchiveNode, EditStatus } from '../shared/types';
import { Api, ApiError } from './api';

const execute = promisify(execFile);
export const sha256 = (content: Uint8Array) => createHash('sha256').update(content).digest('hex');

interface Edit extends EditStatus {
  userId: string; lockToken: string; expiresAt: string; uploadedHash: string;
  lastHeartbeat: number; heartbeatSeconds: number; busy: boolean;
  observedLocked: boolean; unlockedSince: number; fingerprint: string; changedAt: number;
}

// Check the document's exclusive Windows file handle, not the Word/Excel process.
// This is an optional heuristic: some Office versions don't retain that handle.
export async function officeLocked(filename: string): Promise<boolean | null> {
  if (process.platform !== 'win32') return null;
  const script = "$p=[Environment]::GetEnvironmentVariable('ARCHIVE_DOCUMENT'); try { $f=[IO.File]::Open($p,'Open','ReadWrite','None'); $f.Close(); 'free' } catch [IO.IOException] { 'busy' } catch { 'unknown' }";
  try {
    const { stdout } = await execute('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script],
      { env: { ...process.env, ARCHIVE_DOCUMENT: filename }, windowsHide: true, timeout: 5000 });
    return stdout.trim() === 'busy' ? true : stdout.trim() === 'free' ? false : null;
  } catch { return null; }
}

export class SyncManager {
  private sessions = new Map<string, Edit>();
  private timer: ReturnType<typeof setInterval> | null = null;
  constructor(private api: Api, private root: string, private notify: (values: EditStatus[]) => void) {}

  list(): EditStatus[] {
    return [...this.sessions.values()].map(({ id, name, path: filename, baseVersion, state, message, autoClose }) =>
      ({ id, name, path: filename, baseVersion, state, message, autoClose }));
  }

  async initialize(userId: string) {
    if (this.timer) clearInterval(this.timer);
    this.sessions.clear();
    await fs.mkdir(this.root, { recursive: true });
    for (const folder of await fs.readdir(this.root)) {
      try {
        const completed = JSON.parse(await fs.readFile(path.join(this.root, folder, 'completed.json'), 'utf8'));
        if (Number.isFinite(completed.completedAt) && Date.now() - completed.completedAt > 7 * 86400_000) {
          await fs.rm(path.join(this.root, folder), { recursive: true, force: true });
        }
        continue;
      } catch { /* Active copies have no completion marker. */ }
      try {
        const manifest = path.join(this.root, folder, 'session.json');
        const edit = JSON.parse(await fs.readFile(manifest, 'utf8')) as Edit;
        const expected = path.join(this.root, folder, edit.name);
        if (edit.userId !== userId || !safeLocalName(edit.name) || edit.path !== expected) continue;
        await fs.access(edit.path);
        edit.state = 'recovered'; edit.busy = false; edit.autoClose = false;
        edit.message = 'Копия после предыдущего запуска. Автозагрузка остановлена. Сохраните копию и сравните с сервером.';
        // Recovered leases are never resumed implicitly, even if still valid.
        this.sessions.set(edit.id, edit);
      } catch { /* Leave unreadable caches on disk for manual recovery. */ }
    }
    this.timer = setInterval(() => { for (const edit of this.sessions.values()) void this.tick(edit); }, 2000);
    this.emit();
  }

  private emit() { this.notify(this.list()); }
  private async persist(edit: Edit) {
    const manifest = path.join(path.dirname(edit.path), 'session.json');
    await fs.writeFile(manifest + '.tmp', JSON.stringify(edit), { mode: 0o600 });
    await fs.rename(manifest + '.tmp', manifest);
  }

  async start(id: string, userId: string, open: (filename: string) => Promise<string>) {
    if (this.sessions.has(id)) throw new Error('Этот документ уже есть в рабочих копиях. Сначала завершите его обработку.');
    const lock = await this.api.post<{ lock_token: string; version: number; expires_at: string; heartbeat_seconds: number; node: ArchiveNode }>(`/api/files/${id}/checkout`);
    let filename: string | null = null;
    try {
      if (!safeLocalName(lock.node.name)) throw new Error('Недопустимое имя документа.');
      const folder = path.join(this.root, randomUUID());
      await fs.mkdir(folder, { recursive: true });
      filename = path.join(folder, lock.node.name);
      const response = await this.api.raw(`/api/files/${id}/download?version=${lock.version}`);
      const content = Buffer.from(await response.arrayBuffer());
      if (sha256(content) !== response.headers.get('X-File-SHA256')) throw new Error('Контрольная сумма скачанного файла не совпадает.');
      await fs.writeFile(filename, content, { mode: 0o600 });
      const edit: Edit = { id, name: lock.node.name, path: filename, userId, lockToken: lock.lock_token,
        baseVersion: lock.version, expiresAt: lock.expires_at, uploadedHash: sha256(content),
        lastHeartbeat: Date.now(), heartbeatSeconds: lock.heartbeat_seconds, busy: false,
        state: 'synced', message: 'Открыто для редактирования. Сохранения отправляются автоматически.',
        autoClose: false, observedLocked: false, unlockedSince: 0, fingerprint: '', changedAt: Date.now() };
      await this.persist(edit);
      this.sessions.set(id, edit); this.emit();
      const error = await open(filename);
      if (error) throw new Error('Не удалось открыть документ. Установите Word/Excel: ' + error);
    } catch (error) {
      this.sessions.delete(id); this.emit();
      try { await this.api.post(`/api/files/${id}/release`, { lock_token: lock.lock_token }); } catch { /* Lease expires. */ }
      if (filename) await fs.rm(path.dirname(filename), { recursive: true, force: true });
      throw error;
    }
  }

  private async heartbeat(edit: Edit) {
    if (Date.now() >= Date.parse(edit.expiresAt)) throw new ApiError(409, 'Блокировка истекла. Копия сохранена; сравните её с актуальной версией.');
    if (Date.now() - edit.lastHeartbeat < edit.heartbeatSeconds * 1000) return;
    const result = await this.api.post<{ expires_at: string }>(`/api/files/${edit.id}/heartbeat`, { lock_token: edit.lockToken });
    edit.expiresAt = result.expires_at; edit.lastHeartbeat = Date.now();
  }

  private async upload(edit: Edit) {
    const before = await fs.stat(edit.path);
    const content = await fs.readFile(edit.path);
    const after = await fs.stat(edit.path);
    if (before.mtimeMs !== after.mtimeMs || before.size !== after.size) return false;
    const digest = sha256(content);
    if (digest === edit.uploadedHash) return true;
    edit.state = 'saving'; edit.message = 'Отправка сохранённой версии…'; this.emit();
    const form = new FormData();
    form.set('lock_token', edit.lockToken); form.set('base_version', String(edit.baseVersion));
    form.set('file', new Blob([new Uint8Array(content)]), edit.name);
    const node = await this.api.request<ArchiveNode>(`/api/files/${edit.id}/versions`, { method: 'POST', body: form });
    edit.baseVersion = node.version; edit.uploadedHash = digest;
    if (node.lock) edit.expiresAt = node.lock.expires_at;
    await this.persist(edit);
    return true;
  }

  private failure(edit: Edit, error: unknown) {
    const status = error instanceof ApiError ? error.status : 0;
    edit.state = [400, 401, 403, 404, 409, 413, 415, 422, 423].includes(status) ? 'conflict' : 'offline';
    edit.message = error instanceof Error && !('code' in error) ? error.message
      : 'Рабочий файл временно недоступен или занят. Проверьте документ и свободное место на диске. Копия сохранена.';
  }

  private async tick(edit: Edit) {
    if (edit.busy || ['conflict', 'recovered'].includes(edit.state)) return;
    edit.busy = true;
    try {
      await this.heartbeat(edit);
      const stats = await fs.stat(edit.path);
      const fingerprint = `${stats.mtimeMs}:${stats.size}`;
      if (fingerprint !== edit.fingerprint) { edit.fingerprint = fingerprint; edit.changedAt = Date.now(); }
      if (Date.now() - edit.changedAt >= 2500) {
        await this.upload(edit);
        edit.state = 'synced'; edit.message = `Версия ${edit.baseVersion} на сервере. При завершении закройте документ и нажмите кнопку.`;
      }
      if (edit.autoClose) {
        const locked = await officeLocked(edit.path);
        if (locked === true) { edit.observedLocked = true; edit.unlockedSince = 0; }
        else if (locked === false && edit.observedLocked) {
          edit.unlockedSince ||= Date.now();
          if (Date.now() - edit.unlockedSince > 10_000 && Date.now() - edit.changedAt > 5000) {
            if (!await this.upload(edit)) return;
            await this.api.post(`/api/files/${edit.id}/release`, { lock_token: edit.lockToken });
            await this.remove(edit);
          }
        } else { edit.unlockedSince = 0; }
      }
    } catch (error) { this.failure(edit, error); }
    finally { edit.busy = false; this.emit(); }
  }

  private get(id: string) { const edit = this.sessions.get(id); if (!edit) throw new Error('Рабочая копия не найдена.'); return edit; }
  localPath(id: string) { return this.get(id).path; }
  setAutoClose(id: string, enabled: boolean) {
    const edit = this.get(id); edit.autoClose = enabled; edit.observedLocked = false; edit.unlockedSince = 0;
    this.emit();
  }
  private async remove(edit: Edit) {
    // Keep completed copies for 7 days, including documents with unsaved Office buffers.
    // Removing the manifest excludes a completed copy from recovery, never deleting the document here.
    await fs.rm(path.join(path.dirname(edit.path), 'session.json'), { force: true });
    await fs.writeFile(path.join(path.dirname(edit.path), 'completed.json'), JSON.stringify({ completedAt: Date.now() }));
    this.sessions.delete(edit.id);
  }
  async finish(id: string) {
    const edit = this.get(id);
    if (edit.busy) throw new Error('Дождитесь текущей синхронизации и повторите.');
    if (['conflict', 'recovered'].includes(edit.state)) throw new Error('Автозагрузка остановлена. Сначала сохраните локальную копию.');
    if (await officeLocked(edit.path) === true) throw new Error('Документ ещё занят. Сохраните и закройте его в Word/Excel.');
    edit.busy = true;
    try {
      edit.lastHeartbeat = 0; await this.heartbeat(edit);
      // Require a stable saved file before releasing the lease.
      const before = await fs.stat(edit.path);
      await new Promise(resolve => setTimeout(resolve, 1200));
      const after = await fs.stat(edit.path);
      if (before.mtimeMs !== after.mtimeMs || before.size !== after.size || !await this.upload(edit)) {
        throw new Error('Файл ещё сохраняется. Подождите и повторите завершение.');
      }
      await this.api.post(`/api/files/${id}/release`, { lock_token: edit.lockToken });
      await this.remove(edit);
    } catch (error) { this.failure(edit, error); throw error; }
    finally { edit.busy = false; this.emit(); }
  }
  async discard(id: string) {
    const edit = this.get(id);
    if (edit.busy) throw new Error('Дождитесь текущей синхронизации.');
    try { await this.api.post(`/api/files/${id}/release`, { lock_token: edit.lockToken }); } catch (error) {
      if (!(error instanceof ApiError) || ![401, 404, 409].includes(error.status)) throw error;
    }
    await this.remove(edit); this.emit();
  }
  async shutdown() { if (this.timer) clearInterval(this.timer); this.timer = null; }
}

export function safeLocalName(name: string): boolean {
  return name.length > 0 && name.length <= 180 && !/[\\/<>:"|?*\x00-\x1f]/.test(name)
    && !['.', '..'].includes(name) && !/[. ]$/.test(name)
    && !/^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)/i.test(name);
}
