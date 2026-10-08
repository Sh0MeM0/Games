import { useEffect, useRef, useState } from 'react';
import { ArrowLeft, CalendarDays, Check, ChevronRight, Clock3, Play, Plus, Save, Square, Trash2, Users, X } from 'lucide-react';
import { api, json } from '../api';
import type { Task, Workspace } from '../types';
import TaskForm, { canEditTask, formatDuration, initials, priorityLabels, shortDate, statusLabels } from './TaskForm';

type Props = { taskId: string | null; workspace: Workspace; onClose: () => void; onRefresh: () => Promise<void> };
type Draft = Pick<Task, 'title' | 'description' | 'status' | 'priority' | 'progress' | 'assigneeIds' | 'dueDate' | 'projectId'>;

export default function TaskDetail({ taskId, workspace, onClose, onRefresh }: Props) {
  const dialog = useRef<HTMLElement>(null);
  const [selectedId, setSelectedId] = useState(taskId);
  const task = workspace.tasks.find(item => item.id === selectedId);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [saved, setSaved] = useState(false);
  const [addingSubtask, setAddingSubtask] = useState(false);
  const [deleteConfirm, setDeleteConfirm] = useState(false);
  const [userSearch, setUserSearch] = useState('');
  const [now, setNow] = useState(Date.now());
  useEffect(() => { setSelectedId(taskId); }, [taskId]);
  useEffect(() => {
    if (task) setDraft({ title: task.title, description: task.description, status: task.status, priority: task.priority, progress: task.progress, assigneeIds: [...task.assigneeIds], dueDate: task.dueDate, projectId: task.projectId });
    setSaved(false); setError(''); setDeleteConfirm(false); setUserSearch('');
  }, [task?.id, task?.updatedAt]);
  useEffect(() => { const id = window.setInterval(() => setNow(Date.now()), 1000); return () => window.clearInterval(id); }, []);
  useEffect(() => { const handleKey = (event: KeyboardEvent) => { const dialogs = document.querySelectorAll('[role="dialog"]'); if (event.key === 'Escape' && !busy && dialogs[dialogs.length - 1] === dialog.current) onClose(); }; window.addEventListener('keydown', handleKey); return () => window.removeEventListener('keydown', handleKey); }, [busy, onClose]);
  if (!taskId) return null;
  if (!task || !draft) return <div className="detail-backdrop"><aside className="detail-drawer" ref={dialog} role="dialog" aria-modal="true" aria-label="Задача недоступна"><button className="icon-button detail-not-found-close" onClick={onClose} aria-label="Закрыть"><X /></button><div className="empty-state"><h3>Задача недоступна</h3><p>Возможно, её удалили или изменили права доступа.</p><button className="button button-secondary" onClick={onClose}>Закрыть</button></div></aside></div>;

  const editable = canEditTask(workspace, task);
  const parent = workspace.tasks.find(item => item.id === task.parentId);
  const subtasks = workspace.tasks.filter(item => item.parentId === task.id);
  const entries = workspace.timeEntries.filter(item => item.taskId === task.id).sort((a, b) => b.startAt.localeCompare(a.startAt));
  const timer = workspace.runningTimer;
  const runningHere = timer?.taskId === task.id;
  const elapsed = runningHere ? Math.max(0, Math.floor((now - new Date(timer.startedAt).getTime()) / 1000)) : 0;
  const trackedSeconds = entries.reduce((sum, entry) => sum + entry.durationSeconds, 0) + elapsed;
  const users = workspace.users.filter(user => user.active && `${user.name} ${user.email}`.toLowerCase().includes(userSearch.toLowerCase()));
  const changeDraft = (value: Partial<Draft>) => { setDraft({ ...draft, ...value }); setSaved(false); };
  const dirty = JSON.stringify(draft) !== JSON.stringify({ title: task.title, description: task.description, status: task.status, priority: task.priority, progress: task.progress, assigneeIds: task.assigneeIds, dueDate: task.dueDate, projectId: task.projectId });

  async function mutate(action: () => Promise<unknown>) {
    setBusy(true); setError('');
    try { await action(); await onRefresh(); return true; }
    catch (cause) { setError(cause instanceof Error ? cause.message : 'Не удалось сохранить изменение.'); return false; }
    finally { setBusy(false); }
  }
  async function save() {
    const successful = await mutate(() => api(`/api/tasks/${task!.id}`, json('PATCH', { ...draft, title: draft!.title.trim(), description: draft!.description.trim() })));
    if (successful) setSaved(true);
  }
  async function deleteTask() {
    const successful = await mutate(() => api(`/api/tasks/${task!.id}`, { method: 'DELETE' }));
    if (successful) onClose();
  }

  return <>
    <div className="detail-backdrop" onMouseDown={event => { if (event.target === event.currentTarget && !busy) onClose(); }}>
      <aside className="detail-drawer" ref={dialog} role="dialog" aria-modal="true" aria-labelledby="detail-heading">
        <header className="detail-header"><div>{parent ? <button className="detail-breadcrumb" onClick={() => setSelectedId(parent.id)}><ArrowLeft size={15} />{parent.title}</button> : <span className="detail-breadcrumb">{workspace.projects.find(item => item.id === task.projectId)?.name || 'Задачи'}<ChevronRight size={14} />Детали задачи</span>}</div><button className="icon-button" aria-label="Закрыть задачу" onClick={onClose} disabled={busy}><X size={21} /></button></header>
        <div className="detail-scroll">
          {error && <div className="feature-error" role="alert">{error}</div>}
          <div className="detail-heading"><span className={`badge badge-status-${task.status}`}>{statusLabels[task.status]}</span><span className="muted detail-task-id">#{task.id.slice(-6).toUpperCase()}</span></div>
          <label className="detail-title-field"><span className="sr-only" id="detail-heading">Название задачи</span><textarea aria-label="Название задачи" value={draft.title} onChange={event => changeDraft({ title: event.target.value })} maxLength={200} rows={2} disabled={!editable} /></label>
          {!editable && <p className="detail-readonly">Редактирование доступно автору, исполнителям и администратору.</p>}
          <div className="detail-fields"><label><span>Статус</span><select className="select" aria-label="Статус" value={draft.status} disabled={!editable || !!subtasks.length} onChange={event => { const status = event.target.value as Task['status']; changeDraft({ status, ...(status === 'done' ? { progress: 100 } : draft.status === 'done' ? { progress: 0 } : {}) }); }}>{Object.entries(statusLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label><label><span>Приоритет</span><select className="select" aria-label="Приоритет" value={draft.priority} disabled={!editable} onChange={event => changeDraft({ priority: event.target.value as Task['priority'] })}>{Object.entries(priorityLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label><label><span><CalendarDays size={14} />Срок выполнения</span><input className="input" type="date" value={draft.dueDate || ''} disabled={!editable} onChange={event => changeDraft({ dueDate: event.target.value || null })} /></label><label><span>Проект</span><select className="select" aria-label="Проект" value={draft.projectId || ''} disabled={!editable} onChange={event => changeDraft({ projectId: event.target.value || null })}><option value="">Без проекта</option>{workspace.projects.map(project => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label></div>
          <section className="detail-section"><div className="detail-section-title"><h3>Описание</h3></div><textarea className="textarea detail-description" value={draft.description} rows={4} placeholder={editable ? 'Добавьте контекст, требования и полезные ссылки…' : 'Описание не добавлено'} disabled={!editable} onChange={event => changeDraft({ description: event.target.value })} maxLength={10000} /></section>
          <section className="detail-section"><div className="detail-section-title"><h3>Прогресс выполнения</h3><strong className="detail-progress-value">{draft.progress}%</strong></div><input aria-label="Прогресс выполнения" className="detail-progress-slider" type="range" min="0" max="100" step="5" value={draft.progress} disabled={!editable || !!subtasks.length} onChange={event => { const progress = Number(event.target.value); changeDraft({ progress, ...(draft.status === 'done' && progress < 100 ? { status: 'in_progress' } : {}) }); }} /><div className="detail-range-labels"><span>{subtasks.length ? 'Рассчитывается по подзадачам' : 'Только начали'}</span><span>Всё готово</span></div></section>
          <section className="detail-section"><div className="detail-section-title"><h3><Users size={17} />Исполнители</h3><span className="detail-count">{draft.assigneeIds.length}</span></div><div className="detail-assignees">{draft.assigneeIds.map(id => { const user = workspace.users.find(item => item.id === id); return user && <span className="detail-assignee" key={id}><span className="avatar" style={{ background: user.avatarColor }}>{initials(user.name)}</span><span>{user.name}</span>{editable && <button className="icon-button" aria-label={`Убрать исполнителя ${user.name}`} onClick={() => changeDraft({ assigneeIds: draft.assigneeIds.filter(value => value !== id) })}><X size={13} /></button>}</span>; })}{!draft.assigneeIds.length && <span className="muted">Исполнители пока не назначены</span>}</div>{editable && <details className="detail-add-assignees"><summary><Plus size={14} />Назначить исполнителей</summary><input className="input" aria-label="Поиск исполнителей" placeholder="Имя или почта" value={userSearch} onChange={event => setUserSearch(event.target.value)} /><div className="detail-assignee-options">{users.map(user => <button key={user.id} className={draft.assigneeIds.includes(user.id) ? 'selected' : ''} onClick={() => changeDraft({ assigneeIds: draft.assigneeIds.includes(user.id) ? draft.assigneeIds.filter(id => id !== user.id) : [...draft.assigneeIds, user.id] })}><span className="avatar tf-small-avatar" style={{ background: user.avatarColor }}>{initials(user.name)}</span>{user.name}{draft.assigneeIds.includes(user.id) && <Check size={15} />}</button>)}{!users.length && <p className="muted">Участники не найдены</p>}</div></details>}</section>
          <section className="detail-section"><div className="detail-section-title"><h3>Подзадачи</h3><span className="detail-count">{subtasks.filter(item => item.status === 'done').length}/{subtasks.length}</span>{editable && <button className="button button-ghost detail-small-button" onClick={() => setAddingSubtask(true)}><Plus size={15} />Добавить</button>}</div><div className="detail-subtasks">{subtasks.map(item => <div className="detail-subtask" key={item.id}><button className={`detail-subtask-check ${item.status === 'done' ? 'checked' : ''}`} disabled={busy || !canEditTask(workspace, item) || workspace.tasks.some(child => child.parentId === item.id)} aria-label={item.status === 'done' ? `Вернуть подзадачу ${item.title} в работу` : `Завершить подзадачу ${item.title}`} onClick={() => void mutate(() => api(`/api/tasks/${item.id}`, json('PATCH', { status: item.status === 'done' ? 'todo' : 'done', progress: item.status === 'done' ? 0 : 100 })))}>{item.status === 'done' && <Check size={14} />}</button><button className={`detail-subtask-title ${item.status === 'done' ? 'complete' : ''}`} onClick={() => setSelectedId(item.id)}>{item.title}</button><div className="avatar-stack">{item.assigneeIds.slice(0, 2).map(id => { const user = workspace.users.find(value => value.id === id); return user && <span key={id} className="avatar tf-small-avatar" title={user.name} style={{ background: user.avatarColor }}>{initials(user.name)}</span>; })}</div><ChevronRight size={15} className="muted" /></div>)}{!subtasks.length && <p className="detail-no-subtasks">Разбейте большую задачу на понятные шаги.</p>}</div></section>
          <section className="detail-section"><div className="detail-section-title"><h3><Clock3 size={17} />Учёт времени</h3><strong>{formatDuration(trackedSeconds)}</strong></div><div className={`detail-timer-panel ${runningHere ? 'running' : ''}`}><div><span className="detail-timer-caption">{runningHere ? 'Вы работаете над задачей' : 'Начните работу с таймером'}</span><strong>{runningHere ? new Date(elapsed * 1000).toISOString().slice(11, 19) : '00:00:00'}</strong></div>{runningHere ? <button className="button button-primary" disabled={busy} onClick={() => void mutate(() => api('/api/timer/stop', json('POST', {})))}><Square size={13} fill="currentColor" />Остановить</button> : <button className="button button-primary" disabled={busy || !!timer || !editable} title={timer ? 'Сначала остановите таймер другой задачи в разделе «Учёт времени»' : ''} onClick={() => void mutate(() => api('/api/timer/start', json('POST', { taskId: task.id })))}><Play size={14} fill="currentColor" />Начать</button>}</div>{timer && !runningHere && <p className="detail-timer-hint">У вас уже запущен таймер другой задачи. Остановите его в разделе «Учёт времени».</p>}{entries.length > 0 && <div className="detail-time-entries">{entries.slice(0, 5).map(entry => <div key={entry.id}><span>{workspace.users.find(user => user.id === entry.userId)?.name || 'Участник'}<small>{entry.note || shortDate(entry.startAt)}</small></span><strong>{formatDuration(entry.durationSeconds)}</strong></div>)}</div>}</section>
          <p className="detail-created">Создал(а) {workspace.users.find(user => user.id === task.createdBy)?.name || 'участник'} · {shortDate(task.createdAt)}</p>
          {deleteConfirm && <div className="detail-delete-confirm"><p>Удалить задачу{Boolean(subtasks.length) && ' и все её подзадачи'}? Это действие нельзя отменить.</p><button className="button button-danger" disabled={busy} onClick={() => void deleteTask()}>Удалить задачу</button><button className="button button-secondary" onClick={() => setDeleteConfirm(false)} disabled={busy}>Отмена</button></div>}
        </div>
        <footer className="detail-footer">{editable && <button className="icon-button detail-delete-button" aria-label="Удалить задачу" title="Удалить задачу" disabled={busy} onClick={() => setDeleteConfirm(!deleteConfirm)}><Trash2 size={18} /></button>}<span className="detail-save-state">{saved ? <><Check size={15} />Сохранено</> : dirty ? 'Есть несохранённые изменения' : 'Все изменения сохранены'}</span>{editable && <button className="button button-primary" disabled={busy || !draft.title.trim() || !dirty} onClick={() => void save()}><Save size={16} />{busy ? 'Сохраняем…' : 'Сохранить'}</button>}</footer>
      </aside>
    </div>
    {addingSubtask && <TaskForm workspace={workspace} parentId={task.id} projectId={task.projectId || undefined} onClose={() => setAddingSubtask(false)} onRefresh={onRefresh} />}
  </>;
}
