import { DatabaseSync } from 'node:sqlite';
import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { seedDatabase } from './seed.js';
import { hashPassword } from './security.js';

export function openDatabase({ dbPath, demo, adminPassword, seed = true, adminEmail = 'admin@potok.local' }) {
  if (dbPath !== ':memory:') mkdirSync(dirname(dbPath), { recursive: true, mode: 0o700 });
  const db = new DatabaseSync(dbPath);
  db.exec(`
    PRAGMA foreign_keys = ON;
    PRAGMA journal_mode = WAL;
    PRAGMA busy_timeout = 5000;
    CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS users (
      id TEXT PRIMARY KEY, name TEXT NOT NULL, email TEXT NOT NULL UNIQUE COLLATE NOCASE,
      role TEXT NOT NULL CHECK(role IN ('admin','member')), avatarColor TEXT NOT NULL,
      title TEXT NOT NULL DEFAULT '', active INTEGER NOT NULL DEFAULT 1 CHECK(active IN (0,1)),
      passwordHash TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS projects (id TEXT PRIMARY KEY, name TEXT NOT NULL, color TEXT NOT NULL, description TEXT NOT NULL DEFAULT '');
    CREATE TABLE IF NOT EXISTS tasks (
      id TEXT PRIMARY KEY, title TEXT NOT NULL, description TEXT NOT NULL DEFAULT '',
      projectId TEXT REFERENCES projects(id), parentId TEXT REFERENCES tasks(id) ON DELETE CASCADE,
      status TEXT NOT NULL CHECK(status IN ('todo','in_progress','review','done')),
      priority TEXT NOT NULL CHECK(priority IN ('low','medium','high')),
      progress REAL NOT NULL DEFAULT 0 CHECK(progress >= 0 AND progress <= 100),
      dueDate TEXT, createdBy TEXT NOT NULL REFERENCES users(id), createdAt TEXT NOT NULL, updatedAt TEXT NOT NULL
    );
    CREATE INDEX IF NOT EXISTS tasks_parent ON tasks(parentId);
    CREATE TABLE IF NOT EXISTS task_assignees (
      taskId TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
      userId TEXT NOT NULL REFERENCES users(id), PRIMARY KEY(taskId,userId)
    );
    CREATE TABLE IF NOT EXISTS time_entries (
      id TEXT PRIMARY KEY, userId TEXT NOT NULL REFERENCES users(id), taskId TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
      startAt TEXT NOT NULL, endAt TEXT NOT NULL, durationSeconds INTEGER NOT NULL CHECK(durationSeconds >= 0), note TEXT NOT NULL DEFAULT ''
    );
    CREATE INDEX IF NOT EXISTS time_entries_user ON time_entries(userId,startAt);
    CREATE TABLE IF NOT EXISTS timers (
      id TEXT PRIMARY KEY, userId TEXT NOT NULL UNIQUE REFERENCES users(id), taskId TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
      startedAt TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS sessions (
      tokenHash TEXT PRIMARY KEY, userId TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE, expiresAt INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS activities (
      id TEXT PRIMARY KEY, userId TEXT NOT NULL REFERENCES users(id), taskId TEXT REFERENCES tasks(id) ON DELETE SET NULL,
      text TEXT NOT NULL, createdAt TEXT NOT NULL
    );
    CREATE INDEX IF NOT EXISTS activities_date ON activities(createdAt);
  `);
  try {
    const mode = db.prepare('SELECT value FROM metadata WHERE key = ?').get('demo');
    if (mode && mode.value !== String(demo)) {
      throw new Error('База данных создана для другого режима. Для смены демонстрационного и рабочего режимов укажите отдельный POTOK_DB_PATH.');
    }
    const empty = db.prepare('SELECT COUNT(*) AS count FROM users').get().count === 0;
    if (empty && !demo && (typeof adminPassword !== 'string' || adminPassword.length < 12 || adminPassword.length > 128)) {
      throw new Error('Для первого рабочего запуска задайте POTOK_ADMIN_PASSWORD длиной от 12 до 128 символов.');
    }
    if (empty && seed) {
      transaction(db, () => {
        if (demo) seedDatabase(db);
        else {
          db.prepare('INSERT INTO users (id,name,email,role,avatarColor,title,passwordHash) VALUES (?,?,?,?,?,?,?)')
            .run('user-admin', 'Администратор', adminEmail.toLowerCase(), 'admin', '#7864D9', 'Администратор пространства', hashPassword(adminPassword));
        }
        db.prepare('INSERT OR REPLACE INTO metadata (key,value) VALUES (?,?)').run('demo', String(demo));
      });
    }
    return db;
  } catch (error) { db.close(); throw error; }
}

export function transaction(db, operation) {
  db.exec('BEGIN IMMEDIATE');
  try { const result = operation(); db.exec('COMMIT'); return result; }
  catch (error) { db.exec('ROLLBACK'); throw error; }
}

export function publicUser(row) {
  if (!row) return null;
  const { passwordHash, ...user } = row;
  return { ...user, active: Boolean(user.active) };
}

export function getTask(db, id) {
  const row = db.prepare('SELECT * FROM tasks WHERE id = ?').get(id);
  if (!row) return null;
  return { ...row, assigneeIds: db.prepare('SELECT userId FROM task_assignees WHERE taskId = ? ORDER BY userId').all(id).map((a) => a.userId) };
}

export function allTasks(db) {
  const assignees = new Map();
  for (const row of db.prepare('SELECT * FROM task_assignees ORDER BY userId').all()) {
    if (!assignees.has(row.taskId)) assignees.set(row.taskId, []);
    assignees.get(row.taskId).push(row.userId);
  }
  return db.prepare('SELECT * FROM tasks ORDER BY createdAt DESC, id').all().map((row) => ({ ...row, assigneeIds: assignees.get(row.id) || [] }));
}

export function recalculateAncestors(db, startingId) {
  let id = startingId;
  const visited = new Set();
  while (id && !visited.has(id)) {
    visited.add(id);
    const task = db.prepare('SELECT * FROM tasks WHERE id = ?').get(id);
    if (!task) break;
    const children = db.prepare('SELECT status,progress FROM tasks WHERE parentId = ?').all(id);
    if (children.length) {
      const progress = Math.round(children.reduce((sum, child) => sum + child.progress, 0) / children.length);
      const status = children.every((child) => child.status === 'done') ? 'done'
        : children.every((child) => child.status === 'todo') ? 'todo'
          : children.every((child) => child.status === 'done' || child.status === 'review') ? 'review' : 'in_progress';
      db.prepare('UPDATE tasks SET progress = ?, status = ?, updatedAt = ? WHERE id = ?').run(progress, status, new Date().toISOString(), id);
    } else if (task.status === 'done' && task.progress !== 100) {
      db.prepare('UPDATE tasks SET progress = 100 WHERE id = ?').run(id);
    }
    id = task.parentId;
  }
}
