'use client';
import { useEffect, useState } from 'react';

// UC-10 wizard (D09). Kept in sessionStorage across the MFA redirect: the form and its Idempotency-Key.
const DRAFT = 'rc.onboarding.draft';
type Draft = { key: string; legalName: string; industryCode: string; firstBranchName: string };
type Step = { name: string; complete: boolean };

const STEP_LABELS: Record<string, string> = {
  'branch-and-register': 'شعبه و صندوق اول',
  warehouse: 'انبار اول',
  'chart-of-accounts': 'شجره حساب‌ها',
  'fiscal-periods': 'دوره‌های مالی سال جاری',
};
const ERRORS: Record<string, string> = { // D09 §10.1
  validation: 'اطلاعات واردشده معتبر نیست.',
  'idempotency-key-reused': 'این فرم قبلاً با اطلاعات دیگری ارسال شده است. صفحه را دوباره باز کنید.',
  'upstream-unavailable': 'سرویس موقتاً در دسترس نیست. چند لحظه بعد دوباره تلاش کنید.',
};

const csrf = () => document.cookie.split('; ').find((c) => c.startsWith('rc_csrf=') || c.startsWith('__Host-rc_csrf='))?.split('=')[1] ?? '';
const load = (): Draft | null => { try { return JSON.parse(sessionStorage.getItem(DRAFT) ?? 'null'); } catch { return null; } };
const save = (d: Draft | null) => { try { d ? sessionStorage.setItem(DRAFT, JSON.stringify(d)) : sessionStorage.removeItem(DRAFT); } catch { /* private mode */ } };
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

export function Onboarding() {
  const [draft, setDraft] = useState<Draft>(() => load() ?? { key: crypto.randomUUID(), legalName: '', industryCode: 'supermarket', firstBranchName: 'شعبه مرکزی' });
  const [phase, setPhase] = useState<'form' | 'submitting' | 'provisioning' | 'ready'>('form');
  const [steps, setSteps] = useState<Step[]>([]);
  const [error, setError] = useState('');
  const [fieldErrors, setFieldErrors] = useState<string[]>([]);

  async function submit(d: Draft) {
    setError(''); setFieldErrors([]); setPhase('submitting'); save(d);
    const r = await fetch('/bff/tenants', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-csrf-token': csrf(), 'idempotency-key': d.key },
      body: JSON.stringify({ legalName: d.legalName, industryCode: d.industryCode, firstBranchName: d.firstBranchName }),
    });
    const body = await r.json().catch(() => ({}));
    if (r.status === 403 && body.code === 'mfa-required') {
      // the owner role needs a second factor (IAM-003); Keycloak enrols TOTP or a passkey, then we come back here
      window.location.assign('/auth/login?' + new URLSearchParams({ acr: 'mfa', returnTo: '/?onboarding=resume' }));
      return;
    }
    if (r.status !== 201) {
      setPhase('form');
      setFieldErrors(Object.keys(body.errors ?? {}));
      return setError(ERRORS[body.code] ?? body.code ?? `http-${r.status}`);
    }
    await provision(body.id as string);
  }

  async function provision(tenantId: string) {
    setPhase('provisioning');
    for (let i = 0; i < 60; i++) { // membership is created by the Workers from platform.tenant-registered
      const me = await fetch('/bff/me', { cache: 'no-store' });
      const data = me.ok ? await me.json() : null;
      if (data?.memberships?.some((m: { tenantId: string }) => m.tenantId === tenantId)) break;
      await sleep(500);
    }
    const sel = await fetch('/bff/active-tenant', {
      method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() }, body: JSON.stringify({ tenantId }),
    });
    if (sel.status !== 204) { setPhase('form'); return setError((await sel.json().catch(() => ({}))).code ?? 'error'); }
    for (let i = 0; i < 60; i++) {
      const s = await fetch('/bff/tenant-setup', { cache: 'no-store' });
      if (s.ok) {
        const st = await s.json();
        setSteps(st.steps);
        if (st.complete) { save(null); setPhase('ready'); return; }
      }
      await sleep(500);
    }
    setError('upstream-unavailable');
  }

  useEffect(() => {
    // returning from the MFA step: resubmit the same draft with the same key (replays if it already went through)
    if (new URLSearchParams(window.location.search).get('onboarding') === 'resume') {
      const d = load();
      if (d) void submit(d);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (phase === 'ready')
    return (
      <section>
        <h1 data-testid="onboarding-ready">فروشگاه شما آماده است</h1>
        <ul>{steps.map((s) => <li key={s.name}>✓ {STEP_LABELS[s.name] ?? s.name}</li>)}</ul>
        <a href="/" data-testid="go-home">ورود به پنل</a>
      </section>
    );
  if (phase === 'provisioning')
    return (
      <section>
        <h1>در حال آماده‌سازی فروشگاه…</h1>
        <ul data-testid="setup-steps">{steps.map((s) => <li key={s.name}>{s.complete ? '✓' : '…'} {STEP_LABELS[s.name] ?? s.name}</li>)}</ul>
      </section>
    );
  const invalid = (f: string) => (fieldErrors.includes(f) ? { 'aria-invalid': true as const } : {});
  return (
    <form onSubmit={(e) => { e.preventDefault(); void submit(draft); }} data-testid="onboarding-form">
      <h1>ثبت‌نام کسب‌وکار</h1>
      <label>نام فروشگاه
        <input name="legalName" required minLength={2} maxLength={200} value={draft.legalName} {...invalid('legalName')}
          onChange={(e) => setDraft({ ...draft, legalName: e.target.value })} />
      </label>
      <label>صنف
        <select name="industryCode" value={draft.industryCode} onChange={(e) => setDraft({ ...draft, industryCode: e.target.value })}>
          <option value="supermarket">سوپرمارکت</option>
          <option value="apparel">پوشاک</option>
          <option value="general">عمومی</option>
        </select>
      </label>
      <label>نام شعبه اول
        <input name="firstBranchName" required maxLength={100} value={draft.firstBranchName} {...invalid('firstBranchName')}
          onChange={(e) => setDraft({ ...draft, firstBranchName: e.target.value })} />
      </label>
      <p>مالک فروشگاه باید ورود دومرحله‌ای داشته باشد؛ در صورت نیاز همین حالا فعال می‌شود.</p>
      <button type="submit" disabled={phase === 'submitting'} data-testid="onboarding-submit" style={{ minHeight: 44 }}>ساخت فروشگاه</button>
      {error && <p role="alert" data-testid="onboarding-error">{error}</p>}
    </form>
  );
}
