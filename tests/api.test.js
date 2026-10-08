import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import request from 'supertest';
import { createApp } from '../server/app.js';

const DEMO_PASSWORD = 'PotokDemo2026!';
const ADMIN_PASSWORD = 'TestAdministrator2026!';
const MEMBER_PASSWORD = 'MemberForTests2026!';

function fixture(t, options = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'potok-api-test-'));
  const dbPath = join(dir, 'workspace.sqlite');
  let app = createApp({ dbPath, demo: true, secureCookies: false, ...options });
  t.after(() => {
    if (app) app.locals.db.close();
    rmSync(dir, { recursive: true, force: true });
  });
  return {
    get app() { return app; },
    dbPath,
    reopen(nextOptions = options) {
      app.locals.db.close();
      app = null;
      app = createApp({ dbPath, demo: true, secureCookies: false, ...nextOptions });
      return app;
    },
  };
}

async function login(app, email = 'admin@potok.local', password = DEMO_PASSWORD) {
  const client = request.agent(app);
  const response = await client.post('/api/auth/login').send({ email, password }).expect(200);
  return { client, response };
}

async function workspace(client) {
  return (await client.get('/api/workspace').expect(200)).body;
}

async function createTask(client, attributes = {}) {
  return (await client.post('/api/tasks').send({ title: 'Интеграционная задача', ...attributes }).expect(201)).body.task;
}

async function createMember(client, attributes = {}) {
  return (await client.post('/api/users').send({ name: 'Тестовый участник', email: 'member@example.test', role: 'member', password: MEMBER_PASSWORD, ...attributes }).expect(201)).body.user;
}

test('workspace requires authentication and demo seed contains 30 participants without credentials', async (t) => {
  const { app } = fixture(t);
  await request(app).get('/api/workspace').expect(401);
  const { client } = await login(app);
  const state = await workspace(client);
  assert.equal(state.users.length, 30);
  assert.ok(state.projects.length > 0);
  assert.ok(state.tasks.some((task) => task.parentId));
  assert.equal(state.user.id, 'user-admin');
  assert.ok(state.users.every((user) => !('passwordHash' in user) && !('password' in user)));
  assert.ok(Array.isArray(state.timeEntries));
  assert.ok(Array.isArray(state.activities));
});

test('login issues an HttpOnly session whose hash is persisted and logout revokes it', async (t) => {
  const { app } = fixture(t);
  await request(app).post('/api/auth/login').send({ email: 'admin@potok.local', password: 'wrong-password' }).expect(401);
  const { client, response } = await login(app);
  const setCookie = response.headers['set-cookie'][0];
  assert.match(setCookie, /HttpOnly/i);
  assert.match(setCookie, /SameSite=Lax/i);
  const rawCookie = setCookie.split(';', 1)[0];
  const rawToken = decodeURIComponent(rawCookie.slice(rawCookie.indexOf('=') + 1));
  const sessions = app.locals.db.prepare('SELECT tokenHash FROM sessions').all();
  assert.equal(sessions.length, 1);
  assert.equal(sessions[0].tokenHash, createHash('sha256').update(rawToken).digest('hex'));
  assert.notEqual(sessions[0].tokenHash, rawToken);
  await client.get('/api/workspace').expect(200);
  await client.post('/api/auth/logout').expect(200);
  await client.get('/api/workspace').expect(401);
  await request(app).get('/api/workspace').set('Cookie', rawCookie).expect(401);
  assert.equal(app.locals.db.prepare('SELECT COUNT(*) AS count FROM sessions').get().count, 0);
});

test('production boot requires a private password and does not expose demo impersonation', async (t) => {
  assert.throws(() => createApp({ dbPath: ':memory:', demo: false }), /POTOK_ADMIN_PASSWORD/);
  assert.throws(() => createApp({ dbPath: ':memory:', demo: false, adminPassword: 'short' }), /POTOK_ADMIN_PASSWORD/);
  assert.throws(() => createApp({ dbPath: ':memory:', demo: false, adminPassword: 'a'.repeat(129) }), /POTOK_ADMIN_PASSWORD/);
  const { app } = fixture(t, { demo: false, adminPassword: ADMIN_PASSWORD });
  await request(app).post('/api/auth/demo').send({ userId: 'user-admin' }).expect(404);
  await request(app).post('/api/auth/login').send({ email: 'admin@potok.local', password: DEMO_PASSWORD }).expect(401);
  const { client } = await login(app, 'admin@potok.local', ADMIN_PASSWORD);
  const state = await workspace(client);
  assert.equal(state.users.length, 1);
  assert.equal(state.tasks.length, 0);
  assert.equal(state.projects.length, 0);
  const stored = app.locals.db.prepare('SELECT passwordHash FROM users WHERE id = ?').get('user-admin').passwordHash;
  assert.match(stored, /^scrypt:/);
  assert.ok(!stored.includes(ADMIN_PASSWORD));
});

test('demo entry signs in and production cannot reuse a demonstration database', async (t) => {
  const fx = fixture(t);
  const client = request.agent(fx.app);
  const entry = await client.post('/api/auth/demo').send({}).expect(200);
  assert.equal(entry.body.user.id, 'user-admin');
  assert.equal((await workspace(client)).users.length, 30);
  assert.throws(() => fx.reopen({ demo: false, adminPassword: ADMIN_PASSWORD }), /POTOK_DB_PATH/);
});

test('demo mode cannot reopen a private working database', (t) => {
  const fx = fixture(t, { demo: false, adminPassword: ADMIN_PASSWORD });
  assert.throws(() => fx.reopen({ demo: true }), /POTOK_DB_PATH/);
});

test('nested subtasks derive parent progress and completion from descendants', async (t) => {
  const { app } = fixture(t);
  const { client } = await login(app);
  const parent = await createTask(client, { title: 'Новая функция', projectId: 'project-1', assigneeIds: ['user-2', 'user-3'], priority: 'high' });
  assert.equal(parent.createdBy, 'user-admin');
  assert.deepEqual([...parent.assigneeIds].sort(), ['user-2', 'user-3']);
  const design = await createTask(client, { title: 'Дизайн', parentId: parent.id, projectId: 'project-1', status: 'in_progress', progress: 20, assigneeIds: ['user-2'] });
  const code = await createTask(client, { title: 'Разработка', parentId: parent.id, projectId: 'project-1', status: 'done', progress: 15, assigneeIds: ['user-3'] });
  assert.equal(code.progress, 100, 'completed leaf tasks have 100 percent progress');
  const find = async (id) => (await workspace(client)).tasks.find((task) => task.id === id);
  assert.equal((await find(parent.id)).progress, 60);
  assert.equal((await find(parent.id)).status, 'in_progress');
  await client.patch(`/api/tasks/${parent.id}`).send({ progress: 99, status: 'done' }).expect(200);
  assert.equal((await find(parent.id)).progress, 60, 'parent progress cannot disagree with children');
  const research = await createTask(client, { title: 'Исследование', parentId: design.id, projectId: 'project-1', status: 'in_progress', progress: 30 });
  await createTask(client, { title: 'Макеты', parentId: design.id, projectId: 'project-1', status: 'done' });
  assert.equal((await find(design.id)).progress, 65);
  assert.equal((await find(parent.id)).progress, 83);
  await client.patch(`/api/tasks/${research.id}`).send({ status: 'done' }).expect(200);
  const finished = await find(parent.id);
  assert.equal(finished.status, 'done');
  assert.equal(finished.progress, 100);
});

test('members update assigned and self-created tasks while other work and team management stay protected', async (t) => {
  const { app } = fixture(t, { demo: false, adminPassword: ADMIN_PASSWORD });
  const { client: admin } = await login(app, 'admin@potok.local', ADMIN_PASSWORD);
  const member = await createMember(admin);
  const { client } = await login(app, member.email, MEMBER_PASSWORD);
  const assigned = await createTask(admin, { assigneeIds: [member.id] });
  const other = await createTask(admin, { title: 'Задача другого участника', assigneeIds: ['user-admin'] });
  await client.patch(`/api/tasks/${assigned.id}`).send({ progress: 45, status: 'in_progress' }).expect(200);
  assert.equal((await workspace(admin)).tasks.find((task) => task.id === assigned.id).progress, 45);
  const own = await createTask(client, { title: 'Моя задача', assigneeIds: [member.id] });
  assert.equal(own.createdBy, member.id);
  await client.patch(`/api/tasks/${own.id}`).send({ title: 'Обновленная моя задача' }).expect(200);
  await client.patch(`/api/tasks/${other.id}`).send({ progress: 100 }).expect(403);
  await client.delete(`/api/tasks/${other.id}`).expect(403);
  await client.post('/api/tasks').send({ title: 'Чужая подзадача', parentId: other.id }).expect(403);
  await client.post('/api/users').send({ name: 'Незаконный администратор', email: 'forbidden@example.test', role: 'admin', password: MEMBER_PASSWORD }).expect(403);
  await client.patch(`/api/users/${member.id}`).send({ role: 'admin' }).expect(403);
  assert.equal((await workspace(admin)).users.find((user) => user.id === member.id).role, 'member');
});

test('task and project validation reject malformed state without partially creating records', async (t) => {
  const { app } = fixture(t);
  const { client } = await login(app);
  const before = await workspace(client);
  const invalidTasks = [
    { title: '   ' },
    { title: 'Неверный статус', status: 'unknown' },
    { title: 'Неверный прогресс', progress: 101 },
    { title: 'Неверный прогресс', progress: -1 },
    { title: 'Неверный приоритет', priority: 'urgent' },
    { title: 'Нет проекта', projectId: 'missing-project' },
    { title: 'Нет исполнителя', assigneeIds: ['missing-user'] },
    { title: 'Неверная дата', dueDate: '2026-02-30' },
  ];
  for (const body of invalidTasks) {
    const response = await client.post('/api/tasks').send(body).expect(400);
    assert.equal(typeof response.body.error, 'string');
  }
  await client.post('/api/projects').send({ name: ' ', color: '#7864D9' }).expect(400);
  await client.post('/api/projects').send({ name: 'Новый проект', color: 'javascript:evil' }).expect(400);
  const after = await workspace(client);
  assert.equal(after.tasks.length, before.tasks.length);
  assert.equal(after.projects.length, before.projects.length);
  const project = (await client.post('/api/projects').send({ name: 'Новый проект', color: '#7864D9', description: 'Описание проекта' }).expect(201)).body.project;
  const task = await createTask(client, { projectId: project.id });
  assert.equal(task.projectId, project.id);
});

test('task hierarchy cannot contain a cycle and removing subtasks updates their parent', async (t) => {
  const { app } = fixture(t);
  const { client } = await login(app);
  const parent = await createTask(client);
  const child = await createTask(client, { title: 'Первая подзадача', parentId: parent.id, progress: 20, status: 'in_progress' });
  await createTask(client, { title: 'Вторая подзадача', parentId: parent.id, status: 'done' });
  await client.patch(`/api/tasks/${parent.id}`).send({ parentId: child.id }).expect(400);
  await client.patch(`/api/tasks/${child.id}`).send({ parentId: child.id }).expect(400);
  await client.delete(`/api/tasks/${child.id}`).expect(200);
  const updated = (await workspace(client)).tasks.find((task) => task.id === parent.id);
  assert.equal(updated.progress, 100);
  assert.equal(updated.status, 'done');
  await client.delete(`/api/tasks/${parent.id}`).expect(200);
  assert.ok(!(await workspace(client)).tasks.some((task) => task.id === parent.id || task.parentId === parent.id));
});

test('manual time has consistent duration, validates input and can only be deleted by its owner or admin', async (t) => {
  const { app } = fixture(t, { demo: false, adminPassword: ADMIN_PASSWORD });
  const { client: admin } = await login(app, 'admin@potok.local', ADMIN_PASSWORD);
  const first = await createMember(admin);
  const second = await createMember(admin, { name: 'Второй участник', email: 'second@example.test' });
  const { client: member } = await login(app, first.email, MEMBER_PASSWORD);
  const { client: outsider } = await login(app, second.email, MEMBER_PASSWORD);
  const task = await createTask(admin, { assigneeIds: [first.id] });
  const date = new Date().toISOString().slice(0, 10);
  const entry = (await member.post('/api/time-entries').send({ taskId: task.id, date, durationMinutes: 90, note: 'Работа над задачей' }).expect(201)).body.timeEntry;
  assert.equal(entry.userId, first.id);
  assert.equal(entry.taskId, task.id);
  assert.equal(entry.durationSeconds, 5400);
  assert.equal((Date.parse(entry.endAt) - Date.parse(entry.startAt)) / 1000, 5400);
  assert.equal(entry.note, 'Работа над задачей');
  assert.ok((await workspace(admin)).timeEntries.some((item) => item.id === entry.id));
  assert.deepEqual((await workspace(outsider)).timeEntries, [], 'another member cannot read someone else’s working time');
  const adminEntry = (await admin.post('/api/time-entries').send({ taskId: task.id, date, durationMinutes: 20 }).expect(201)).body.timeEntry;
  assert.ok(!(await workspace(member)).timeEntries.some((item) => item.id === adminEntry.id));
  assert.equal((await workspace(member)).timeEntries.filter((item) => item.id === entry.id).length, 1);
  await outsider.delete(`/api/time-entries/${entry.id}`).expect(403);
  await outsider.post('/api/time-entries').send({ taskId: task.id, date, durationMinutes: 30 }).expect(403);
  for (const invalid of [{ durationMinutes: 0 }, { durationMinutes: -2 }, { durationMinutes: 1441 }, { date: '2026-02-30' }]) {
    await member.post('/api/time-entries').send({ taskId: task.id, date, durationMinutes: 30, ...invalid }).expect(400);
  }
  await member.delete(`/api/time-entries/${entry.id}`).expect(200);
  assert.ok(!(await workspace(admin)).timeEntries.some((item) => item.id === entry.id));
});

test('a member has one timer, stopping saves elapsed time and duplicate stops cannot double count', async (t) => {
  const { app } = fixture(t, { demo: false, adminPassword: ADMIN_PASSWORD });
  const { client: admin } = await login(app, 'admin@potok.local', ADMIN_PASSWORD);
  const member = await createMember(admin);
  const { client } = await login(app, member.email, MEMBER_PASSWORD);
  const task = await createTask(admin, { assigneeIds: [member.id] });
  const started = await client.post('/api/timer/start').send({ taskId: task.id }).expect(201);
  assert.equal(started.body.runningTimer.taskId, task.id);
  assert.equal(started.body.runningTimer.userId, member.id);
  await client.post('/api/timer/start').send({ taskId: task.id }).expect(409);
  assert.equal((await workspace(client)).runningTimer.taskId, task.id);
  await new Promise((resolve) => setTimeout(resolve, 1100));
  const stop = await client.post('/api/timer/stop').send({ note: 'Реальная работа' }).expect(200);
  assert.ok(stop.body.timeEntry.durationSeconds >= 1);
  assert.equal(stop.body.timeEntry.userId, member.id);
  assert.equal(stop.body.timeEntry.note, 'Реальная работа');
  assert.equal((await workspace(client)).runningTimer, null);
  await client.post('/api/timer/stop').send({}).expect(409);
  assert.equal((await workspace(client)).timeEntries.filter((entry) => entry.taskId === task.id).length, 1);
});

test('SQLite keeps tasks, manual entries, timers and valid sessions across app restarts', async (t) => {
  const fx = fixture(t, { demo: false, adminPassword: ADMIN_PASSWORD });
  const { client, response } = await login(fx.app, 'admin@potok.local', ADMIN_PASSWORD);
  const cookie = response.headers['set-cookie'][0].split(';', 1)[0];
  const task = await createTask(client, { title: 'Сохраненная задача', progress: 25, status: 'in_progress' });
  const entry = (await client.post('/api/time-entries').send({ taskId: task.id, date: new Date().toISOString().slice(0, 10), durationMinutes: 40 }).expect(201)).body.timeEntry;
  await client.post('/api/timer/start').send({ taskId: task.id }).expect(201);
  fx.reopen({ demo: false });
  const state = (await request(fx.app).get('/api/workspace').set('Cookie', cookie).expect(200)).body;
  assert.equal(state.tasks.find((item) => item.id === task.id).title, 'Сохраненная задача');
  assert.equal(state.tasks.find((item) => item.id === task.id).progress, 25);
  assert.equal(state.timeEntries.find((item) => item.id === entry.id).durationSeconds, 2400);
  assert.equal(state.runningTimer.taskId, task.id);
  assert.equal(state.users.length, 1, 'restart does not reseed existing data');
});

test('deactivation revokes a member session and login while leaving the admin available', async (t) => {
  const { app } = fixture(t, { demo: false, adminPassword: ADMIN_PASSWORD });
  const { client: admin } = await login(app, 'admin@potok.local', ADMIN_PASSWORD);
  const member = await createMember(admin);
  const { client } = await login(app, member.email, MEMBER_PASSWORD);
  await admin.patch(`/api/users/${member.id}`).send({ active: false }).expect(200);
  await client.get('/api/workspace').expect(401);
  await request(app).post('/api/auth/login').send({ email: member.email, password: MEMBER_PASSWORD }).expect(401);
  assert.equal((await workspace(admin)).user.id, 'user-admin');
  await admin.patch('/api/users/user-admin').send({ active: false }).expect(409);
  await admin.patch('/api/users/user-admin').send({ role: 'member' }).expect(409);
});

test('cross-origin mutations are rejected while same-origin requests work', async (t) => {
  const { app } = fixture(t);
  const { client } = await login(app);
  const before = (await workspace(client)).tasks.length;
  await client.post('/api/tasks').set('Origin', 'https://attacker.invalid').send({ title: 'Внешняя задача' }).expect(403);
  await client.post('/api/tasks').set('Sec-Fetch-Site', 'cross-site').send({ title: 'Еще внешняя задача' }).expect(403);
  assert.equal((await workspace(client)).tasks.length, before);
  await client.post('/api/tasks').set('Host', 'localhost:3001').set('Origin', 'http://localhost:3001').send({ title: 'Задача из приложения' }).expect(201);
});

test('profile password change requires the current password and invalidates old credentials', async (t) => {
  const { app } = fixture(t, { demo: false, adminPassword: ADMIN_PASSWORD });
  const { client, response } = await login(app, 'admin@potok.local', ADMIN_PASSWORD);
  const oldCookie = response.headers['set-cookie'][0].split(';', 1)[0];
  const newPassword = 'ChangedPrivatePassword2026!';
  await client.patch('/api/profile').send({ currentPassword: 'incorrect', newPassword }).expect(403);
  await client.patch('/api/profile').send({ name: 'Новое имя', currentPassword: ADMIN_PASSWORD, newPassword }).expect(200);
  await request(app).get('/api/workspace').set('Cookie', oldCookie).expect(401);
  assert.equal((await workspace(client)).user.name, 'Новое имя', 'the current browser receives a replacement session');
  await request(app).post('/api/auth/login').send({ email: 'admin@potok.local', password: ADMIN_PASSWORD }).expect(401);
  const { client: fresh } = await login(app, 'admin@potok.local', newPassword);
  assert.equal((await workspace(fresh)).user.name, 'Новое имя');
});

test('deleting a task or its ancestor cannot remove working time or an active timer', async (t) => {
  const { app } = fixture(t, { demo: false, adminPassword: ADMIN_PASSWORD });
  const { client } = await login(app, 'admin@potok.local', ADMIN_PASSWORD);
  const parent = await createTask(client, { title: 'История проекта' });
  const child = await createTask(client, { title: 'Задача с историей', parentId: parent.id });
  await client.post('/api/timer/start').send({ taskId: child.id }).expect(201);
  await client.delete(`/api/tasks/${child.id}`).expect(409);
  await client.delete(`/api/tasks/${parent.id}`).expect(409);
  const entry = (await client.post('/api/timer/stop').send({ note: 'Сохраняем историю' }).expect(200)).body.timeEntry;
  await client.delete(`/api/tasks/${child.id}`).expect(409);
  await client.delete(`/api/tasks/${parent.id}`).expect(409);
  const state = await workspace(client);
  assert.ok(state.tasks.some((task) => task.id === parent.id));
  assert.ok(state.tasks.some((task) => task.id === child.id));
  assert.ok(state.timeEntries.some((item) => item.id === entry.id));
});

test('deactivating a participant closes their timer and excludes them from new assignments', async (t) => {
  const { app } = fixture(t, { demo: false, adminPassword: ADMIN_PASSWORD });
  const { client: admin } = await login(app, 'admin@potok.local', ADMIN_PASSWORD);
  const member = await createMember(admin);
  const { client } = await login(app, member.email, MEMBER_PASSWORD);
  const task = await createTask(admin, { assigneeIds: [member.id] });
  await client.post('/api/timer/start').send({ taskId: task.id }).expect(201);
  await admin.patch(`/api/users/${member.id}`).send({ active: false }).expect(200);
  await client.post('/api/timer/stop').send({}).expect(401);
  const state = await workspace(admin);
  const entries = state.timeEntries.filter((entry) => entry.userId === member.id && entry.taskId === task.id);
  assert.equal(entries.length, 1);
  assert.ok(entries[0].durationSeconds >= 0);
  await admin.post('/api/tasks').send({ title: 'Недоступный исполнитель', assigneeIds: [member.id] }).expect(400);
  await admin.patch(`/api/users/${member.id}`).send({ active: true }).expect(200);
  const { client: returned } = await login(app, member.email, MEMBER_PASSWORD);
  assert.equal((await workspace(returned)).runningTimer, null);
});

test('team account creation validates passwords, email uniqueness and role names', async (t) => {
  const { app } = fixture(t, { demo: false, adminPassword: ADMIN_PASSWORD });
  const { client } = await login(app, 'admin@potok.local', ADMIN_PASSWORD);
  const member = await createMember(client);
  assert.ok(!('passwordHash' in member));
  for (const invalid of [
    { name: '', email: 'valid@example.test', password: MEMBER_PASSWORD },
    { name: 'Тест', email: 'wrong-email', password: MEMBER_PASSWORD },
    { name: 'Тест', email: 'valid@example.test', password: 'short' },
    { name: 'Тест', email: 'valid@example.test', password: MEMBER_PASSWORD, role: 'superuser' },
  ]) {
    await client.post('/api/users').send(invalid).expect(400);
  }
  await client.post('/api/users').send({ name: 'Дубликат', email: member.email.toUpperCase(), password: MEMBER_PASSWORD }).expect(409);
  assert.equal((await workspace(client)).users.length, 2);
});

test('the twenty-level hierarchy limit also applies to moving an existing subtree', async (t) => {
  const { app } = fixture(t, { demo: false, adminPassword: ADMIN_PASSWORD });
  const { client } = await login(app, 'admin@potok.local', ADMIN_PASSWORD);
  const chain = [];
  for (let depth = 1; depth <= 20; depth++) {
    chain.push(await createTask(client, { title: `Уровень ${depth}`, parentId: chain.at(-1)?.id ?? null }));
  }
  await client.post('/api/tasks').send({ title: 'Слишком глубокая подзадача', parentId: chain[19].id }).expect(400);
  const subtree = await createTask(client, { title: 'Перемещаемая задача' });
  const leaf = await createTask(client, { title: 'Ее подзадача', parentId: subtree.id });
  await client.patch(`/api/tasks/${subtree.id}`).send({ parentId: chain[18].id }).expect(400);
  const unchanged = (await workspace(client)).tasks.find((task) => task.id === subtree.id);
  assert.equal(unchanged.parentId, null);
  await client.patch(`/api/tasks/${subtree.id}`).send({ parentId: chain[17].id }).expect(200);
  const state = await workspace(client);
  assert.equal(state.tasks.find((task) => task.id === subtree.id).parentId, chain[17].id);
  assert.equal(state.tasks.find((task) => task.id === leaf.id).parentId, subtree.id);
});

test('manual entries use the requested calendar timezone at midnight boundaries', async (t) => {
  t.mock.timers.enable({ apis: ['Date'], now: new Date('2026-10-06T22:00:00.000Z') });
  t.after(() => t.mock.timers.reset());
  const { app } = fixture(t, { demo: false, adminPassword: ADMIN_PASSWORD });
  const { client } = await login(app, 'admin@potok.local', ADMIN_PASSWORD);
  const task = await createTask(client);
  const send = (date, timezone) => client.post('/api/time-entries')
    .set('X-Potok-Timezone', timezone)
    .send({ taskId: task.id, date, durationMinutes: 90 });
  const moscow = (await send('2026-10-07', 'Europe/Moscow').expect(201)).body.timeEntry;
  assert.equal(moscow.startAt, '2026-10-07T09:00:00.000Z');
  assert.equal(moscow.durationSeconds, 5400);
  await send('2026-10-08', 'Europe/Moscow').expect(400);
  const losAngeles = (await send('2026-10-06', 'America/Los_Angeles').expect(201)).body.timeEntry;
  assert.equal(losAngeles.startAt, '2026-10-06T19:00:00.000Z');
  await send('2026-10-07', 'America/Los_Angeles').expect(400);
  await send('2026-10-06', 'Not/A_Timezone').expect(400);
  const defaultZone = (await client.post('/api/time-entries').send({ taskId: task.id, date: '2026-10-07', durationMinutes: 30 }).expect(201)).body.timeEntry;
  assert.equal(defaultZone.startAt, '2026-10-07T09:00:00.000Z', 'the default calendar timezone is Moscow');
});
