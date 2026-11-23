'use client';
import { useEffect, useState } from 'react';
import { codeOf, deleteWithCsrf, MESSAGES, postJson } from '@/lib/client';

type Item = { handle: string; device: string; createdAt: number; lastSeen: number; mfa: boolean; current: boolean };

const when = (epoch: number) => new Date(epoch * 1000).toLocaleString('fa-IR');

/** IAM-005 / US-024: where I am signed in; sign another device out, or every device including this one. */
export default function SessionsPage() {
  const [items, setItems] = useState<Item[] | null>(null);
  const [error, setError] = useState('');

  async function load() {
    const r = await fetch('/bff/sessions', { cache: 'no-store' });
    if (r.status === 401) return void (window.location.href = '/');
    if (!r.ok) return setError(await codeOf(r));
    setItems((await r.json()).items);
  }
  useEffect(() => { void load(); }, []);

  async function end(handle: string) {
    setError('');
    const r = await deleteWithCsrf(`/bff/sessions/${handle}`);
    if (r.status !== 204) setError(await codeOf(r));
    await load();
  }

  async function endAll() {
    if (!window.confirm('از همه دستگاه‌ها، از جمله همین دستگاه، خارج می‌شوید. ادامه می‌دهید؟')) return;
    await postJson('/bff/sessions/logout-all');
    window.location.href = '/';
  }

  if (error && !items) return <p role="alert" data-testid="sessions-error">{MESSAGES[error] ?? error}</p>;
  if (!items) return <p>در حال بارگذاری…</p>;
  return (
    <section>
      <h1>دستگاه‌های واردشده</h1>
      <ul data-testid="sessions">
        {items.map((s) => (
          <li key={s.handle} data-testid="session" data-current={s.current ? 'yes' : 'no'}>
            {s.device || 'دستگاه'} — ورود: {when(s.createdAt)} — آخرین استفاده: {when(s.lastSeen)}{s.mfa ? ' — با تأیید دومرحله‌ای' : ''}
            {s.current
              ? <strong> (همین دستگاه)</strong>
              : <> <button type="button" style={{ minHeight: 44 }} data-testid={`session-end-${s.handle}`} onClick={() => void end(s.handle)}>خروج این دستگاه</button></>}
          </li>
        ))}
      </ul>
      <button type="button" style={{ minHeight: 44 }} data-testid="logout-all" onClick={() => void endAll()}>خروج از همه دستگاه‌ها</button>
      {error && <p role="alert" data-testid="sessions-error">{MESSAGES[error] ?? error}</p>}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
