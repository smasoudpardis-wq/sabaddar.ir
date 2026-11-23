'use client';
import { useCallback, useEffect, useState } from 'react';
import { Onboarding } from './onboarding';

type Membership = { tenantId: string; tenantName: string; roles: string[]; mfaRequired: boolean };
type Me = { displayName: string; memberships: Membership[]; activeTenantId: string | null; mfaSatisfied: boolean; terminal: { id: string } | null };
type Tenant = {
  id: string; legalName: string; status: string; plan: string; trialEndsAt: string | null;
  entitlements: { maxBranches: number | null; maxRegisters: number | null; maxUsers: number | null };
  readOnlyReason: 'suspended' | 'closed' | 'trial-expired' | null;
};
const READ_ONLY: Record<string, string> = {
  suspended: 'حساب فروشگاه تعلیق شده است',
  closed: 'حساب فروشگاه بسته شده است',
  'trial-expired': 'دوره آزمایشی به پایان رسیده است',
};
const PLANS: Record<string, string> = { Trial: 'آزمایشی', Basic: 'پایه', Pro: 'حرفه‌ای', Enterprise: 'سازمانی' };
const cap = (n: number | null) => (n === null ? 'نامحدود' : n.toLocaleString('fa-IR'));

const MESSAGES: Record<string, string> = { // D09 §10.1
  'mfa-not-completed': 'تأیید مرحله دوم انجام نشد.',
  'upstream-unavailable': 'سرویس موقتاً در دسترس نیست. چند لحظه بعد دوباره تلاش کنید.',
  'not-found': 'یافت نشد.',
  csrf: 'نشست شما منقضی شده یا درخواست از صفحه‌ی دیگری آمده است. صفحه را دوباره باز کنید.',
};
const codeOf = async (r: Response) => ((await r.json().catch(() => null)) as { code?: string } | null)?.code ?? `http-${r.status}`;
const csrf = () => document.cookie.split('; ').find((c) => c.startsWith('rc_csrf=') || c.startsWith('__Host-rc_csrf='))?.split('=')[1] ?? '';
/** US-022: the API asked for a second factor; Keycloak handles TOTP/passkey (and enrolment if none exists). */
const stepUp = (tenantId?: string) => {
  const q = new URLSearchParams({ acr: 'mfa', returnTo: '/' });
  if (tenantId) q.set('tenant', tenantId);
  window.location.assign(`/auth/login?${q}`);
};

export default function Home() {
  const [me, setMe] = useState<Me | null | 'anonymous' | 'new-user' | 'locked'>(null);
  const [pin, setPin] = useState('');
  const [tenant, setTenant] = useState<Tenant | null>(null);
  const [error, setError] = useState<string>('');

  const load = useCallback(async () => {
    const r = await fetch('/bff/me', { cache: 'no-store' });
    if (r.status === 401) return setMe('anonymous');
    if (!r.ok) {
      const code = await codeOf(r);
      if (code === 'user-not-provisioned') return setMe('new-user'); // first login: sign-up wizard (UC-10)
      return setError(code);
    }
    const data = await r.json();
    if (data.locked) return setMe('locked');
    setMe(data as Me);
    if (data.activeTenantId) {
      const t = await fetch('/bff/tenant', { cache: 'no-store' });
      if (t.ok) return setTenant(await t.json());
      const code = await codeOf(t);
      if (code === 'mfa-required' || code === 'step-up-required') return stepUp(data.activeTenantId);
      setTenant(null);
    }
  }, []);

  useEffect(() => {
    if (new URLSearchParams(window.location.search).get('onboarding') === 'resume') return setMe('new-user');
    void load();
  }, [load]);

  async function select(tenantId: string) {
    setError('');
    const r = await fetch('/bff/active-tenant', {
      method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() }, body: JSON.stringify({ tenantId }),
    });
    if (r.status === 204) return load();
    const code = await codeOf(r);
    if (code === 'mfa-required') return stepUp(tenantId);
    setError(code);
  }

  async function logout() {
    await fetch('/auth/logout', { method: 'POST', headers: { 'x-csrf-token': csrf() } });
    setTenant(null);
    setMe('anonymous');
  }

  async function lock() {
    await fetch('/bff/lock', { method: 'POST', headers: { 'x-csrf-token': csrf() } });
    setMe('locked');
  }

  async function unlock(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    const r = await fetch('/bff/unlock', { method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() }, body: JSON.stringify({ pin }) });
    setPin('');
    if (r.status === 204) return load();
    const body = await r.json().catch(() => ({}));
    if (body.code === 'pin-locked') { setMe('anonymous'); return setError(MESSAGES['pin-locked']); }
    setError(body.code === 'pin-invalid' ? `PIN اشتباه است. ${body.attemptsLeft} تلاش باقی مانده.` : MESSAGES[body.code] ?? body.code);
  }

  if (me === null) return <p>در حال بارگذاری…</p>;
  if (me === 'locked')
    return (
      <form onSubmit={(e) => void unlock(e)} data-testid="lock-screen">
        <h1>صندوق قفل است</h1>
        <input name="pin" inputMode="numeric" autoComplete="off" pattern="[0-9]{4,6}" maxLength={6} required value={pin}
          onChange={(e) => setPin(e.target.value)} aria-label="PIN" style={{ fontSize: 24, letterSpacing: 8 }} />
        <button type="submit" data-testid="unlock" style={{ minHeight: 44 }}>باز کردن</button>
        {error && <p role="alert" data-testid="lock-error">{error}</p>}
      </form>
    );
  if (me === 'new-user') return <Onboarding />;
  if (me === 'anonymous')
    return (
      <section>
        <h1>RetailCloud</h1>
        <a href="/auth/login" data-testid="login">ورود</a>
      </section>
    );
  return (
    <section>
      <h1 data-testid="welcome">خوش آمدید، {me.displayName}</h1>
      <p data-testid="mfa-state">{me.mfaSatisfied ? 'ورود دومرحله‌ای: تأییدشده' : 'ورود با رمز عبور'}</p>
      <h2>فروشگاه‌های من</h2>
      {me.memberships.length === 0 && <button onClick={() => setMe('new-user')} data-testid="new-business">ثبت کسب‌وکار جدید</button>}
      <ul>
        {me.memberships.map((m) => (
          <li key={m.tenantId}>
            <button data-testid={`select-${m.tenantId}`} onClick={() => void select(m.tenantId)} style={{ minHeight: 44 }}>
              {m.tenantName}
            </button>
            {m.mfaRequired && <small> (نیازمند ورود دومرحله‌ای)</small>}
          </li>
        ))}
      </ul>
      {tenant && <p data-testid="active-tenant">فروشگاه فعال: {tenant.legalName}</p>}
      {tenant?.readOnlyReason && (
        <p role="status" data-testid="read-only-banner">
          {READ_ONLY[tenant.readOnlyReason]}؛ فروشگاه فقط‌خواندنی است. اطلاعات، گزارش‌ها و خروجی در دسترس‌اند، ولی ثبت جدید ممکن نیست.
          برای تمدید یا رفع تعلیق با پشتیبانی تماس بگیرید.
        </p>
      )}
      {tenant && (
        <p data-testid="plan">
          پلن: {PLANS[tenant.plan] ?? tenant.plan}
          {tenant.trialEndsAt && <> · پایان دوره آزمایشی: {new Date(tenant.trialEndsAt).toLocaleDateString('fa-IR')}</>}
          {' '}· سقف: {cap(tenant.entitlements.maxBranches)} شعبه، {cap(tenant.entitlements.maxRegisters)} صندوق، {cap(tenant.entitlements.maxUsers)} کاربر
        </p>
      )}
      {error && <p role="alert" data-testid="error">{MESSAGES[error] ?? error}</p>}
      {tenant && <p><a href="/users" data-testid="users-link">کاربران و دعوت همکار</a> · <a href="/branches" data-testid="branches-link">شعب</a> · <a href="/terminals" data-testid="terminals-link">دستگاه‌ها</a> · <a href="/approvals" data-testid="approvals-link">تأییدها</a> · <a href="/audit" data-testid="audit-link">سابقه ممیزی</a> · <a href="/exports" data-testid="exports-link">خروجی داده‌ها</a> · <a href="/support-access" data-testid="support-link">دسترسی پشتیبانی</a></p>}
      {me.terminal && <button onClick={() => void lock()} data-testid="lock" style={{ minHeight: 44 }}>قفل صندوق</button>}
      <button onClick={() => void logout()} data-testid="logout" style={{ minHeight: 44 }}>خروج</button>
    </section>
  );
}
