import { CheckCheck, ChevronDown, ChevronRight, Clock3, Layers3, LayoutDashboard, LogOut, Plus, Users, X } from 'lucide-react';
import type { View, Workspace } from '../types';

interface Props {
  workspace: Workspace;
  view: View;
  projectId: string | null;
  open: boolean;
  onClose: () => void;
  onNavigate: (view: View) => void;
  onSelectProject: (id: string | null) => void;
  onCreateProject: () => void;
  onProfile: () => void;
  onLogout: () => void;
}

const navigation = [
  { id: 'dashboard', title: 'Обзор', icon: LayoutDashboard },
  { id: 'tasks', title: 'Задачи', icon: CheckCheck },
  { id: 'time', title: 'Учёт времени', icon: Clock3 },
  { id: 'team', title: 'Команда', icon: Users },
] as const;

export default function Sidebar({ workspace, view, projectId, open, onClose, onNavigate, onSelectProject, onCreateProject, onProfile, onLogout }: Props) {
  const initials = workspace.user.name.split(' ').slice(0, 2).map(word => word[0]).join('');
  const activeTasks = workspace.tasks.filter(task => task.status !== 'done' && task.assigneeIds.includes(workspace.user.id)).length;
  return <>
    {open && <button className="sidebar-scrim" aria-label="Закрыть меню" onClick={onClose} />}
    <aside className={`sidebar ${open ? 'sidebar-open' : ''}`} role={open ? 'dialog' : undefined} aria-modal={open ? true : undefined} aria-label={open ? 'Навигация по пространству' : undefined}>
      <div className="brand"><span className="brand-mark"><Layers3 size={25} strokeWidth={2.3} /></span><span>поток<span className="brand-dot">.</span></span><button className="sidebar-close icon-button" onClick={onClose} aria-label="Закрыть меню"><X size={20}/></button></div>
      <div className="workspace-switch"><span className="workspace-icon">К</span><div><strong>Моя команда</strong><small>Рабочее пространство</small></div><ChevronDown size={15} /></div>
      <div className="nav-section-label">РАБОЧЕЕ ПРОСТРАНСТВО</div>
      <nav className="main-nav" aria-label="Главное меню">{navigation.map(item => <button key={item.id} className={`nav-link ${view === item.id && !projectId ? 'nav-link-active' : ''}`} onClick={() => { onSelectProject(null); onNavigate(item.id); onClose(); }}><item.icon size={19} /><span>{item.title}</span>{item.id === 'tasks' && activeTasks > 0 && <span className="nav-count">{activeTasks}</span>}</button>)}</nav>
      <div className="nav-section-label projects-label"><span>ПРОЕКТЫ</span>{workspace.user.role === 'admin' && <button onClick={onCreateProject} className="sidebar-add" aria-label="Создать проект"><Plus size={16}/></button>}</div>
      <nav className="project-nav" aria-label="Проекты">{workspace.projects.map(project => <button key={project.id} className={`project-link ${projectId === project.id ? 'project-link-active' : ''}`} onClick={() => { onSelectProject(project.id); onNavigate('tasks'); onClose(); }}><span className="project-dot" style={{ background: project.color }}/><span>{project.name}</span></button>)}</nav>
      <div className="sidebar-bottom">
        <div className="workspace-plan"><span className="plan-dot"/><span>Всё нужное — бесплатно</span><span className="plan-users">{workspace.users.filter(user => user.active).length} чел.</span></div>
        <button className="sidebar-profile" onClick={onProfile}><span className="avatar" style={{background: workspace.user.avatarColor}}>{initials}</span><span className="profile-info"><strong>{workspace.user.name}</strong><small>{workspace.user.role === 'admin' ? 'Администратор' : 'Участник команды'}</small></span><ChevronRight size={16}/></button>
        <button className="logout-button" onClick={onLogout}><LogOut size={14}/> Выйти из аккаунта</button>
      </div>
    </aside>
  </>;
}
