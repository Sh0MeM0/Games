import { randomBytes, scryptSync, timingSafeEqual, createHash } from 'node:crypto';

export function hashPassword(password) {
  const salt = randomBytes(16).toString('hex');
  const key = scryptSync(password, salt, 64).toString('hex');
  return `scrypt:${salt}:${key}`;
}

export function verifyPassword(password, hash) {
  try {
    const [algorithm, salt, expected] = hash.split(':');
    if (algorithm !== 'scrypt' || !salt || !/^[a-f0-9]{128}$/.test(expected)) return false;
    const actual = scryptSync(password, salt, 64);
    return timingSafeEqual(actual, Buffer.from(expected, 'hex'));
  } catch {
    return false;
  }
}

export const newToken = () => randomBytes(32).toString('base64url');
export const tokenHash = (token) => createHash('sha256').update(token).digest('hex');

export function parseCookies(value = '') {
  const cookies = Object.create(null);
  for (const part of value.split(';')) {
    const index = part.indexOf('=');
    if (index < 0) continue;
    try { cookies[part.slice(0, index).trim()] = decodeURIComponent(part.slice(index + 1).trim()); } catch { /* Ignore malformed cookies. */ }
  }
  return cookies;
}
