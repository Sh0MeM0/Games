import { useEffect, useState } from 'react';
import { LockKeyhole, UserRound, X } from 'lucide-react';
import { api, json } from '../api';
import type { User } from '../types';

export default function ProfileModal({ user, onClose, onRefresh }: { user: User; onClose: () => void; onRefresh: () => Promise<void> }) {
  const [name, setName] = useState(user.name);
  const [title, setTitle] = useState(user.title);
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    const listener = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose(); };
    document.addEventListener('keydown', listener);
    return () => document.removeEventListener('keydown', listener);
  }, [onClose]);
  async function save(event: React.FormEvent) {
    event.preventDefault(); setBusy(true); setError('');
    try {
      await api('/api/profile', json('PATCH', { name, title, ...(newPassword ? { currentPassword, newPassword } : {}) }));
      await onRefresh(); onClose();
    } catch (err) { setError(err instanceof Error ? err.message : 'Не удалось сохранить профиль'); }
    finally { setBusy(false); }
  }
  return <div className="modal-backdrop" onMouseDown={event => { if (event.target === event.currentTarget) onClose(); }}>
    <section className="modal" role="dialog" aria-modal="true" aria-labelledby="profile-title">
      <div className="modal-header"><h2 id="profile-title"><UserRound size={21} /> Мой профиль</h2><button className="icon-button" onClick={onClose} aria-label="Закрыть"><X size={20}/></button></div>
      <form onSubmit={save}>
        <div className="modal-body">
          <label className="form-field"><span className="field-label">Имя и фамилия</span><input className="input" autoFocus required maxLength={100} value={name} onChange={event => setName(event.target.value)} /></label>
          <label className="form-field"><span className="field-label">Должность</span><input className="input" maxLength={100} value={title} onChange={event => setTitle(event.target.value)} /></label>
          <label className="form-field"><span className="field-label">Электронная почта</span><input className="input" disabled value={user.email} /></label>
          <p className="muted" style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 24 }}><LockKeyhole size={16} /> Смена пароля — по желанию</p>
          <label className="form-field"><span className="field-label">Текущий пароль</span><input className="input" type="password" autoComplete="current-password" value={currentPassword} onChange={event => setCurrentPassword(event.target.value)} required={Boolean(newPassword)} /></label>
          <label className="form-field"><span className="field-label">Новый пароль</span><input className="input" type="password" autoComplete="new-password" minLength={12} placeholder="Не менее 12 символов" value={newPassword} onChange={event => setNewPassword(event.target.value)} /></label>
          {error && <p className="error-message" role="alert">{error}</p>}
        </div>
        <div className="modal-footer"><button type="button" className="button button-secondary" onClick={onClose}>Отмена</button><button className="button button-primary" disabled={busy}>{busy ? 'Сохраняем…' : 'Сохранить изменения'}</button></div>
      </form>
    </section>
  </div>;
}
