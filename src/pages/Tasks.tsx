import { useState } from 'react';
import { CalendarDays, Check, CheckSquare, ChevronDown, Clock3, LayoutGrid, List, Plus, Search, SlidersHorizontal } from 'lucide-react';
import { api, json } from '../api';
import type { Task, Workspace } from '../types';
import { canEditTask, initials, priorityLabels, shortDate, statusLabels } from '../components/TaskForm';

type Props = { workspace: Workspace; onRefresh: () => Promise<void>; onOpenTask: (id: string) => void; onCreateTask: (projectId?: string) => void; search: string; projectId: string | null };
const statuses: Task['status'][] = ['todo', 'in_progress', 'review', 'done'];

export default function Tasks({ workspace, onRefresh, onOpenTask, onCreateTask, search, projectId }: Props) {
  const [view, setView] = useState<'board' | 'list'>('board');
  const [scope, setScope] = useState('all');
  const [statusFilter, setStatusFilter] = useState('all');
  const [assigneeFilter, setAssigneeFilter] = useState('all');
  const [localSearch, setLocalSearch] = useState('');
  const [error, setError] = useState('');
  const [busyId, setBusyId] = useState<string | null>(null);
  const project = workspace.projects.find(item => item.id === projectId);
  const query = `${search} ${localSearch}`.trim().toLowerCase();
  const allTasks = workspace.tasks.filter(task => !task.parentId && (!projectId || task.projectId === projectId));
  const filtered = allTasks.filter(task => (!query || query.split(/\s+/).every(term => `${task.title} ${task.description}`.toLowerCase().includes(term))) && (scope !== 'mine' || task.assigneeIds.includes(workspace.user.id)) && (statusFilter === 'all' || task.status === statusFilter) && (assigneeFilter === 'all' || task.assigneeIds.includes(assigneeFilter)));
  const complete = allTasks.filter(task => task.status === 'done').length;

  async function moveTask(task: Task, status: Task['status']) {
    if (task.status === status || !canEditTask(workspace, task)) return;
    if (workspace.tasks.some(item => item.parentId === task.id)) { setError('Статус этой задачи рассчитывается по подзадачам. Измените статус её подзадач.'); return; }
    setError(''); setBusyId(task.id);
    try { await api(`/api/tasks/${task.id}`, json('PATCH', { status, ...(status === 'done' ? { progress: 100 } : {}) })); await onRefresh(); }
    catch (cause) { setError(cause instanceof Error ? cause.message : 'Не удалось переместить задачу.'); }
    finally { setBusyId(null); }
  }

  function TaskAvatars({ task }: { task: Task }) {
    const users = task.assigneeIds.map(id => workspace.users.find(item => item.id === id)).filter(Boolean);
    return <div className="avatar-stack" aria-label={`Исполнителей: ${users.length}`}>{users.slice(0, 3).map(user => <span key={user!.id} title={user!.name} className="avatar tasks-avatar" style={{ background: user!.avatarColor }}>{initials(user!.name)}</span>)}{users.length > 3 && <span className="avatar tasks-avatar tasks-extra-avatar">+{users.length - 3}</span>}{!users.length && <span className="tasks-unassigned">Без исполнителя</span>}</div>;
  }

  function TaskCard({ task }: { task: Task }) {
    const itemProject = workspace.projects.find(item => item.id === task.projectId);
    const subtasks = workspace.tasks.filter(item => item.parentId === task.id);
    const overdue = task.dueDate && task.status !== 'done' && task.dueDate < new Date().toLocaleDateString('sv-SE');
    const time = workspace.timeEntries.filter(item => item.taskId === task.id).reduce((sum, item) => sum + item.durationSeconds, 0);
    return <article className={`tasks-card ${busyId === task.id ? 'tasks-card-busy' : ''}`} draggable={canEditTask(workspace, task) && !subtasks.length} onDragStart={event => { event.dataTransfer.setData('text/plain', task.id); event.dataTransfer.effectAllowed = 'move'; }}>
      <div className="tasks-card-top"><span className={`badge badge-priority-${task.priority}`}>{priorityLabels[task.priority]}</span><button className="icon-button tasks-card-open" aria-label={`Открыть задачу ${task.title}`} onClick={() => onOpenTask(task.id)}><ChevronDown size={16} /></button></div>
      <button className="tasks-title-button" onClick={() => onOpenTask(task.id)}>{task.title}</button>
      {itemProject && <span className="tasks-project"><span style={{ background: itemProject.color }} />{itemProject.name}</span>}
      <div className="tasks-progress-label"><span>Прогресс</span><strong>{task.progress}%</strong></div><div className={`tasks-progress ${task.status === 'done' ? 'complete' : ''}`}><span style={{ width: `${task.progress}%` }} /></div>
      <div className="tasks-card-meta">{task.dueDate && <span className={overdue ? 'tasks-overdue' : ''}><CalendarDays size={13} />{shortDate(task.dueDate)}</span>}{subtasks.length > 0 && <span><CheckSquare size={13} />{subtasks.filter(item => item.status === 'done').length}/{subtasks.length}</span>}{time > 0 && <span><Clock3 size={13} />{(time / 3600).toFixed(1)} ч</span>}</div>
      <div className="tasks-card-bottom"><TaskAvatars task={task} /><select aria-label={`Статус задачи ${task.title}`} title={subtasks.length ? 'Статус рассчитывается по подзадачам' : undefined} className="tasks-inline-status" value={task.status} disabled={!!busyId || !canEditTask(workspace, task) || !!subtasks.length} onChange={event => void moveTask(task, event.target.value as Task['status'])}>{statuses.map(status => <option key={status} value={status}>{statusLabels[status]}</option>)}</select></div>
    </article>;
  }

  return <div className="tasks-page">
    <div className="page-title-row"><div><div className="tasks-page-eyebrow">РАБОЧЕЕ ПРОСТРАНСТВО</div><h1 className="page-title">{project?.name || 'Задачи'}</h1><p className="page-description">{project?.description || 'От идеи до результата — всё в одном потоке.'}</p></div><button className="button button-primary" onClick={() => onCreateTask(projectId || undefined)}><Plus size={18} />Новая задача</button></div>
    <div className="tasks-summary"><span><strong>{allTasks.length}</strong> всего задач</span><span className="tasks-summary-divider" /><span><span className="tasks-summary-dot" /><strong>{allTasks.filter(task => task.status === 'in_progress').length}</strong> в работе</span><span className="tasks-summary-divider" /><span><Check size={15} /><strong>{complete}</strong> завершено</span></div>
    <div className="tasks-toolbar"><div className="tasks-scope"><button className={scope === 'all' ? 'active' : ''} onClick={() => setScope('all')}>Все задачи <span>{allTasks.length}</span></button><button className={scope === 'mine' ? 'active' : ''} onClick={() => setScope('mine')}>Мои задачи <span>{allTasks.filter(task => task.assigneeIds.includes(workspace.user.id)).length}</span></button></div><div className="tasks-view-switch"><button aria-label="Показать доску" title="Доска" className={view === 'board' ? 'active' : ''} onClick={() => setView('board')}><LayoutGrid size={16} />Доска</button><button aria-label="Показать список" title="Список" className={view === 'list' ? 'active' : ''} onClick={() => setView('list')}><List size={17} />Список</button></div></div>
    <div className="tasks-filters"><label className="tasks-search"><Search size={16} /><input placeholder="Найти задачу…" aria-label="Поиск задач" value={localSearch} onChange={event => setLocalSearch(event.target.value)} /></label><div className="tasks-filter-select"><SlidersHorizontal size={15} /><select aria-label="Фильтр по статусу" value={statusFilter} onChange={event => setStatusFilter(event.target.value)}><option value="all">Все статусы</option>{statuses.map(status => <option key={status} value={status}>{statusLabels[status]}</option>)}</select></div><div className="tasks-filter-select"><select aria-label="Фильтр по исполнителю" value={assigneeFilter} onChange={event => setAssigneeFilter(event.target.value)}><option value="all">Все исполнители</option>{workspace.users.filter(user => user.active).map(user => <option key={user.id} value={user.id}>{user.name}</option>)}</select></div><span className="tasks-result-count">{filtered.length} задач</span></div>
    {error && <div className="feature-error" role="alert">{error}</div>}
    {view === 'board' ? <div className="tasks-board">{statuses.map(status => { const items = filtered.filter(task => task.status === status); return <section className={`tasks-column tasks-column-${status}`} key={status} onDragOver={event => event.preventDefault()} onDrop={event => { event.preventDefault(); const task = workspace.tasks.find(item => item.id === event.dataTransfer.getData('text/plain')); if (task) void moveTask(task, status); }}><header className="tasks-column-header"><div><span className="tasks-column-dot" /><h2>{statusLabels[status]}</h2><span className="tasks-column-count">{items.length}</span></div><button className="icon-button" aria-label={`Создать задачу в разделе ${statusLabels[status]}`} onClick={() => onCreateTask(projectId || undefined)}><Plus size={16} /></button></header><div className="tasks-column-content">{items.map(task => <TaskCard key={task.id} task={task} />)}{!items.length && <div className="tasks-column-empty"><CheckSquare size={24} /><span>Здесь пока нет задач</span></div>}<button className="tasks-add-card" onClick={() => onCreateTask(projectId || undefined)}><Plus size={15} />Добавить задачу</button></div></section>; })}</div> : <div className="card feature-table-wrap"><table className="feature-table tasks-table"><thead><tr><th>Задача</th><th>Исполнители</th><th>Статус</th><th>Срок</th><th>Прогресс</th></tr></thead><tbody>{filtered.map(task => <tr key={task.id}><td><button className="tasks-table-title" onClick={() => onOpenTask(task.id)}>{task.title}</button><span className="tasks-table-project">{workspace.projects.find(item => item.id === task.projectId)?.name || 'Без проекта'}</span></td><td><TaskAvatars task={task} /></td><td><select className={`tasks-table-status badge-status-${task.status}`} aria-label={`Статус задачи ${task.title}`} value={task.status} disabled={!!busyId || !canEditTask(workspace, task) || workspace.tasks.some(item => item.parentId === task.id)} title={workspace.tasks.some(item => item.parentId === task.id) ? 'Статус рассчитывается по подзадачам' : undefined} onChange={event => void moveTask(task, event.target.value as Task['status'])}>{statuses.map(status => <option key={status} value={status}>{statusLabels[status]}</option>)}</select></td><td className="muted">{task.dueDate ? shortDate(task.dueDate) : '—'}</td><td><div className="tasks-table-progress"><div className="tasks-progress"><span style={{ width: `${task.progress}%` }} /></div><span>{task.progress}%</span></div></td></tr>)}</tbody></table>{!filtered.length && <div className="empty-state"><Search size={30} /><h3>Задачи не найдены</h3><p>Измените поиск или фильтры.</p><button className="button button-secondary" onClick={() => { setLocalSearch(''); setScope('all'); setStatusFilter('all'); setAssigneeFilter('all'); }}>Сбросить фильтры</button></div>}</div>}
  </div>;
}
