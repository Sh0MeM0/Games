import { useCallback, useEffect, useState } from 'react';
import { Layers3, LoaderCircle, Plus, RefreshCw, X } from 'lucide-react';
import { api, ApiError, json } from './api';
import type { User, View, Workspace } from './types';
import Sidebar from './components/Sidebar';
import Header from './components/Header';
import Login from './components/Login';
import ProfileModal from './components/ProfileModal';
import TaskDetail from './components/TaskDetail';
import TaskForm from './components/TaskForm';
import Dashboard from './pages/Dashboard';
import Tasks from './pages/Tasks';
import Time from './pages/Time';
import Team from './pages/Team';
import './styles.css';
import './features.css';

export default function App() {
  useDialogAccessibility();
  const [workspace, setWorkspace] = useState<Workspace | null>(null);
  const [demoEnabled, setDemoEnabled] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [view, setView] = useState<View>('dashboard');
  const [search, setSearch] = useState('');
  const [projectId, setProjectId] = useState<string | null>(null);
  const [selectedTaskId, setSelectedTaskId] = useState<string | null>(null);
  const [taskForm, setTaskForm] = useState<{ projectId?: string } | null>(null);
  const [projectFormOpen, setProjectFormOpen] = useState(false);
  const [profileOpen, setProfileOpen] = useState(false);
  const [sidebarOpen, setSidebarOpen] = useState(false);

  const refresh = useCallback(async () => {
    try {
      const data = await api<Workspace>('/api/workspace');
      setWorkspace(data); setError('');
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        setWorkspace(null); setError(''); setView('dashboard'); setSearch(''); setProjectId(null); setSelectedTaskId(null); setTaskForm(null); setProjectFormOpen(false); setProfileOpen(false); setSidebarOpen(false);
      }
      throw error;
    }
  }, []);
  const initialize = useCallback(async () => {
    setLoading(true); setError('');
    try {
      const session = await api<{ user: User | null; demo: boolean }>('/api/session');
      setDemoEnabled(session.demo);
      if (session.user) await refresh(); else setWorkspace(null);
    } catch (error) { if (!(error instanceof ApiError && error.status === 401)) setError(error instanceof Error ? error.message : 'Не удалось подключиться к серверу'); }
    finally { setLoading(false); }
  }, [refresh]);
  useEffect(() => { void initialize(); }, [initialize]);
  useEffect(() => {
    if (!workspace) return;
    const timer = window.setInterval(() => { void refresh().catch(error => {
      if (error instanceof ApiError && error.status === 401) return;
      setError(error instanceof TypeError ? 'Нет соединения с сервером. Последние данные остаются на экране.' : `Не удалось обновить данные: ${error instanceof Error ? error.message : 'попробуйте ещё раз'}`);
    }); }, 30000);
    return () => window.clearInterval(timer);
  }, [!!workspace, refresh]);
  function navigate(next: View) { setView(next); setSidebarOpen(false); if (next !== 'tasks') {setSearch(''); setProjectId(null);} }
  function createTask(selectedProject?: string) { setTaskForm({projectId: selectedProject || projectId || undefined}); }
  async function logout() {
    try { await api('/api/auth/logout', json('POST', {})); setWorkspace(null); setView('dashboard'); setSelectedTaskId(null); setProfileOpen(false); setTaskForm(null); setSearch(''); setProjectId(null); }
    catch (error) { setError(error instanceof Error ? error.message : 'Не удалось выйти'); }
  }

  if (loading) return <div className="app-loading"><span className="loading-logo"><Layers3 size={31}/></span><h1>поток.</h1><div><LoaderCircle size={18} className="spin"/> Готовим рабочее пространство</div></div>;
  if (!workspace && error) return <div className="connection-error"><span className="loading-logo"><Layers3 size={31}/></span><h1>Не удалось открыть Поток</h1><p>{error}</p><button className="button button-primary" onClick={() => void initialize()}><RefreshCw size={16}/> Попробовать ещё раз</button></div>;
  if (!workspace) return <Login demoEnabled={demoEnabled} onLogin={refresh}/>;

  const shared = {workspace, onRefresh: refresh, onOpenTask: setSelectedTaskId, onCreateTask: createTask};
  return <div className="app-layout">
    <Sidebar workspace={workspace} view={view} projectId={projectId} open={sidebarOpen} onClose={() => setSidebarOpen(false)} onNavigate={navigate} onSelectProject={setProjectId} onCreateProject={() => setProjectFormOpen(true)} onProfile={() => setProfileOpen(true)} onLogout={() => void logout()}/>
    <div className="app-main"><Header workspace={workspace} view={view} projectId={projectId} search={search} onSearch={value => {setSearch(value); setView('tasks');}} onMenu={() => setSidebarOpen(true)} onCreateTask={() => createTask()} onOpenTask={setSelectedTaskId}/>{error && <div className="global-error" role="alert"><span>{error}</span><button className="text-button" onClick={() => void refresh().catch(error => setError(error.message))}>Повторить</button><button className="icon-button" aria-label="Закрыть сообщение" onClick={() => setError('')}><X size={16}/></button></div>}<main className="page-content">{view === 'dashboard' && <Dashboard {...shared} onNavigate={navigate} onSelectProject={setProjectId}/>} {view === 'tasks' && <Tasks {...shared} search={search} projectId={projectId}/>} {view === 'time' && <Time {...shared}/>} {view === 'team' && <Team {...shared}/>}</main></div>
    {selectedTaskId && <TaskDetail taskId={selectedTaskId} workspace={workspace} onClose={() => setSelectedTaskId(null)} onRefresh={refresh}/>}
    {taskForm && <TaskForm workspace={workspace} projectId={taskForm.projectId} onClose={() => setTaskForm(null)} onRefresh={refresh}/>}
    {projectFormOpen && <ProjectForm onClose={() => setProjectFormOpen(false)} onRefresh={refresh}/>}
    {profileOpen && <ProfileModal user={workspace.user} onClose={() => setProfileOpen(false)} onRefresh={refresh}/>}
  </div>;
}

function useDialogAccessibility() {
  useEffect(() => {
    let activeDialog: HTMLElement | null = null;
    let previousFocus: HTMLElement | null = null;
    let lastKnownFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    let openingFocus: HTMLElement | null = null;
    let animationFrame: number | null = null;
    const focusHistory = new WeakMap<HTMLElement, HTMLElement | null>();
    const originalOverflow = document.body.style.overflow;
    const focusable = (dialog: HTMLElement) => Array.from(dialog.querySelectorAll<HTMLElement>('button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), a[href], [tabindex]:not([tabindex="-1"])')).filter(element => element.getClientRects().length > 0);
    const updateDialog = () => {
      const dialogs = Array.from(document.querySelectorAll<HTMLElement>('[role="dialog"][aria-modal="true"]'));
      const next = dialogs.at(-1) || null;
      if (next === activeDialog) return;
      if (animationFrame !== null) window.cancelAnimationFrame(animationFrame);
      const previous = activeDialog;
      if (previous && !dialogs.includes(previous)) previousFocus = focusHistory.get(previous) || null;
      activeDialog = next;
      document.body.style.overflow = next ? 'hidden' : originalOverflow;
      if (next && !focusHistory.has(next)) focusHistory.set(next, openingFocus || lastKnownFocus);
      openingFocus = null;
      animationFrame = window.requestAnimationFrame(() => {
        animationFrame = null;
        if (next) {
          if (previousFocus?.isConnected && next.contains(previousFocus)) previousFocus.focus();
          if (!next.contains(document.activeElement)) (focusable(next)[0] || next).focus();
        } else if (previousFocus?.isConnected) previousFocus.focus();
        previousFocus = null;
      });
    };
    const handleFocus = (event: FocusEvent) => {
      if (!(event.target instanceof HTMLElement)) return;
      const dialog = event.target.closest<HTMLElement>('[role="dialog"][aria-modal="true"]');
      if (dialog && dialog !== activeDialog && !focusHistory.has(dialog)) openingFocus = lastKnownFocus;
      lastKnownFocus = event.target;
    };
    const handleKey = (event: KeyboardEvent) => {
      if (!activeDialog) return;
      if (event.key === 'Escape') {
        const close = activeDialog.querySelector<HTMLButtonElement>('button[aria-label^="Закрыть"]:not([disabled])');
        if (close) { event.preventDefault(); event.stopPropagation(); close.click(); }
        return;
      }
      if (event.key !== 'Tab') return;
      const elements = focusable(activeDialog);
      const first = elements[0]; const last = elements.at(-1);
      if (!first || !last) {event.preventDefault(); return;}
      if (event.shiftKey && (document.activeElement === first || !activeDialog.contains(document.activeElement))) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && (document.activeElement === last || !activeDialog.contains(document.activeElement))) {event.preventDefault(); first.focus();}
    };
    const observer = new MutationObserver(updateDialog);
    observer.observe(document.body, {childList: true, subtree: true, attributes: true, attributeFilter: ['role']});
    document.addEventListener('keydown', handleKey, true);
    document.addEventListener('focusin', handleFocus, true);
    updateDialog();
    return () => { observer.disconnect(); document.removeEventListener('keydown', handleKey, true); document.removeEventListener('focusin', handleFocus, true); document.body.style.overflow = originalOverflow; if (animationFrame !== null) window.cancelAnimationFrame(animationFrame); };
  }, []);
}

function ProjectForm({onClose, onRefresh}: {onClose: () => void; onRefresh: () => Promise<void>}) {
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [color, setColor] = useState('#7464e8');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  async function submit() { setLoading(true); setError(''); try {await api('/api/projects', json('POST', {name: name.trim(), description: description.trim(), color})); await onRefresh(); onClose();} catch (error) {setError(error instanceof Error ? error.message : 'Не удалось создать проект');} finally {setLoading(false);} }
  useEffect(() => { const handleKey = (event: KeyboardEvent) => {if (event.key === 'Escape' && !loading) onClose();}; window.addEventListener('keydown', handleKey); return () => window.removeEventListener('keydown', handleKey); }, [onClose, loading]);
  return <div className="modal-backdrop" onMouseDown={event => {if (event.target === event.currentTarget && !loading) onClose();}}><form className="modal" aria-modal="true" role="dialog" aria-labelledby="project-form-title" onSubmit={event => {event.preventDefault(); void submit();}}><div className="modal-header"><div><h2 id="project-form-title">Новый проект</h2><p className="muted">Объедините задачи вокруг одной цели</p></div><button type="button" className="icon-button" onClick={onClose} disabled={loading} aria-label="Закрыть"><X size={20}/></button></div><div className="modal-body"><label className="form-field"><span className="field-label">Название проекта</span><input className="input" value={name} onChange={event => setName(event.target.value)} placeholder="Например, запуск нового продукта" required maxLength={100} autoFocus disabled={loading}/></label><label className="form-field"><span className="field-label">Описание</span><textarea className="textarea" value={description} onChange={event => setDescription(event.target.value)} placeholder="Что вы хотите достичь?" rows={3} maxLength={1000} disabled={loading}/></label><div className="form-field"><span className="field-label">Цвет проекта</span><div className="color-swatches">{['#7464e8', '#4a9de0', '#48b596', '#e5a458', '#dd7ba0', '#8290a9'].map(value => <button key={value} type="button" aria-label={`Выбрать цвет ${value}`} aria-pressed={color === value} style={{background: value}} className={color === value ? 'selected' : ''} onClick={() => setColor(value)} disabled={loading}/>)}</div></div>{error && <div className="form-error" role="alert">{error}</div>}</div><div className="modal-footer"><button className="button button-secondary" type="button" disabled={loading} onClick={onClose}>Отмена</button><button className="button button-primary" type="submit" disabled={loading || !name.trim()}>{loading ? <LoaderCircle size={17} className="spin"/> : <Plus size={17}/>} Создать проект</button></div></form></div>;
}
