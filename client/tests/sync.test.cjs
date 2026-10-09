const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { SyncManager, sha256, safeLocalName } = require('../dist-electron/main/sync');
const { ApiError } = require('../dist-electron/main/api');

function mockApi() {
  return {
    uploads: [], releases: 0,
    async post(route) {
      if (route.endsWith('/checkout')) return { lock_token: 'test-lock', version: 1,
        expires_at: new Date(Date.now() + 180_000).toISOString(), heartbeat_seconds: 20,
        node: { name: 'Учёт.docx' } };
      if (route.endsWith('/release')) { this.releases++; return {}; }
      if (route.endsWith('/heartbeat')) return { expires_at: new Date(Date.now() + 180_000).toISOString() };
      throw new Error('Unexpected route');
    },
    async raw() { return new Response('initial', { headers: { 'X-File-SHA256': sha256(Buffer.from('initial')) } }); },
    async request(route, options) {
      assert.ok(route.endsWith('/versions'));
      this.uploads.push({ base: Number(options.body.get('base_version')), content: await options.body.get('file').text() });
      return { version: 2 };
    },
  };
}

test('local names cannot escape the working directory or use Windows devices', () => {
  for (const name of ['../evil.docx', 'C:\\evil.docx', 'CON.docx', 'Учёт.docx.', 'NUL', 'x/y.xls']) assert.equal(safeLocalName(name), false);
  assert.equal(safeLocalName('Общий учёт.docx'), true);
});

test('finish uploads the latest saved bytes before releasing the lock and preserves a completed copy', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'archive-sync-'));
  const api = mockApi(); const manager = new SyncManager(api, root, () => {});
  try {
    await manager.start('file-id', 'user-id', async () => '');
    const filename = manager.localPath('file-id');
    await fs.writeFile(filename, 'latest saved bytes');
    await manager.finish('file-id');
    assert.deepEqual(api.uploads, [{ base: 1, content: 'latest saved bytes' }]);
    assert.equal(api.releases, 1);
    assert.equal(manager.list().length, 0);
    assert.equal(await fs.readFile(filename, 'utf8'), 'latest saved bytes');
  } finally { await manager.shutdown(); await fs.rm(root, { recursive: true, force: true }); }
});

test('lost lease prevents upload and release; local changes remain available', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'archive-sync-'));
  const api = mockApi(); const manager = new SyncManager(api, root, () => {});
  try {
    await manager.start('file-id', 'user-id', async () => '');
    const filename = manager.localPath('file-id');
    await fs.writeFile(filename, 'offline changes');
    api.post = async route => { if (route.endsWith('/heartbeat')) throw new ApiError(409, 'Блокировка утрачена'); throw new Error('Unexpected upload/release'); };
    await assert.rejects(manager.finish('file-id'), /Блокировка утрачена/);
    assert.equal(api.uploads.length, 0);
    assert.equal(manager.list()[0].state, 'conflict');
    assert.equal(await fs.readFile(filename, 'utf8'), 'offline changes');
  } finally { await manager.shutdown(); await fs.rm(root, { recursive: true, force: true }); }
});

test('restart recovers local bytes and does not resume uploads with an old lock', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'archive-sync-'));
  const api = mockApi(); const first = new SyncManager(api, root, () => {});
  let second;
  try {
    await first.start('file-id', 'user-id', async () => '');
    await fs.writeFile(first.localPath('file-id'), 'unsent work');
    await first.shutdown();
    second = new SyncManager(api, root, () => {});
    await second.initialize('user-id');
    assert.equal(second.list()[0].state, 'recovered');
    assert.equal(await fs.readFile(second.localPath('file-id'), 'utf8'), 'unsent work');
    await assert.rejects(second.finish('file-id'), /Автозагрузка остановлена/);
    assert.equal(api.uploads.length, 0);
  } finally { await first.shutdown(); if (second) await second.shutdown(); await fs.rm(root, { recursive: true, force: true }); }
});

test('failed Office launch releases the newly acquired lock', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'archive-sync-'));
  const api = mockApi(); const manager = new SyncManager(api, root, () => {});
  try {
    await assert.rejects(manager.start('file-id', 'user-id', async () => 'No program associated'), /Установите Word\/Excel/);
    assert.equal(api.releases, 1);
    assert.equal(manager.list().length, 0);
  } finally { await manager.shutdown(); await fs.rm(root, { recursive: true, force: true }); }
});
