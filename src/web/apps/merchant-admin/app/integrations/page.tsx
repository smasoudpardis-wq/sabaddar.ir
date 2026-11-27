'use client';
import { useEffect, useState } from 'react';
import { codeOf, deleteWithCsrf, postJson } from '@/lib/client';

type Key = { id: string; name: string; prefix: string; scopes: string[]; createdAt: string; expiresAt: string; lastUsedAt: string | null; status: string };

const SCOPES: [string, string][] = [['catalog.read', 'خواندن کالاها و قیمت‌ها'], ['inventory.read', 'خواندن موجودی (بدون بهای تمام‌شده)']];
const STATUS: Record<string, string> = { Active: 'فعال', Expired: 'منقضی', Revoked: 'ابطال‌شده' };
const ERRORS: Record<string, string> = {
  validation: 'نام، دست‌کم یک دسترسی و تاریخ انقضا (حداکثر یک سال) لازم است.',
  forbidden: 'نقش شما اجازه مدیریت کلیدها را نمی‌دهد.',
  'not-found': 'این کلید پیدا نشد یا قبلاً ابطال شده است.',
};
const fa = (d: string) => new Date(d).toLocaleDateString('fa-IR');

/** US-162 / INT-001: API keys for a partner (an online shop, an accounting app): scoped, expiring, revocable, shown once. */
export default function IntegrationsPage() {
  const [keys, setKeys] = useState<Key[] | null>(null);
  const [name, setName] = useState('');
  const [scopes, setScopes] = useState<string[]>(['catalog.read']);
  const [days, setDays] = useState('90');
  const [secret, setSecret] = useState('');
  const [error, setError] = useState('');

  async function load() {
    const r = await fetch('/bff/integrations/keys', { cache: 'no-store' });
    if (!r.ok) { setError(await codeOf(r)); setKeys([]); return; }
    setKeys((await r.json()).items);
  }
  useEffect(() => { void load(); }, []);

  function stepUp() {
    window.location.assign('/auth/login?' + new URLSearchParams({ acr: 'mfa', returnTo: '/integrations' }));
  }

  async function create(e: React.FormEvent) {
    e.preventDefault();
    setError(''); setSecret('');
    const expiresAt = new Date(Date.now() + Number(days) * 86_400_000).toISOString();
    const r = await postJson('/bff/integrations/keys', { name, scopes, expiresAt });
    if (r.status !== 201) {
      const code = await codeOf(r);
      if (code === 'step-up-required' || code === 'mfa-required') return stepUp();
      return setError(code);
    }
    setSecret((await r.json()).secret);
    setName('');
    await load();
  }

  async function revoke(k: Key) {
    if (!window.confirm(`کلید «${k.name}» ابطال شود؟ برنامه‌ای که از آن استفاده می‌کند بلافاصله قطع می‌شود.`)) return;
    setError('');
    const r = await deleteWithCsrf(`/bff/integrations/keys/${k.id}`);
    if (r.status !== 204) {
      const code = await codeOf(r);
      if (code === 'step-up-required' || code === 'mfa-required') return stepUp();
      return setError(code);
    }
    await load();
  }

  if (!keys) return <p>در حال بارگذاری…</p>;
  return (
    <section>
      <h1>کلیدهای اتصال (API)</h1>
      <p>با کلید اتصال، برنامه‌ای مثل فروشگاه اینترنتی می‌تواند کالاها یا موجودی همین فروشگاه را بخواند. کلید فقط یک بار نمایش داده می‌شود.</p>
      <form onSubmit={(e) => void create(e)} data-testid="key-form">
        <label>نام <input name="keyName" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} /></label>
        <fieldset>
          <legend>دسترسی‌ها</legend>
          {SCOPES.map(([s, label]) => (
            <label key={s}>
              <input type="checkbox" name={s} checked={scopes.includes(s)}
                onChange={(e) => setScopes((x) => (e.target.checked ? [...x, s] : x.filter((y) => y !== s)))} /> {label}
            </label>
          ))}
        </fieldset>
        <label>اعتبار
          <select name="days" value={days} onChange={(e) => setDays(e.target.value)}>
            <option value="30">۳۰ روز</option><option value="90">۹۰ روز</option><option value="180">۱۸۰ روز</option><option value="365">یک سال</option>
          </select>
        </label>
        <button type="submit" data-testid="create-key" style={{ minHeight: 44 }}>ساخت کلید</button>
      </form>
      {secret && (
        <div role="status" data-testid="new-key">
          <p>کلید را همین حالا کپی کنید؛ دوباره نمایش داده نمی‌شود:</p>
          <code dir="ltr" data-testid="key-secret">{secret}</code>{' '}
          <button type="button" onClick={() => void navigator.clipboard?.writeText(secret)}>کپی</button>{' '}
          <button type="button" onClick={() => setSecret('')} data-testid="hide-key">پنهان کردن</button>
        </div>
      )}
      {error && <p role="alert" data-testid="key-error">{ERRORS[error] ?? error}</p>}
      <table data-testid="keys">
        <thead><tr><th>نام</th><th>شناسه</th><th>دسترسی</th><th>انقضا</th><th>آخرین استفاده</th><th>وضعیت</th><th /></tr></thead>
        <tbody>
          {keys.map((k) => (
            <tr key={k.id} data-testid={`key-${k.name}`}>
              <td>{k.name}</td>
              <td dir="ltr">rck_{k.prefix}_…</td>
              <td>{k.scopes.map((s) => SCOPES.find(([x]) => x === s)?.[1] ?? s).join('، ')}</td>
              <td>{fa(k.expiresAt)}</td>
              <td>{k.lastUsedAt ? fa(k.lastUsedAt) : '—'}</td>
              <td data-testid={`key-status-${k.name}`}>{STATUS[k.status] ?? k.status}</td>
              <td>{k.status === 'Active' && <button onClick={() => void revoke(k)} data-testid={`revoke-key-${k.name}`}>ابطال</button>}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
