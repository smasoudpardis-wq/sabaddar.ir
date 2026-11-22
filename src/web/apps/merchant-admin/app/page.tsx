'use client';
import { useCallback, useEffect, useState } from 'react';

type Membership = { tenantId: string; tenantName: string };
type Me = { displayName: string; memberships: Membership[]; activeTenantId: string | null };
type Tenant = { id: string; legalName: string; status: string };

const codeOf = async (r: Response) => ((await r.json().catch(() => null)) as { code?: string } | null)?.code ?? `http-${r.status}`;
const csrf = () => document.cookie.split('; ').find((c) => c.startsWith('rc_csrf=') || c.startsWith('__Host-rc_csrf='))?.split('=')[1] ?? '';

export default function Home() {
  const [me, setMe] = useState<Me | null | 'anonymous'>(null);
  const [tenant, setTenant] = useState<Tenant | null>(null);
  const [error, setError] = useState<string>('');

  const load = useCallback(async () => {
    const r = await fetch('/bff/me', { cache: 'no-store' });
    if (r.status === 401) return setMe('anonymous');
    if (!r.ok) return setError(await codeOf(r));
    const data: Me = await r.json();
    setMe(data);
    if (data.activeTenantId) {
      const t = await fetch('/bff/tenant', { cache: 'no-store' });
      setTenant(t.ok ? await t.json() : null);
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  async function select(tenantId: string) {
    const r = await fetch('/bff/active-tenant', {
      method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() }, body: JSON.stringify({ tenantId }),
    });
    if (r.status !== 204) return setError(await codeOf(r));
    await load();
  }

  async function logout() {
    await fetch('/auth/logout', { method: 'POST', headers: { 'x-csrf-token': csrf() } });
    setTenant(null);
    setMe('anonymous');
  }

  if (me === null) return <p>در حال بارگذاری…</p>;
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
      <h2>فروشگاه‌های من</h2>
      <ul>
        {me.memberships.map((m) => (
          <li key={m.tenantId}>
            <button data-testid={`select-${m.tenantId}`} onClick={() => void select(m.tenantId)} style={{ minHeight: 44 }}>
              {m.tenantName}
            </button>
          </li>
        ))}
      </ul>
      {tenant && <p data-testid="active-tenant">فروشگاه فعال: {tenant.legalName}</p>}
      {error && <p role="alert" data-testid="error">{error}</p>}
      <button onClick={() => void logout()} data-testid="logout" style={{ minHeight: 44 }}>خروج</button>
    </section>
  );
}
