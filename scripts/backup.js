import { DatabaseSync, backup } from 'node:sqlite';
import { existsSync, mkdirSync, chmodSync } from 'node:fs';
import { resolve, basename } from 'node:path';

const input = resolve(process.env.POTOK_DB_PATH || 'data/potok.sqlite');
if (!existsSync(input)) {
  console.error('База данных не найдена. Укажите POTOK_DB_PATH (для демо: data/potok-demo.sqlite).');
  process.exit(1);
}
const directory = resolve(process.argv[2] || 'data/backups');
mkdirSync(directory, { recursive: true, mode: 0o700 });
const target = resolve(directory, `${basename(input, '.sqlite')}-${new Date().toISOString().replace(/[:.]/g, '-')}.sqlite`);
const db = new DatabaseSync(input, { readOnly: true });
try {
  await backup(db, target);
  chmodSync(target, 0o600);
  console.log(`Резервная копия сохранена: ${target}`);
} finally {
  db.close();
}
