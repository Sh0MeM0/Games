import { useEffect, useState } from 'react';
import type { Action, ArchiveNode, Audit, EditStatus, User, Version } from '../shared/types';

const date = (value: string) => new Date(value).toLocaleString('ru-RU');
const size = (bytes: number) => bytes < 1024 * 1024 ? `${Math.ceil(bytes / 1024)} КБ` : `${(bytes / 1024 / 1024).toFixed(1)} МБ`;
const labels: Record<string, string> = {
  login: 'Вход', logout: 'Выход', login_failed: 'Неудачный вход', refresh_reuse: 'Повторный refresh-токен',
  upload: 'Загрузка', edit: 'Сохранение', download: 'Скачивание', delete: 'В корзину', restore: 'Восстановление',
  create_folder: 'Создание папки', rename: 'Переименование', checkout: 'Начало редактирования', release: 'Завершение редактирования',
  rollback: 'Откат версии', force_unlock: 'Снятие блокировки', create_user: 'Создание пользователя', block_user: 'Блокировка пользователя',
  activate_user: 'Разблокировка пользователя', reset_password: 'Сброс пароля', import_users: 'Импорт пользователей',
  create_admin_cli: 'Создание администратора', import_users_cli: 'Импорт из CSV',
};

export function App() {
  const [user, setUser] = useState<User | null>(null);
  const [server, setServer] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [tab, setTab] = useState<'files' | 'trash' | 'users' | 'audit'>('files');
  const [nodes, setNodes] = useState<ArchiveNode[]>([]);
  const [crumbs, setCrumbs] = useState<{ id: string; name: string }[]>([]);
  const [search, setSearch] = useState('');
  const [selected, setSelected] = useState<ArchiveNode | null>(null);
  const [edits, setEdits] = useState<EditStatus[]>([]);
  const [users, setUsers] = useState<User[]>([]);
  const [audit, setAudit] = useState<Audit[]>([]);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [busy, setBusy] = useState(false);
  const [history, setHistory] = useState<Version[] | null>(null);
  const [modal, setModal] = useState<'folder' | 'rename' | 'user' | 'password' | null>(null);
  const [name, setName] = useState('');
  const [newUsername, setNewUsername] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [newRole, setNewRole] = useState('user');
  const [targetUser, setTargetUser] = useState<User | null>(null);
  const parentId = crumbs.at(-1)?.id;

  async function invoke<T>(action: Action): Promise<T> {
    const result = await window.archive.action<T>(action);
    if (!result.ok) throw new Error(result.error);
    return result.value as T;
  }
  async function refresh() {
    if (!user) return;
    if (tab === 'files') {
      const result = await invoke<{ items: ArchiveNode[]; truncated: boolean }>({ type: 'list', parentId, search: search.trim() });
      setNodes(result.items); setSelected(previous => previous ? result.items.find(item => item.id === previous.id) || null : null);
      if (result.truncated) setNotice('Показаны первые 500 результатов. Уточните поиск.');
    } else if (tab === 'trash') setNodes(await invoke<ArchiveNode[]>({ type: 'trash' }));
    else if (tab === 'users') setUsers(await invoke<User[]>({ type: 'users' }));
    else setAudit(await invoke<Audit[]>({ type: 'audit' }));
  }
  async function perform(task: () => Promise<unknown>, message = '', refreshAfter = true) {
    setBusy(true); setError(''); setNotice('');
    try { await task(); if (message) setNotice(message); if (refreshAfter) await refresh(); }
    catch (caught) { setError(caught instanceof Error ? caught.message : 'Не удалось выполнить действие.'); }
    finally { setBusy(false); }
  }
  useEffect(() => {
    void window.archive.settings().then(result => { if (result.ok && result.value) { setServer(result.value.server); setUser(result.value.user); } });
    void window.archive.edits().then(result => { if (result.ok) setEdits(result.value || []); });
    return window.archive.onEdits(setEdits);
  }, []);
  useEffect(() => {
    if (!user) return;
    const run = () => void refresh().catch(caught => setError(caught instanceof Error ? caught.message : 'Сервер недоступен.'));
    run(); const timer = setInterval(run, 10_000);
    return () => clearInterval(timer);
  }, [user, tab, parentId, search]);

  async function login(event: React.FormEvent) {
    event.preventDefault(); setBusy(true); setError('');
    try {
      const result = await window.archive.login(server, username, password);
      if (!result.ok) throw new Error(result.error);
      setUser(result.value!); setPassword('');
    } catch (caught) { setError(caught instanceof Error ? caught.message : 'Ошибка входа.'); }
    finally { setBusy(false); }
  }
  async function openFolder(node: ArchiveNode) {
    const details = await invoke<ArchiveNode & { ancestors: { id: string; name: string }[] }>({ type: 'details', id: node.id });
    setCrumbs([...details.ancestors, { id: node.id, name: node.name }]); setSearch(''); setSelected(null);
  }
  function showModal(kind: typeof modal) { setName(kind === 'rename' ? selected?.name || '' : ''); setNewPassword(''); setNewUsername(''); setModal(kind); }
  async function submitModal(event: React.FormEvent) {
    event.preventDefault();
    await perform(async () => {
      if (modal === 'folder') await invoke({ type: 'folder', name, parentId });
      if (modal === 'rename' && selected) await invoke({ type: 'rename', id: selected.id, name });
      if (modal === 'user') await invoke({ type: 'user-create', username: newUsername, full_name: name, password: newPassword, role: newRole });
      if (modal === 'password' && targetUser) await invoke({ type: 'user-password', id: targetUser.id, password: newPassword });
      setNewPassword(''); setModal(null);
    }, 'Изменения сохранены.');
  }

  if (!user) return <main className="login"><form className="login-card" onSubmit={login}>
    <div className="brand">▤</div><h1>Общий файловый архив</h1><p>Документы вашей организации</p>
    <label>Адрес сервера<input type="url" placeholder="https://archive.example.org" required value={server} onChange={e => setServer(e.target.value)} /></label>
    <label>Логин<input autoComplete="username" required value={username} onChange={e => setUsername(e.target.value)} /></label>
    <label>Пароль<input type="password" autoComplete="current-password" required value={password} onChange={e => setPassword(e.target.value)} /></label>
    {error && <div className="alert error" role="alert">{error}</div>}
    <button className="primary" disabled={busy}>{busy ? 'Вход…' : 'Войти'}</button>
  </form></main>;

  return <div className="app"><aside><div className="logo">▤ <strong>Общий архив</strong></div>
    <nav>{(['files', 'trash', ...(user.role === 'admin' ? ['users', 'audit'] : [])] as typeof tab[]).map(value =>
      <button key={value} className={tab === value ? 'active' : ''} onClick={() => { setTab(value); setSelected(null); setHistory(null); }}>
        {{ files: 'Документы', trash: 'Корзина', users: 'Пользователи', audit: 'Журнал действий' }[value]}</button>)}</nav>
    <div className="profile"><strong>{user.full_name}</strong><small>{user.role === 'admin' ? 'Администратор' : 'Сотрудник'}</small>
      <button disabled={busy} onClick={() => void perform(async () => { const result = await window.archive.logout(); if (!result.ok) throw new Error(result.error); setUser(null); setNodes([]); setSelected(null); setCrumbs([]); }, '', false)}>Выйти</button>
      <button disabled={busy} onClick={() => { setUsername(user.username); setUser(null); setError(''); }}>Войти снова</button>
    </div></aside>
    <main className="workspace"><header><div><h1>{{ files: 'Общие документы', trash: 'Корзина', users: 'Пользователи', audit: 'Журнал действий' }[tab]}</h1>
      <p>{tab === 'files' ? 'Актуальные версии файлов для всей команды' : tab === 'trash' ? 'Удалённые документы можно восстановить' : 'Управление архивом'}</p></div>
      <button disabled={busy} onClick={() => void perform(refresh)}>Обновить</button></header>
      {error && <div className="alert error" role="alert">{error}<button onClick={() => setError('')}>×</button></div>}
      {notice && <div className="alert success" role="status">{notice}</div>}
      {tab === 'files' && <><div className="toolbar"><button disabled={busy || !!search.trim()} onClick={() => showModal('folder')}>+ Папка</button>
        <button className="primary" disabled={busy || !!search.trim()} onClick={() => void perform(() => invoke({ type: 'upload', parentId }), 'Загрузка завершена.')}>Загрузить файл</button>
        <input aria-label="Поиск по имени" className="search" placeholder="Поиск по имени файла…" value={search} onChange={e => setSearch(e.target.value)} /></div>
        <div className="breadcrumbs"><button onClick={() => { setCrumbs([]); setSearch(''); setSelected(null); }}>Все документы</button>
          {crumbs.map((crumb, index) => <span key={crumb.id}> / <button onClick={() => { setCrumbs(crumbs.slice(0, index + 1)); setSearch(''); setSelected(null); }}>{crumb.name}</button></span>)}</div>
        {selected && <div className="selection"><strong>{selected.name}</strong>
          {selected.kind === 'folder' ? <button disabled={busy} onClick={() => void perform(() => openFolder(selected))}>Открыть папку</button> : <>
            <button disabled={busy} onClick={() => void perform(() => invoke({ type: 'read', id: selected.id }))}>Для чтения</button>
            <button className="primary" disabled={busy || !!selected.lock || edits.some(e => e.id === selected.id)} onClick={() => void perform(() => invoke({ type: 'edit', id: selected.id }))}>Открыть для редактирования</button>
            <button disabled={busy} onClick={() => void perform(() => invoke({ type: 'download', id: selected.id, name: selected.name }))}>Скачать</button>
            <button disabled={busy} onClick={() => void perform(async () => setHistory(await invoke<Version[]>({ type: 'versions', id: selected.id })))}>История</button>
          </>}
          <button disabled={busy || !!selected.lock} onClick={() => showModal('rename')}>Переименовать</button>
          <button disabled={busy || !!selected.lock} onClick={() => { if (window.confirm(`Переместить «${selected.name}» в корзину?`)) void perform(async () => { await invoke({ type: 'delete', id: selected.id }); setSelected(null); }); }}>В корзину</button>
          {user.role === 'admin' && selected.lock && <button disabled={busy} onClick={() => { if (window.confirm('Принудительно снять блокировку? Несохранённые изменения сотрудника могут потребовать ручного восстановления.')) void perform(() => invoke({ type: 'force-unlock', id: selected.id })); }}>Снять блокировку</button>}
        </div>}
      </>}
      {(tab === 'files' || tab === 'trash') && <div className="table-wrap"><table><thead><tr><th>Название</th><th>Размер</th><th>Изменён</th><th>{tab === 'files' ? 'Состояние' : 'Действие'}</th></tr></thead>
        <tbody>{nodes.map(node => <tr key={node.id} className={selected?.id === node.id ? 'selected' : ''} onClick={() => { setSelected(node); setHistory(null); }}
          onDoubleClick={() => tab === 'files' && void perform(() => node.kind === 'folder' ? openFolder(node) : invoke({ type: 'read', id: node.id }))}>
          <td><span className={`file-icon ${node.kind}`}>{node.kind === 'folder' ? '▣' : /\.xls/i.test(node.name) ? 'X' : 'W'}</span><strong>{node.name}</strong></td>
          <td>{node.kind === 'folder' ? '—' : size(node.size)}</td><td>{date(node.updated_at)}</td>
          <td>{tab === 'trash' ? <button disabled={busy} onClick={() => void perform(() => invoke({ type: 'restore', id: node.id }), 'Восстановлено.')}>Восстановить</button>
            : node.lock ? <span className="badge locked">Редактирует: {node.lock.full_name}</span> : <span className="badge">{node.kind === 'folder' ? 'Папка' : `Версия ${node.version}`}</span>}</td></tr>)}</tbody></table>
        {!nodes.length && <div className="empty">{tab === 'trash' ? 'Корзина пуста' : 'Здесь пока нет документов'}</div>}</div>}
      {tab === 'users' && <><div className="toolbar"><button className="primary" onClick={() => showModal('user')}>Добавить пользователя</button>
        <button disabled={busy} onClick={() => void perform(async () => { const result = await invoke<{ created: number } | null>({ type: 'user-import' }); if (result) setNotice(`Создано пользователей: ${result.created}`); })}>Импорт CSV</button></div>
        <p className="hint">CSV: username,full_name,password,role. UTF-8; пароль от 12 символов. Импорт выполняется целиком.</p>
        <table><thead><tr><th>Сотрудник</th><th>Логин</th><th>Роль</th><th>Действия</th></tr></thead><tbody>{users.map(item => <tr key={item.id}><td>{item.full_name}</td><td>{item.username}</td>
          <td>{item.role === 'admin' ? 'Администратор' : 'Сотрудник'}{!item.active && ' · Заблокирован'}</td><td>
            <button disabled={busy || item.id === user.id} onClick={() => void perform(() => invoke({ type: 'user-active', id: item.id, active: !item.active }))}>{item.active ? 'Заблокировать' : 'Разблокировать'}</button>
            <button disabled={busy || item.id === user.id} onClick={() => { setTargetUser(item); showModal('password'); }}>Сбросить пароль</button></td></tr>)}</tbody></table></>}
      {tab === 'audit' && <><table><thead><tr><th>Время</th><th>Пользователь</th><th>Действие</th><th>Подробности</th></tr></thead>
        <tbody>{audit.map(item => <tr key={item.id}><td>{date(item.created_at)}</td><td>{item.user}</td><td>{labels[item.action] || item.action}</td><td>{item.detail}</td></tr>)}</tbody></table>
        {audit.length > 0 && <button disabled={busy} onClick={() => void perform(async () => { const more = await invoke<Audit[]>({ type: 'audit', before: audit.at(-1)!.id }); setAudit([...audit, ...more]); }, '', false)}>Показать более ранние записи</button>}</>}
      {edits.length > 0 && <section className="edits"><h2>Рабочие копии</h2><p className="hint">Сохраните и закройте документ в Word/Excel, затем нажмите «Завершить». Если связи нет, копия остаётся на компьютере.</p>
        {edits.map(edit => <article key={edit.id} className={['conflict', 'recovered', 'offline'].includes(edit.state) ? 'edit warning' : 'edit'}>
          <div><strong>{edit.name}</strong><p>{edit.message}</p></div><div className="edit-actions">
            <button disabled={busy || ['conflict', 'recovered'].includes(edit.state)} onClick={() => void perform(() => invoke({ type: 'finish', id: edit.id }), 'Редактирование завершено.')}>Завершить</button>
            <button disabled={busy} onClick={() => void perform(() => invoke({ type: 'copy-local', id: edit.id }))}>Сохранить копию</button>
            <button onClick={() => void perform(() => invoke({ type: 'show-local', id: edit.id }))}>Показать файл</button>
            <button disabled={busy} onClick={() => void perform(() => invoke({ type: 'discard', id: edit.id }))}>Прекратить</button>
          </div><label className="checkbox"><input type="checkbox" checked={edit.autoClose} disabled={busy || ['conflict', 'recovered'].includes(edit.state)}
            onChange={e => void perform(() => invoke({ type: 'auto-close', id: edit.id, enabled: e.target.checked }))} />Снимать блокировку по закрытию файла (проверка занятости Windows; может не сработать)</label>
        </article>)}</section>}
    </main>
    {history && selected && <div className="overlay"><section className="dialog history"><h2>История: {selected.name}</h2><p>Откат создаёт новую версию; прежние версии сохраняются.</p>
      <table><thead><tr><th>Версия</th><th>Кто и когда</th><th>Действия</th></tr></thead><tbody>{history.map(item => <tr key={item.number}><td>№ {item.number}<small>{item.note}</small></td><td>{item.author}<small>{date(item.created_at)}</small></td><td>
        <button disabled={busy} onClick={() => void perform(() => invoke({ type: 'download', id: selected.id, name: selected.name, version: item.number }))}>Скачать</button>
        <button disabled={busy} onClick={() => { if (window.confirm(`Откатить документ к версии ${item.number}?`)) void perform(async () => { await invoke({ type: 'rollback', id: selected.id, version: item.number }); setHistory(await invoke<Version[]>({ type: 'versions', id: selected.id })); }); }}>Откатить</button>
      </td></tr>)}</tbody></table><button onClick={() => setHistory(null)}>Закрыть</button></section></div>}
    {modal && <div className="overlay"><form className="dialog" onSubmit={submitModal}><h2>{{ folder: 'Новая папка', rename: 'Переименование', user: 'Новый пользователь', password: 'Новый пароль' }[modal]}</h2>
      {modal !== 'password' && <label>{modal === 'user' ? 'ФИО' : 'Название'}<input required maxLength={modal === 'user' ? 160 : 180} value={name} onChange={e => setName(e.target.value)} autoFocus /></label>}
      {modal === 'user' && <><label>Логин<input required pattern="[A-Za-z0-9_.\-]{3,64}" value={newUsername} onChange={e => setNewUsername(e.target.value)} /></label>
        <label>Роль<select value={newRole} onChange={e => setNewRole(e.target.value)}><option value="user">Сотрудник</option><option value="admin">Администратор</option></select></label></>}
      {(modal === 'user' || modal === 'password') && <label>Пароль (минимум 12 символов)<input type="password" required minLength={12} maxLength={256} autoComplete="new-password" value={newPassword} onChange={e => setNewPassword(e.target.value)} /></label>}
      {error && <div className="alert error">{error}</div>}<div className="dialog-actions"><button type="button" onClick={() => { setModal(null); setNewPassword(''); }}>Отмена</button><button className="primary" disabled={busy}>Сохранить</button></div>
    </form></div>}
  </div>;
}
