export class ApiError extends Error {
  status: number;
  constructor(message: string, status: number) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

export async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  const headers = new Headers(options.headers);
  headers.set('X-Potok-Timezone', Intl.DateTimeFormat().resolvedOptions().timeZone);
  const response = await fetch(path, { ...options, headers, credentials: 'same-origin' });
  const data = await response.json().catch(() => null);
  if (!response.ok) {
    throw new ApiError(data?.error || `Не удалось выполнить запрос (${response.status})`, response.status);
  }
  return data as T;
}

export function json(method: string, body?: unknown): RequestInit {
  return { method, headers: { 'Content-Type': 'application/json' }, ...(body === undefined ? {} : { body: JSON.stringify(body) }) };
}
