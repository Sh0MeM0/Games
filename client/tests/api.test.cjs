const { test } = require('node:test');
const assert = require('node:assert/strict');
const { Api, ApiError, normalizeServer } = require('../dist-electron/main/api');

test('HTTPS only; credentials and paths cannot be embedded in a server URL', () => {
  assert.equal(normalizeServer(' https://archive.example.org/ '), 'https://archive.example.org');
  for (const url of ['http://example.org', 'https://user:password@example.org', 'https://example.org/api', 'https://example.org/?token=foo']) {
    assert.throws(() => normalizeServer(url));
  }
});

test('parallel 401 requests rotate refresh token once and retry with the new access token', async () => {
  const original = global.fetch;
  let refreshes = 0;
  const persisted = [];
  const api = new Api(async tokens => persisted.push(tokens));
  api.server = 'https://archive.example.org';
  api.credentials = { access_token: 'old', refresh_token: 'refresh-old' };
  global.fetch = async (url, options) => {
    if (url.endsWith('/api/auth/refresh')) {
      refreshes++;
      await new Promise(resolve => setTimeout(resolve, 20));
      return Response.json({ access_token: 'new', refresh_token: 'refresh-new' });
    }
    return options.headers.get('Authorization') === 'Bearer old'
      ? Response.json({ detail: 'expired' }, { status: 401 }) : Response.json({ success: true });
  };
  try {
    const results = await Promise.all([api.request('/api/nodes'), api.request('/api/nodes')]);
    assert.deepEqual(results, [{ success: true }, { success: true }]);
    assert.equal(refreshes, 1);
    assert.equal(persisted[0].refresh_token, 'refresh-new');
  } finally { global.fetch = original; }
});

test('network failure gives a Russian error and retains credentials for retry', async () => {
  const original = global.fetch;
  const api = new Api(async () => {});
  api.server = 'https://archive.example.org';
  api.credentials = { access_token: 'access', refresh_token: 'refresh' };
  global.fetch = async () => { throw new TypeError('fetch failed'); };
  try {
    await assert.rejects(api.request('/api/nodes'), error => error instanceof ApiError && error.status === 0 && error.message.includes('Нет связи'));
    assert.equal(api.credentials.refresh_token, 'refresh');
  } finally { global.fetch = original; }
});
