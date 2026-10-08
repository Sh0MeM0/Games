import { useEffect, useRef, useState } from 'react';
import { Check, Search, X } from 'lucide-react';
import { api, json } from '../api';
import type { Workspace, Task } from '../types';

type Props = { workspace: Workspace; onClose: () => void; onRefresh: () => Promise<void>; projectId?: string; parentId?: string };

export const statusLabels: Record<Task['status'], string> = { todo: 'К выполнению', in_progress: 'В работе', review: 'На проверке', done: 'Готово' };
export const priorityLabels: Record<Task['priority'], string> = { low: 'Низкий', medium: 'Средний', high: 'Высокий' };
export const initials = (name: string) => name.split(' ').filter(Boolean).slice(0, 2).map(word => word[0]).join('').toUpperCase();
export const formatDuration = (seconds: number) => { const minutes = Math.floor(seconds / 60); return `${Math.floor(minutes / 60)} ч ${minutes % 60} мин`; };
export const shortDate = (date: string) => new Date(date.length === 10 ? `${date}T12:00:00` : date).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' });
export const canEditTask = (workspace: Workspace, task: Task) => workspace.user.role === 'admin' || task.createdBy === workspace.user.id || task.assigneeIds.includes(workspace.user.id);

export default function TaskForm({ workspace, onClose, onRefresh, projectId, parentId }: Props) {
  const dialog = useRef<HTMLElement>(null);
  const parent = workspace.tasks.find(task => task.id === parentId);
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [project, setProject] = useState(projectId || parent?.projectId || '');
  const [assigneeIds, setAssigneeIds] = useState<string[]>([workspace.user.id]);
  const [status, setStatus] = useState<Task['status']>('todo');
  const [priority, setPriority] = useState<Task['priority']>('medium');
  const [dueDate, setDueDate] = useState('');
  const [search, setSearch] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const users = workspace.users.filter(user => user.active && `${user.name} ${user.email}`.toLowerCase().includes(search.toLowerCase()));
  const toggleUser = (id: string) => setAssigneeIds(current => current.includes(id) ? current.filter(value => value !== id) : [...current, id]);
  useEffect(() => { const handleKey = (event: KeyboardEvent) => { const dialogs = document.querySelectorAll('[role="dialog"]'); if (event.key === 'Escape' && !busy && dialogs[dialogs.length - 1] === dialog.current) onClose(); }; window.addEventListener('keydown', handleKey); return () => window.removeEventListener('keydown', handleKey); }, [busy, onClose]);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setError(''); setBusy(true);
    try {
      await api('/api/tasks', json('POST', { title: title.trim(), description: description.trim(), projectId: project || null, parentId: parentId || null, assigneeIds, status, priority, dueDate: dueDate || null, progress: status === 'done' ? 100 : 0 }));
      await onRefresh(); onClose();
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Не удалось создать задачу. Попробуйте ещё раз.'); }
    finally { setBusy(false); }
  }

  return <div className="modal-backdrop" onMouseDown={event => { if (event.target === event.currentTarget && !busy) onClose(); }}>
    <section className="modal tf-modal" ref={dialog} role="dialog" aria-modal="true" aria-labelledby="task-form-title">
      <form onSubmit={submit}>
        <header className="modal-header"><div><h2 id="task-form-title">{parentId ? 'Новая подзадача' : 'Новая задача'}</h2><p className="muted">{parent ? `Для задачи «${parent.title}»` : 'Соберите команду и определите следующий шаг'}</p></div><button type="button" className="icon-button" aria-label="Закрыть" onClick={onClose} disabled={busy}><X size={20} /></button></header>
        <div className="modal-body tf-body">
          {error && <div className="feature-error" role="alert">{error}</div>}
          <label className="form-field"><span className="field-label">Название задачи <span className="tf-required">*</span></span><input className="input" autoFocus required maxLength={200} placeholder="Что нужно сделать?" value={title} onChange={event => setTitle(event.target.value)} /></label>
          <label className="form-field"><span className="field-label">Описание</span><textarea className="textarea" rows={3} maxLength={10000} placeholder="Контекст, ожидаемый результат и полезные ссылки" value={description} onChange={event => setDescription(event.target.value)} /></label>
          <div className="tf-grid"><label className="form-field"><span className="field-label">Проект</span><select className="select" aria-label="Проект" value={project} onChange={event => setProject(event.target.value)}><option value="">Без проекта</option>{workspace.projects.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label><label className="form-field"><span className="field-label">Срок выполнения</span><input className="input" type="date" value={dueDate} onChange={event => setDueDate(event.target.value)} /></label></div>
          <div className="tf-grid"><label className="form-field"><span className="field-label">Статус</span><select className="select" aria-label="Статус" value={status} onChange={event => setStatus(event.target.value as Task['status'])}>{Object.entries(statusLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label><label className="form-field"><span className="field-label">Приоритет</span><select className="select" aria-label="Приоритет" value={priority} onChange={event => setPriority(event.target.value as Task['priority'])}>{Object.entries(priorityLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label></div>
          <div className="form-field"><span className="field-label">Исполнители <span className="muted">· {assigneeIds.length}</span></span>
            <div className="tf-selected">{assigneeIds.map(id => { const user = workspace.users.find(item => item.id === id); return user && <button type="button" key={id} className="tf-person-chip" onClick={() => toggleUser(id)}><span className="avatar tf-small-avatar" style={{ background: user.avatarColor }}>{initials(user.name)}</span>{user.name}<X size={12} /></button>; })}{!assigneeIds.length && <span className="muted">Можно назначить исполнителей позже</span>}</div>
            <div className="tf-user-picker"><label className="tf-user-search"><Search size={16} /><input aria-label="Найти исполнителя" placeholder="Поиск по имени или почте" value={search} onChange={event => setSearch(event.target.value)} /></label><div className="tf-user-options">{users.map(user => <button type="button" className={`tf-user-option ${assigneeIds.includes(user.id) ? 'selected' : ''}`} onClick={() => toggleUser(user.id)} key={user.id}><span className="avatar" style={{ background: user.avatarColor }}>{initials(user.name)}</span><span><strong>{user.name}</strong><small>{user.title || user.email}</small></span><span className="tf-checkbox">{assigneeIds.includes(user.id) && <Check size={13} />}</span></button>)}{!users.length && <p className="muted tf-no-results">Участники не найдены</p>}</div></div>
          </div>
        </div>
        <footer className="modal-footer"><button type="button" className="button button-secondary" onClick={onClose} disabled={busy}>Отмена</button><button className="button button-primary" type="submit" disabled={busy || !title.trim()}>{busy ? 'Создаём…' : parentId ? 'Создать подзадачу' : 'Создать задачу'}</button></footer>
      </form>
    </section>
  </div>;
}
