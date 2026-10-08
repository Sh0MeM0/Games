import { resolve } from 'node:path';
import { createApp } from './app.js';

const production = process.env.NODE_ENV === 'production';
const demo = process.env.POTOK_DEMO_MODE === undefined ? !production : process.env.POTOK_DEMO_MODE === 'true';
const port = Number(process.env.PORT || 3001);
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('PORT должен быть числом от 1 до 65535.');
let trustProxy = false;
if (process.env.POTOK_TRUST_PROXY) {
  trustProxy = Number(process.env.POTOK_TRUST_PROXY);
  if (!Number.isInteger(trustProxy) || trustProxy < 0) throw new Error('POTOK_TRUST_PROXY должен быть неотрицательным количеством доверенных прокси.');
}
const secureCookieSetting = process.env.POTOK_SECURE_COOKIE ?? process.env.POTOK_SECURE_COOKIES;
const app = createApp({
  dbPath: resolve(process.env.POTOK_DB_PATH || (demo ? 'data/potok-demo.sqlite' : 'data/potok.sqlite')),
  demo,
  adminPassword: process.env.POTOK_ADMIN_PASSWORD,
  adminEmail: process.env.POTOK_ADMIN_EMAIL || 'admin@potok.local',
  secureCookies: secureCookieSetting === undefined ? production : secureCookieSetting === 'true',
  trustProxy,
});
const server = app.listen(port, '0.0.0.0', () => {
  console.log(`Поток: сервер запущен на порту ${port}. Режим: ${demo ? 'демонстрация' : 'рабочий'}.`);
});
const shutdown = () => {
  server.close(() => { app.locals.db.close(); process.exit(0); });
  const force = setTimeout(() => process.exit(1), 10000);
  force.unref();
};
process.on('SIGTERM', shutdown);
process.on('SIGINT', shutdown);
