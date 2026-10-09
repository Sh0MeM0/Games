export interface User { id: string; username: string; full_name: string; role: 'admin' | 'user'; active: boolean }
export interface ArchiveNode {
  id: string; parent_id: string | null; name: string; kind: 'file' | 'folder'; deleted: boolean;
  version: number; size: number; updated_at: string;
  lock: { user_id: string; full_name: string; expires_at: string } | null;
}
export interface Version { number: number; author: string; created_at: string; size: number; note: string; sha256: string }
export interface EditStatus {
  id: string; name: string; path: string; baseVersion: number;
  state: 'synced' | 'saving' | 'offline' | 'conflict' | 'recovered'; message: string; autoClose: boolean;
}
export interface Audit { id: number; user: string; action: string; detail: string; created_at: string }
export type Action =
  | { type: 'list'; parentId?: string; search?: string }
  | { type: 'details'; id: string }
  | { type: 'folder'; name: string; parentId?: string }
  | { type: 'upload'; parentId?: string }
  | { type: 'download'; id: string; name: string; version?: number }
  | { type: 'read'; id: string }
  | { type: 'edit'; id: string }
  | { type: 'finish'; id: string }
  | { type: 'discard'; id: string }
  | { type: 'copy-local'; id: string }
  | { type: 'show-local'; id: string }
  | { type: 'auto-close'; id: string; enabled: boolean }
  | { type: 'rename'; id: string; name: string }
  | { type: 'delete'; id: string }
  | { type: 'trash' }
  | { type: 'restore'; id: string }
  | { type: 'versions'; id: string }
  | { type: 'rollback'; id: string; version: number }
  | { type: 'force-unlock'; id: string }
  | { type: 'users' }
  | { type: 'user-create'; username: string; full_name: string; password: string; role: string }
  | { type: 'user-active'; id: string; active: boolean }
  | { type: 'user-password'; id: string; password: string }
  | { type: 'user-import' }
  | { type: 'audit'; before?: number };
export interface Result<T = unknown> { ok: boolean; value?: T; error?: string }
export interface Bridge {
  settings(): Promise<Result<{ server: string; user: User | null }>>;
  login(server: string, username: string, password: string): Promise<Result<User>>;
  logout(): Promise<Result>;
  action<T = unknown>(action: Action): Promise<Result<T>>;
  edits(): Promise<Result<EditStatus[]>>;
  onEdits(callback: (edits: EditStatus[]) => void): () => void;
}
