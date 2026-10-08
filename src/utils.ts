import type { Priority, Task, TaskStatus, User } from './types';

export const statusLabels: Record<TaskStatus, string> = { todo: 'К выполнению', in_progress: 'В работе', review: 'На проверке', done: 'Готово' };
export const priorityLabels: Record<Priority, string> = { low: 'Низкий', medium: 'Средний', high: 'Высокий' };
export const statusColors: Record<TaskStatus, string> = { todo: '#9a9eb5', in_progress: '#7464e8', review: '#efa853', done: '#36a986' };
export function initials(name: string) { return name.trim().split(/\s+/).slice(0, 2).map(word => word[0] || '').join('').toUpperCase(); }
export function formatDuration(seconds: number) {
  const minutes = Math.floor(Math.max(0, seconds) / 60);
  return `${Math.floor(minutes / 60)} ч ${minutes % 60} мин`;
}
export function formatDate(value: string | null, options?: Intl.DateTimeFormatOptions) {
  if (!value) return 'Без срока';
  return new Date(value.length === 10 ? value + 'T12:00:00' : value).toLocaleDateString('ru-RU', options || { day: 'numeric', month: 'short' });
}
export function getUserName(id: string, users: User[]) { return users.find(user => user.id === id)?.name || 'Участник'; }
export function taskCanEdit(task: Task, user: User) { return user.role === 'admin' || task.createdBy === user.id || task.assigneeIds.includes(user.id); }
