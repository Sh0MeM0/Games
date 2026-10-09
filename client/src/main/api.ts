export class ApiError extends Error {
  readonly status: number;
  constructor(status: number, message: string) { super(message); this.status = status; }
}

export function normalizeServer(value: string): string {
  let url: URL;
  try { url = new URL(value.trim()); }
  catch { throw new Error('Введите корректный адрес HTTPS, например https://archive.example.org'); }
  if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash
      || !['', '/'].includes(url.pathname)) {
    throw new Error('Введите адрес HTTPS без пути, например https://archive.example.org');
  }
  return url.origin;
}

interface Credentials { access_token: string; refresh_token: string }

export class Api {
  server = '';
  credentials: Credentials | null = null;
  private refreshing: Promise<void> | null = null;
  constructor(private persist: (credentials: Credentials | null) => Promise<void>) {}

  async login(server: string, username: string, password: string) {
    this.server = normalizeServer(server);
    const result = await this.request<{ access_token: string; refresh_token: string; user: import('../shared/types').User }>(
      '/api/auth/login', { method: 'POST', body: JSON.stringify({ username, password }) }, false);
    this.credentials = { access_token: result.access_token, refresh_token: result.refresh_token };
    await this.persist(this.credentials);
    return result.user;
  }

  private async response(path: string, init: RequestInit, authenticated: boolean): Promise<Response> {
    if (!this.server) throw new Error('Укажите адрес сервера.');
    const headers = new Headers(init.headers);
    if (authenticated && this.credentials) headers.set('Authorization', `Bearer ${this.credentials.access_token}`);
    if (typeof init.body === 'string') headers.set('Content-Type', 'application/json');
    let response: Response;
    try {
      response = await fetch(this.server + path, { ...init, headers, redirect: 'error', signal: AbortSignal.timeout(30_000) });
    } catch {
      throw new ApiError(0, 'Нет связи с сервером. Проверьте интернет, адрес сервера и сертификат HTTPS. Локальная копия сохранена.');
    }
    return response;
  }

  private async refresh() {
    if (!this.credentials) throw new ApiError(401, 'Войдите в систему.');
    if (!this.refreshing) {
      this.refreshing = (async () => {
        const response = await this.response('/api/auth/refresh', {
          method: 'POST', body: JSON.stringify({ refresh_token: this.credentials!.refresh_token }),
        }, false);
        if (!response.ok) {
          if (response.status === 401) { this.credentials = null; await this.persist(null); }
          await this.fail(response);
        }
        this.credentials = await response.json() as Credentials;
        await this.persist(this.credentials);
      })().finally(() => { this.refreshing = null; });
    }
    await this.refreshing;
  }

  private async fail(response: Response): Promise<never> {
    let detail = response.status === 413 ? 'Файл превышает допустимый размер.' : 'Не удалось выполнить запрос. Повторите позже.';
    try { const data = await response.json() as { detail?: unknown }; if (typeof data.detail === 'string') detail = data.detail; } catch { /* Proxy HTML is not displayed. */ }
    throw new ApiError(response.status, detail);
  }

  async raw(path: string, init: RequestInit = {}, authenticated = true): Promise<Response> {
    if (authenticated && !this.credentials) throw new ApiError(401, 'Сеанс завершён. Войдите снова.');
    const previousAccess = this.credentials?.access_token;
    let response = await this.response(path, init, authenticated);
    if (response.status === 401 && authenticated) {
      // An in-flight request may fail after another request already rotated the token.
      if (this.credentials?.access_token === previousAccess) await this.refresh();
      response = await this.response(path, init, authenticated);
    }
    if (!response.ok) await this.fail(response);
    return response;
  }

  async request<T = unknown>(path: string, init: RequestInit = {}, authenticated = true): Promise<T> {
    const response = await this.raw(path, init, authenticated);
    try { return await response.json() as T; }
    catch { throw new ApiError(502, 'Сервер вернул некорректный ответ. Повторите позже или обратитесь к администратору.'); }
  }

  async post<T = unknown>(path: string, data: unknown = {}): Promise<T> {
    return this.request<T>(path, { method: 'POST', body: JSON.stringify(data) });
  }
}
