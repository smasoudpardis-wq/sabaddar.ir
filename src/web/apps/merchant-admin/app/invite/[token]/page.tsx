'use client';
import { use, useEffect, useState } from 'react';
import { codeOf, MESSAGES, postJson } from '@/lib/client';

/** US-025 / IAM-001: accept an invitation. Signs in first if needed, then joins and opens the store. */
export default function InvitePage({ params }: { params: Promise<{ token: string }> }) {
  const { token } = use(params);
  const [state, setState] = useState<'loading' | 'anonymous' | 'ready' | 'joining'>('loading');
  const [error, setError] = useState('');

  useEffect(() => {
    void (async () => {
      const r = await fetch('/bff/me', { cache: 'no-store' });
      setState(r.status === 401 ? 'anonymous' : 'ready'); // 403 user-not-provisioned is fine: accepting provisions the user
    })();
  }, []);

  async function accept() {
    setState('joining'); setError('');
    const r = await postJson(`/bff/invitations/${token}/accept`);
    if (!r.ok) { setState('ready'); return setError(await codeOf(r)); }
    const joined = (await r.json()) as { tenantId: string; tenantName: string; roles: string[] };
    const sel = await postJson('/bff/active-tenant', { tenantId: joined.tenantId });
    if (sel.status === 403 && (await codeOf(sel)) === 'mfa-required') {
      window.location.assign('/auth/login?' + new URLSearchParams({ acr: 'mfa', tenant: joined.tenantId, returnTo: '/' }));
      return;
    }
    window.location.assign('/');
  }

  if (state === 'loading') return <p>در حال بارگذاری…</p>;
  if (state === 'anonymous')
    return (
      <section>
        <h1>دعوت به فروشگاه</h1>
        <p>برای پذیرش دعوت ابتدا وارد شوید.</p>
        <a data-testid="invite-login" href={'/auth/login?' + new URLSearchParams({ returnTo: `/invite/${token}` })}>ورود</a>
      </section>
    );
  return (
    <section>
      <h1>دعوت به فروشگاه</h1>
      <p>با پذیرش، با نقشی که مدیر تعیین کرده به فروشگاه می‌پیوندید.</p>
      <button data-testid="invite-accept" disabled={state === 'joining'} onClick={() => void accept()} style={{ minHeight: 44 }}>پذیرش دعوت</button>
      {error && <p role="alert" data-testid="invite-error">{MESSAGES[error] ?? error}</p>}
    </section>
  );
}
