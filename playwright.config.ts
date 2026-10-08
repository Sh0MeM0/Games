import { defineConfig } from '@playwright/test';
import { existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const dbPath = join(tmpdir(), `potok-browser-${process.pid}-${Date.now()}.sqlite`);
const chromiumPath = process.env.POTOK_CHROMIUM_PATH || (existsSync('/usr/bin/chromium') ? '/usr/bin/chromium' : undefined);
export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  timeout: 45000,
  expect: { timeout: 8000 },
  reporter: 'list',
  use: {
    baseURL: 'http://127.0.0.1:3012',
    viewport: { width: 1440, height: 1050 },
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    launchOptions: { executablePath: chromiumPath, args: ['--no-sandbox', '--disable-dev-shm-usage'] },
  },
  webServer: {
    command: 'node server/index.js',
    url: 'http://127.0.0.1:3012/api/health',
    timeout: 30000,
    reuseExistingServer: false,
    env: { PORT: '3012', NODE_ENV: 'test', POTOK_DEMO_MODE: 'true', POTOK_DB_PATH: dbPath, POTOK_SECURE_COOKIE: 'false' },
  },
});
