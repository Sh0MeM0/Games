import { Bell, CalendarDays, ChevronRight, Menu, Plus, Search, X } from 'lucide-react';
import { useState } from 'react';
import type { View, Workspace } from '../types';

interface Props { workspace: Workspace; view: View; projectId: string | null; search: string; onSearch: (value: string) => void; onMenu: () => void; onCreateTask: () => void; onOpenTask: (id: string) => void; }
const titles: Record<View, string> = { dashboard: 'Обзор', tasks: 'Задачи', time: 'Учёт времени', team: 'Команда' };

export default function Header({ workspace, view, projectId, search, onSearch, onMenu, onCreateTask, onOpenTask }: Props) {
  const [notificationsOpen, setNotificationsOpen] = useState(false);
  const project = workspace.projects.find(item => item.id === projectId);
  const today = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'long', timeZone: 'Europe/Moscow' }).format(new Date());
  return <header className="app-header">
    <div className="header-breadcrumb"><button className="mobile-menu icon-button" onClick={onMenu} aria-label="Открыть меню"><Menu size={21}/></button><span className="breadcrumb-workspace">Рабочее пространство</span><ChevronRight size={14} className="breadcrumb-separator"/><strong>{project?.name || titles[view]}</strong></div>
    <div className="header-actions"><label className="header-search"><Search size={17}/><input aria-label="Поиск задач" placeholder="Поиск задач…" value={search} onChange={event => onSearch(event.target.value)}/>{search && <button onClick={() => onSearch('')} aria-label="Очистить поиск"><X size={14}/></button>}<kbd>⌕</kbd></label><span className="header-date"><CalendarDays size={16}/>{today}</span><div className="notifications-container"><button className={`notification-button icon-button ${notificationsOpen ? 'active' : ''}`} title="Активность команды" aria-label="Активность команды" onClick={() => setNotificationsOpen(!notificationsOpen)}><Bell size={20}/><span className="notification-dot"/></button>{notificationsOpen && <><button className="popover-dismiss" aria-label="Закрыть уведомления" onClick={() => setNotificationsOpen(false)}/><div className="notifications-popover"><div className="card-header"><strong>Активность команды</strong><button className="icon-button" onClick={() => setNotificationsOpen(false)} aria-label="Закрыть"><X size={17}/></button></div>{workspace.activities.slice(0, 5).map(activity => <button disabled={!activity.taskId} className="notification-row" key={activity.id} onClick={() => {if (activity.taskId) onOpenTask(activity.taskId); setNotificationsOpen(false);}}><strong>{workspace.users.find(user => user.id === activity.userId)?.name || 'Участник'}</strong><span>{activity.text}</span></button>)}{workspace.activities.length === 0 && <p className="muted">Уведомлений пока нет</p>}</div></>}</div><button className="button button-primary header-create" onClick={onCreateTask}><Plus size={17}/><span>Создать задачу</span></button></div>
  </header>;
}
