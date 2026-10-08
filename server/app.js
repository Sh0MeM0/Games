import express from 'express';
import { randomUUID } from 'node:crypto';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { existsSync } from 'node:fs';
import { openDatabase, transaction, publicUser, getTask, allTasks, recalculateAncestors } from './db.js';
import { hashPassword, verifyPassword, newToken, tokenHash, parseCookies } from './security.js';
import { resolveTimezone, dateInTimezone, localNoonInstant } from './timezone.js';

const COOKIE = 'potok_session';
const SESSION_MS = 7 * 24 * 60 * 60 * 1000;
const STATUS = ['todo', 'in_progress', 'review', 'done'];
const PRIORITY = ['low', 'medium', 'high'];
const own = (object, field) => Object.hasOwn(object, field);
const now = () => new Date().toISOString();

class ApiError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}
const fail = (status, message) => { throw new ApiError(status, message); };
function text(value, name, max, { empty = false } = {}) {
  if (typeof value !== 'string') fail(400, `${name}: ожидается текст.`);
  const cleaned = value.trim();
  if ((!empty && !cleaned) || cleaned.length > max) fail(400, `${name}: укажите ${empty ? 'не более' : 'от 1 до'} ${max} символов.`);
  return cleaned;
}
function choice(value, allowed, name) {
  if (!allowed.includes(value)) fail(400, `${name}: недопустимое значение.`);
  return value;
}
function password(value) {
  if (typeof value !== 'string' || value.length < 12 || value.length > 128) fail(400, 'Пароль должен содержать от 12 до 128 символов.');
  return value;
}
function email(value) {
  const result = text(value, 'Email', 254).toLowerCase();
  if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(result)) fail(400, 'Укажите корректный email.');
  return result;
}
function dateOnly(value, name = 'Дата') {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value)) fail(400, `${name}: используйте формат ГГГГ-ММ-ДД.`);
  const date = new Date(`${value}T00:00:00.000Z`);
  if (!Number.isFinite(date.getTime()) || date.toISOString().slice(0, 10) !== value) fail(400, `${name}: некорректная дата.`);
  return value;
}
function body(req) {
  if (!req.body || typeof req.body !== 'object' || Array.isArray(req.body)) fail(400, 'Ожидается JSON-объект.');
  return req.body;
}
const asyncRoute = (handler) => (req, res, next) => { try { Promise.resolve(handler(req, res)).catch(next); } catch (error) { next(error); } };

export function createApp({
  dbPath = ':memory:', demo = process.env.NODE_ENV !== 'production', adminPassword,
  seed = true, adminEmail = 'admin@potok.local', secureCookies = process.env.NODE_ENV === 'production',
  trustProxy = false, staticDir = resolve(dirname(fileURLToPath(import.meta.url)), '../dist'),
} = {}) {
  const db = openDatabase({ dbPath, demo, adminPassword, seed, adminEmail: email(adminEmail) });
  const app = express();
  app.locals.db = db;
  app.locals.demo = demo;
  app.disable('x-powered-by');
  app.set('trust proxy', trustProxy);
  app.use((req, res, next) => {
    res.set({ 'X-Content-Type-Options': 'nosniff', 'Referrer-Policy': 'same-origin', 'X-Frame-Options': 'DENY' });
    if (req.path.startsWith('/api/')) res.set('Cache-Control', 'no-store');
    next();
  });
  app.use('/api', (req, res, next) => {
    if (!['GET', 'HEAD', 'OPTIONS'].includes(req.method)) {
      if (req.get('sec-fetch-site') === 'cross-site') return next(new ApiError(403, 'Запрос с другого сайта запрещён.'));
      const origin = req.get('origin');
      if (origin) {
        try {
          const parsed = new URL(origin);
          if (parsed.host !== req.get('host') || parsed.protocol !== `${req.protocol}:` || parsed.origin === 'null') {
            return next(new ApiError(403, 'Запрос с другого сайта запрещён.'));
          }
        } catch { return next(new ApiError(403, 'Некорректный источник запроса.')); }
      }
    }
    next();
  });
  app.use(express.json({ limit: '100kb' }));
  app.use('/api', (req, res, next) => {
    const token = parseCookies(req.get('cookie'))[COOKIE];
    if (token && /^[A-Za-z0-9_-]{43}$/.test(token)) {
      const session = db.prepare(`SELECT users.* FROM sessions JOIN users ON users.id = sessions.userId
        WHERE sessions.tokenHash = ? AND sessions.expiresAt > ? AND users.active = 1`).get(tokenHash(token), Date.now());
      if (session) req.user = publicUser(session);
    }
    next();
  });
  const requireAuth = (req, res, next) => req.user ? next() : next(new ApiError(401, 'Войдите в пространство.'));
  const requireAdmin = (req, res, next) => req.user?.role === 'admin' ? next() : next(new ApiError(403, 'Действие доступно администратору.'));
  const cookieOptions = { httpOnly: true, secure: Boolean(secureCookies), sameSite: 'lax', path: '/' };
  const startSession = (res, userId) => {
    const token = newToken();
    db.prepare('DELETE FROM sessions WHERE expiresAt <= ?').run(Date.now());
    db.prepare('INSERT INTO sessions (tokenHash,userId,expiresAt) VALUES (?,?,?)').run(tokenHash(token), userId, Date.now() + SESSION_MS);
    res.cookie(COOKIE, token, { ...cookieOptions, maxAge: SESSION_MS });
  };
  const activity = (userId, taskId, message) => db.prepare('INSERT INTO activities (id,userId,taskId,text,createdAt) VALUES (?,?,?,?,?)')
    .run(randomUUID(), userId, taskId, message, now());
  const taskOr404 = (id) => getTask(db, id) || fail(404, 'Задача не найдена.');
  const canEditTask = (user, task) => user.role === 'admin' || task.createdBy === user.id || task.assigneeIds.includes(user.id);
  const assertEdit = (user, task) => { if (!canEditTask(user, task)) fail(403, 'Изменять задачу может её автор, исполнитель или администратор.'); };
  const projectRef = (value) => {
    if (value === null || value === '') return null;
    const id = text(value, 'Проект', 100);
    if (!db.prepare('SELECT id FROM projects WHERE id = ?').get(id)) fail(400, 'Указанный проект не найден.');
    return id;
  };
  const assigneeRefs = (value) => {
    if (!Array.isArray(value) || value.length > 100 || value.some((id) => typeof id !== 'string')) fail(400, 'Исполнители: укажите список участников.');
    const ids = [...new Set(value)];
    for (const id of ids) {
      if (!db.prepare('SELECT id FROM users WHERE id = ? AND active = 1').get(id)) fail(400, 'Исполнителем может быть только активный участник.');
    }
    return ids;
  };
  const replaceAssignees = (taskId, ids) => {
    db.prepare('DELETE FROM task_assignees WHERE taskId = ?').run(taskId);
    const statement = db.prepare('INSERT INTO task_assignees (taskId,userId) VALUES (?,?)');
    ids.forEach((id) => statement.run(taskId, id));
  };
  const validateParent = (parentId, taskId, user) => {
    if (parentId === null || parentId === '') return null;
    const id = text(parentId, 'Родительская задача', 100);
    assertEdit(user, taskOr404(id));
    let ancestor = id;
    const seen = new Set();
    let depth = 0;
    while (ancestor) {
      if (ancestor === taskId || seen.has(ancestor)) fail(400, 'Задачи не могут образовывать цикл.');
      seen.add(ancestor);
      if (++depth >= 20) fail(400, 'Максимальная вложенность задач — 20 уровней.');
      ancestor = db.prepare('SELECT parentId FROM tasks WHERE id = ?').get(ancestor)?.parentId;
    }
    if (taskId) {
      const subtree = db.prepare(`WITH RECURSIVE descendants(id,depth) AS (SELECT id,1 FROM tasks WHERE id=? UNION ALL
        SELECT tasks.id, descendants.depth+1 FROM tasks JOIN descendants ON tasks.parentId=descendants.id)
        SELECT MAX(depth) AS height FROM descendants`).get(taskId);
      if (depth + (subtree.height || 1) > 20) fail(400, 'Перемещение превысит максимальную вложенность задач — 20 уровней.');
    }
    return id;
  };
  const validatedTask = (payload, existing, user) => {
    const task = existing ? { ...existing } : {
      title: '', description: '', projectId: null, parentId: null, assigneeIds: [], status: 'todo', priority: 'medium', progress: 0, dueDate: null,
    };
    if (!existing || own(payload, 'title')) task.title = text(payload.title, 'Название задачи', 200);
    if (own(payload, 'description')) task.description = text(payload.description, 'Описание', 10000, { empty: true });
    if (own(payload, 'projectId')) task.projectId = projectRef(payload.projectId);
    if (own(payload, 'parentId')) task.parentId = validateParent(payload.parentId, existing?.id, user);
    if (own(payload, 'assigneeIds')) task.assigneeIds = assigneeRefs(payload.assigneeIds);
    if (own(payload, 'status')) task.status = choice(payload.status, STATUS, 'Статус');
    if (own(payload, 'priority')) task.priority = choice(payload.priority, PRIORITY, 'Приоритет');
    if (own(payload, 'progress')) {
      if (typeof payload.progress !== 'number' || !Number.isFinite(payload.progress) || payload.progress < 0 || payload.progress > 100) fail(400, 'Прогресс должен быть числом от 0 до 100.');
      task.progress = Math.round(payload.progress);
    }
    if (own(payload, 'dueDate')) task.dueDate = payload.dueDate === null || payload.dueDate === '' ? null : dateOnly(payload.dueDate, 'Срок');
    if (task.parentId && !own(payload, 'projectId') && !existing) task.projectId = taskOr404(task.parentId).projectId;
    if (task.status === 'done') task.progress = 100;
    else if (existing?.status === 'done' && own(payload, 'status') && !own(payload, 'progress')) task.progress = 0;
    return task;
  };

  app.get('/api/health', (req, res) => res.json({ ok: true }));
  app.get('/api/session', (req, res) => res.json({ user: req.user || null, demo: Boolean(demo) }));

  const loginAttempts = new Map();
  const dummyHash = hashPassword(newToken());
  const loginGuard = (req, res, next) => {
    const key = req.ip;
    const current = loginAttempts.get(key);
    if (current && current.until > Date.now() && current.count >= 12) {
      res.set('Retry-After', String(Math.ceil((current.until - Date.now()) / 1000)));
      return next(new ApiError(429, 'Слишком много попыток входа. Попробуйте через 15 минут.'));
    }
    if (loginAttempts.size > 10000) for (const [ip, entry] of loginAttempts) if (entry.until <= Date.now()) loginAttempts.delete(ip);
    loginAttempts.set(key, { count: current && current.until > Date.now() ? current.count + 1 : 1, until: current && current.until > Date.now() ? current.until : Date.now() + 15 * 60000 });
    next();
  };
  app.post('/api/auth/login', loginGuard, asyncRoute((req, res) => {
    const payload = body(req);
    const loginEmail = email(payload.email);
    if (typeof payload.password !== 'string' || payload.password.length > 128) fail(400, 'Укажите пароль.');
    const user = db.prepare('SELECT * FROM users WHERE email = ? COLLATE NOCASE').get(loginEmail);
    const valid = verifyPassword(payload.password, user?.passwordHash || dummyHash);
    if (!user?.active || !valid) fail(401, 'Неверный email или пароль.');
    loginAttempts.delete(req.ip);
    startSession(res, user.id);
    res.json({ user: publicUser(user) });
  }));
  app.post('/api/auth/demo', loginGuard, asyncRoute((req, res) => {
    if (!demo) fail(404, 'Демонстрационный вход отключён.');
    const user = db.prepare("SELECT * FROM users WHERE id = 'user-admin' AND active = 1 AND role = 'admin'").get();
    if (!user) fail(409, 'Демонстрационный аккаунт недоступен.');
    startSession(res, user.id);
    res.json({ user: publicUser(user) });
  }));
  app.post('/api/auth/logout', asyncRoute((req, res) => {
    const token = parseCookies(req.get('cookie'))[COOKIE];
    if (token) db.prepare('DELETE FROM sessions WHERE tokenHash = ?').run(tokenHash(token));
    res.clearCookie(COOKIE, cookieOptions);
    res.json({ ok: true });
  }));

  app.use('/api', requireAuth);
  app.get('/api/workspace', (req, res) => res.json({
    user: req.user,
    users: db.prepare('SELECT * FROM users ORDER BY role,name').all().map(publicUser),
    projects: db.prepare('SELECT * FROM projects ORDER BY rowid').all(),
    tasks: allTasks(db),
    timeEntries: req.user.role === 'admin'
      ? db.prepare('SELECT * FROM time_entries ORDER BY startAt DESC').all()
      : db.prepare('SELECT * FROM time_entries WHERE userId = ? ORDER BY startAt DESC').all(req.user.id),
    runningTimer: db.prepare('SELECT * FROM timers WHERE userId = ?').get(req.user.id) || null,
    activities: db.prepare('SELECT * FROM activities ORDER BY createdAt DESC LIMIT 100').all(),
  }));
  app.post('/api/tasks', asyncRoute((req, res) => {
    const task = validatedTask(body(req), null, req.user);
    task.id = randomUUID();
    transaction(db, () => {
      db.prepare(`INSERT INTO tasks (id,title,description,projectId,parentId,status,priority,progress,dueDate,createdBy,createdAt,updatedAt)
        VALUES (?,?,?,?,?,?,?,?,?,?,?,?)`).run(task.id, task.title, task.description, task.projectId, task.parentId, task.status, task.priority, task.progress, task.dueDate, req.user.id, now(), now());
      replaceAssignees(task.id, task.assigneeIds);
      recalculateAncestors(db, task.parentId);
      activity(req.user.id, task.id, `создал(а) ${task.parentId ? 'подзадачу' : 'задачу'} «${task.title}»`);
    });
    res.status(201).json({ task: getTask(db, task.id) });
  }));
  app.patch('/api/tasks/:id', asyncRoute((req, res) => {
    const previous = taskOr404(req.params.id);
    assertEdit(req.user, previous);
    const task = validatedTask(body(req), previous, req.user);
    transaction(db, () => {
      db.prepare(`UPDATE tasks SET title=?,description=?,projectId=?,parentId=?,status=?,priority=?,progress=?,dueDate=?,updatedAt=? WHERE id=?`)
        .run(task.title, task.description, task.projectId, task.parentId, task.status, task.priority, task.progress, task.dueDate, now(), task.id);
      replaceAssignees(task.id, task.assigneeIds);
      recalculateAncestors(db, task.id);
      if (previous.parentId !== task.parentId) recalculateAncestors(db, previous.parentId);
      activity(req.user.id, task.id, `обновил(а) задачу «${task.title}»`);
    });
    res.json({ task: getTask(db, task.id) });
  }));
  app.delete('/api/tasks/:id', asyncRoute((req, res) => {
    const task = taskOr404(req.params.id);
    const descendants = db.prepare(`WITH RECURSIVE subtree(id) AS (SELECT id FROM tasks WHERE id=? UNION ALL
      SELECT tasks.id FROM tasks JOIN subtree ON tasks.parentId=subtree.id) SELECT id FROM subtree`).all(task.id);
    descendants.forEach((child) => assertEdit(req.user, taskOr404(child.id)));
    for (const child of descendants) {
      if (db.prepare('SELECT id FROM timers WHERE taskId=? LIMIT 1').get(child.id)) fail(409, 'В задаче или подзадаче работает таймер. Сначала остановите таймер.');
      if (db.prepare('SELECT id FROM time_entries WHERE taskId=? LIMIT 1').get(child.id)) fail(409, 'У задачи или подзадачи есть учтённое время. Завершите задачу, чтобы сохранить историю и отчёты.');
    }
    transaction(db, () => {
      activity(req.user.id, null, `удалил(а) задачу «${task.title}»`);
      db.prepare('DELETE FROM tasks WHERE id = ?').run(task.id);
      recalculateAncestors(db, task.parentId);
    });
    res.json({ ok: true });
  }));
  app.post('/api/projects', asyncRoute((req, res) => {
    const payload = body(req);
    const color = payload.color === undefined ? '#7864D9' : text(payload.color, 'Цвет', 7);
    if (!/^#[\da-fA-F]{6}$/.test(color)) fail(400, 'Цвет должен быть в формате #RRGGBB.');
    const project = { id: randomUUID(), name: text(payload.name, 'Название проекта', 100), color,
      description: payload.description === undefined ? '' : text(payload.description, 'Описание', 2000, { empty: true }) };
    transaction(db, () => {
      db.prepare('INSERT INTO projects (id,name,color,description) VALUES (?,?,?,?)').run(project.id, project.name, project.color, project.description);
      activity(req.user.id, null, `создал(а) проект «${project.name}»`);
    });
    res.status(201).json({ project });
  }));

  app.post('/api/users', requireAdmin, asyncRoute((req, res) => {
    const payload = body(req);
    const user = { id: randomUUID(), name: text(payload.name, 'Имя', 100), email: email(payload.email),
      role: choice(payload.role || 'member', ['admin', 'member'], 'Роль'), title: text(payload.title ?? '', 'Должность', 120, { empty: true }), active: true,
      avatarColor: ['#7765D5', '#D68E57', '#599A85', '#7092C4', '#C16D89'][Math.floor(Math.random() * 5)] };
    const passwordHash = hashPassword(password(payload.password));
    if (db.prepare('SELECT id FROM users WHERE email = ?').get(user.email)) fail(409, 'Участник с таким email уже существует.');
    transaction(db, () => {
      db.prepare('INSERT INTO users (id,name,email,role,avatarColor,title,passwordHash) VALUES (?,?,?,?,?,?,?)')
        .run(user.id, user.name, user.email, user.role, user.avatarColor, user.title, passwordHash);
      activity(req.user.id, null, `добавил(а) участника ${user.name}`);
    });
    res.status(201).json({ user });
  }));
  const finishTimer = (timer, note) => {
    const endedAt = now();
    const entry = { id: randomUUID(), userId: timer.userId, taskId: timer.taskId, startAt: timer.startedAt, endAt: endedAt,
      durationSeconds: Math.max(0, Math.floor((Date.parse(endedAt) - Date.parse(timer.startedAt)) / 1000)), note };
    db.prepare('INSERT INTO time_entries (id,userId,taskId,startAt,endAt,durationSeconds,note) VALUES (?,?,?,?,?,?,?)')
      .run(entry.id, entry.userId, entry.taskId, entry.startAt, entry.endAt, entry.durationSeconds, entry.note);
    db.prepare('DELETE FROM timers WHERE id=?').run(timer.id);
    return entry;
  };
  app.patch('/api/users/:id', requireAdmin, asyncRoute((req, res) => {
    const previous = db.prepare('SELECT * FROM users WHERE id=?').get(req.params.id);
    if (!previous) fail(404, 'Участник не найден.');
    const payload = body(req);
    const user = { ...previous };
    if (own(payload, 'name')) user.name = text(payload.name, 'Имя', 100);
    if (own(payload, 'title')) user.title = text(payload.title, 'Должность', 120, { empty: true });
    if (own(payload, 'role')) user.role = choice(payload.role, ['admin', 'member'], 'Роль');
    if (own(payload, 'active')) {
      if (typeof payload.active !== 'boolean') fail(400, 'Активность должна быть логическим значением.');
      user.active = Number(payload.active);
    }
    if (own(payload, 'password')) user.passwordHash = hashPassword(password(payload.password));
    if (previous.active && previous.role === 'admin' && (!user.active || user.role !== 'admin') &&
      db.prepare("SELECT COUNT(*) AS count FROM users WHERE active=1 AND role='admin'").get().count <= 1) fail(409, 'В пространстве должен оставаться активный администратор.');
    transaction(db, () => {
      db.prepare('UPDATE users SET name=?,title=?,role=?,active=?,passwordHash=? WHERE id=?')
        .run(user.name, user.title, user.role, user.active, user.passwordHash, user.id);
      if (!user.active || own(payload, 'password')) db.prepare('DELETE FROM sessions WHERE userId=?').run(user.id);
      if (!user.active) {
        const timer = db.prepare('SELECT * FROM timers WHERE userId=?').get(user.id);
        if (timer) finishTimer(timer, 'Таймер остановлен при отключении участника');
      }
      activity(req.user.id, null, `изменил(а) профиль участника ${user.name}`);
    });
    res.json({ user: publicUser(user) });
  }));
  app.patch('/api/profile', asyncRoute((req, res) => {
    const payload = body(req);
    const user = db.prepare('SELECT * FROM users WHERE id=?').get(req.user.id);
    if (own(payload, 'name')) user.name = text(payload.name, 'Имя', 100);
    if (own(payload, 'title')) user.title = text(payload.title, 'Должность', 120, { empty: true });
    if (own(payload, 'newPassword') && payload.newPassword) {
      if (typeof payload.currentPassword !== 'string' || payload.currentPassword.length > 128 || !verifyPassword(payload.currentPassword, user.passwordHash)) fail(403, 'Текущий пароль неверен.');
      user.passwordHash = hashPassword(password(payload.newPassword));
    }
    transaction(db, () => {
      db.prepare('UPDATE users SET name=?,title=?,passwordHash=? WHERE id=?').run(user.name, user.title, user.passwordHash, user.id);
      if (own(payload, 'newPassword') && payload.newPassword) {
        db.prepare('DELETE FROM sessions WHERE userId=?').run(user.id);
        startSession(res, user.id);
      }
    });
    res.json({ user: publicUser(user) });
  }));

  const assertTrackable = (user, taskId) => { const task = taskOr404(text(taskId, 'Задача', 100)); assertEdit(user, task); return task; };
  app.post('/api/timer/start', asyncRoute((req, res) => {
    const task = assertTrackable(req.user, body(req).taskId);
    if (db.prepare('SELECT id FROM timers WHERE userId=?').get(req.user.id)) fail(409, 'Сначала остановите текущий таймер.');
    const runningTimer = { id: randomUUID(), userId: req.user.id, taskId: task.id, startedAt: now() };
    transaction(db, () => {
      db.prepare('INSERT INTO timers (id,userId,taskId,startedAt) VALUES (?,?,?,?)').run(runningTimer.id, runningTimer.userId, runningTimer.taskId, runningTimer.startedAt);
      activity(req.user.id, task.id, `начал(а) работу над задачей «${task.title}»`);
    });
    res.status(201).json({ runningTimer });
  }));
  app.post('/api/timer/stop', asyncRoute((req, res) => {
    const payload = body(req);
    const note = text(payload.note ?? '', 'Комментарий', 1000, { empty: true });
    const timer = db.prepare('SELECT * FROM timers WHERE userId=?').get(req.user.id);
    if (!timer) fail(409, 'Активный таймер не найден.');
    const timeEntry = transaction(db, () => {
      const entry = finishTimer(timer, note);
      activity(req.user.id, timer.taskId, 'завершил(а) сеанс работы по таймеру');
      return entry;
    });
    res.json({ timeEntry });
  }));
  app.post('/api/time-entries', asyncRoute((req, res) => {
    const payload = body(req);
    const task = assertTrackable(req.user, payload.taskId);
    const date = dateOnly(payload.date);
    let timeZone;
    try { timeZone = resolveTimezone(req.get('X-Potok-Timezone') || 'Europe/Moscow'); }
    catch { fail(400, 'Некорректный часовой пояс. Укажите имя IANA, например Europe/Moscow.'); }
    if (date > dateInTimezone(new Date(), timeZone)) fail(400, 'Нельзя записать рабочее время на будущую дату.');
    if (typeof payload.durationMinutes !== 'number' || !Number.isFinite(payload.durationMinutes) || payload.durationMinutes < 1 || payload.durationMinutes > 24 * 60) fail(400, 'Укажите длительность от 1 минуты до 24 часов.');
    const note = text(payload.note ?? '', 'Комментарий', 1000, { empty: true });
    const durationSeconds = Math.round(payload.durationMinutes * 60);
    let startAt;
    try { startAt = localNoonInstant(date, timeZone); }
    catch { fail(400, 'Указанная дата не существует в выбранном часовом поясе.'); }
    const timeEntry = { id: randomUUID(), userId: req.user.id, taskId: task.id, startAt,
      endAt: new Date(Date.parse(startAt) + durationSeconds * 1000).toISOString(), durationSeconds, note };
    transaction(db, () => {
      db.prepare('INSERT INTO time_entries (id,userId,taskId,startAt,endAt,durationSeconds,note) VALUES (?,?,?,?,?,?,?)')
        .run(timeEntry.id, timeEntry.userId, timeEntry.taskId, timeEntry.startAt, timeEntry.endAt, timeEntry.durationSeconds, timeEntry.note);
      activity(req.user.id, task.id, `добавил(а) запись рабочего времени в задаче «${task.title}»`);
    });
    res.status(201).json({ timeEntry });
  }));
  app.delete('/api/time-entries/:id', asyncRoute((req, res) => {
    const entry = db.prepare('SELECT * FROM time_entries WHERE id=?').get(req.params.id);
    if (!entry) fail(404, 'Запись времени не найдена.');
    if (entry.userId !== req.user.id && req.user.role !== 'admin') fail(403, 'Можно удалить только собственную запись времени.');
    transaction(db, () => {
      db.prepare('DELETE FROM time_entries WHERE id=?').run(entry.id);
      activity(req.user.id, entry.taskId, 'удалил(а) запись рабочего времени');
    });
    res.json({ ok: true });
  }));
  app.use('/api', (req, res) => res.status(404).json({ error: 'API-метод не найден.' }));

  if (existsSync(resolve(staticDir, 'index.html'))) {
    app.use(express.static(staticDir, { index: false }));
    app.use((req, res, next) => {
      if (req.method === 'GET' && req.accepts('html')) return res.sendFile(resolve(staticDir, 'index.html'));
      next();
    });
  }
  app.use((req, res) => res.status(404).json({ error: 'Страница не найдена.' }));
  app.use((error, req, res, next) => {
    if (res.headersSent) return next(error);
    if (error instanceof ApiError) return res.status(error.status).json({ error: error.message });
    if (error.type === 'entity.parse.failed') return res.status(400).json({ error: 'Некорректный JSON.' });
    if (error.type === 'entity.too.large') return res.status(413).json({ error: 'Запрос слишком большой.' });
    if (error.code?.startsWith('SQLITE_CONSTRAINT') || (error.errcode & 255) === 19) return res.status(409).json({ error: 'Изменение конфликтует с существующими данными.' });
    console.error('Potok API error:', error.message);
    res.status(500).json({ error: 'Внутренняя ошибка сервера.' });
  });
  return app;
}
