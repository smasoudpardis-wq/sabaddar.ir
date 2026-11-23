'use client';
import { useEffect, useState } from 'react';
import { codeOf, deleteWithCsrf, MESSAGES, postJson } from '@/lib/client';

type Request = {
  id: string; requestedBy: string; reason: string; durationMinutes: number;
  status: 'Pending' | 'Approved' | 'Rejected' | 'Expired' | 'Revoked'; requestedAt: string; grantedUntil: string | null;
};

const STATUS: Record<Request['status'], string> = {
  Pending: 'منتظر تصمیم شما', Approved: 'فعال', Rejected: 'رد شد', Expired: 'منقضی', Revoked: 'پایان داده شد',
};
const time = (iso: string) => new Date(iso).toLocaleString('fa-IR');

/** US-027 / IAM-008: platform support asks for time-limited access; only the owner decides and may end it early. */
export default function SupportAccessPage() {
  const [items, setItems] = useState<Request[] | null>(null);
  const [error, setError] = useState('');

  async function load() {
    const r = await fetch('/bff/support-access', { cache: 'no-store' });
    if (!r.ok) return setError(await codeOf(r));
    setItems(await r.json());
  }
  useEffect(() => { void load(); }, []);

  async function act(r: Response) {
    if (r.ok) return void load();
    const code = await codeOf(r);
    if (code === 'step-up-required' || code === 'mfa-required') {
      window.location.assign('/auth/login?' + new URLSearchParams({ acr: 'mfa', returnTo: '/support-access' }));
      return;
    }
    setError(code);
  }

  if (!items) return error ? <p role="alert">{MESSAGES[error] ?? error}</p> : <p>در حال بارگذاری…</p>;
  return (
    <section>
      <h1>دسترسی پشتیبانی</h1>
      <p>پشتیبانی فقط با تأیید مالک و حداکثر ۴ ساعت به فروشگاه دسترسی دارد؛ همه کارهایش با برچسب پشتیبانی ممیزی می‌شود.</p>
      {items.length === 0 && <p data-testid="no-support-requests">درخواستی ثبت نشده است.</p>}
      <ul data-testid="support-requests">
        {items.map((q) => (
          <li key={q.id} data-testid={`support-${q.id}`} data-status={q.status}>
            {time(q.requestedAt)} — {q.reason} ({q.durationMinutes.toLocaleString('fa-IR')} دقیقه) — <strong>{STATUS[q.status]}</strong>
            {q.status === 'Approved' && q.grantedUntil && <> تا {time(q.grantedUntil)}</>}
            {q.status === 'Pending' && <>
              {' '}<button data-testid={`support-approve-${q.id}`} style={{ minHeight: 44 }}
                onClick={async () => act(await postJson(`/bff/support-access/${q.id}/decision`, { decision: 'Approve' }))}>تأیید</button>
              {' '}<button data-testid={`support-reject-${q.id}`} style={{ minHeight: 44 }}
                onClick={async () => act(await postJson(`/bff/support-access/${q.id}/decision`, { decision: 'Reject' }))}>رد</button>
            </>}
            {q.status === 'Approved' && <>
              {' '}<button data-testid={`support-revoke-${q.id}`} style={{ minHeight: 44 }}
                onClick={async () => act(await deleteWithCsrf(`/bff/support-access/${q.id}`))}>پایان دسترسی</button>
            </>}
          </li>
        ))}
      </ul>
      {error && <p role="alert" data-testid="support-error">{MESSAGES[error] ?? error}</p>}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
