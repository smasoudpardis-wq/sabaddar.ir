'use client';
import { useEffect, useState } from 'react';
import { codeOf, MESSAGES, postJson } from '@/lib/client';

type Approval = { id: string; action: string; reason: string | null; status: string; context: { condition: string; value: string | null; limit: string | null } };

const ACTIONS: Record<string, string> = {
  'pos.discount.apply': 'تخفیف دستی', 'sales.return.create': 'برگشت از فروش', 'payments.refund': 'بازپرداخت',
  'receivables.writeoff': 'سوخت مطالبات', 'inventory.adjust': 'تعدیل موجودی', 'catalog.price.manage': 'تغییر قیمت',
};

/** US-026 / IAM-007: requests waiting for my approval (remote approval from the manager's own device). */
export default function ApprovalsPage() {
  const [items, setItems] = useState<Approval[] | null>(null);
  const [done, setDone] = useState<Record<string, string>>({});
  const [error, setError] = useState('');

  async function load() {
    const r = await fetch('/bff/approvals', { cache: 'no-store' });
    if (!r.ok) return setError(await codeOf(r));
    setItems(await r.json());
  }
  useEffect(() => { void load(); }, []);

  async function decide(id: string, decision: 'Approve' | 'Reject') {
    setError('');
    const r = await postJson(`/bff/approvals/${id}/decision`, { decision });
    if (r.ok) return setDone({ ...done, [id]: decision === 'Approve' ? 'تأیید شد' : 'رد شد' });
    const code = await codeOf(r);
    if (code === 'step-up-required') {
      // a recent second factor is required to decide (≤ 15 minutes)
      window.location.assign('/auth/login?' + new URLSearchParams({ acr: 'mfa', returnTo: '/approvals' }));
      return;
    }
    setError(code);
  }

  if (!items) return error ? <p role="alert">{MESSAGES[error] ?? error}</p> : <p>در حال بارگذاری…</p>;
  return (
    <section>
      <h1>درخواست‌های تأیید</h1>
      {items.length === 0 && <p data-testid="no-approvals">درخواستی منتظر تأیید شما نیست.</p>}
      <ul data-testid="approvals">
        {items.map((a) => (
          <li key={a.id}>
            {ACTIONS[a.action] ?? a.action}: {a.context.value ?? ''} (سقف {a.context.limit ?? '—'}){a.reason ? ` — ${a.reason}` : ''}
            {done[a.id]
              ? <strong data-testid={`decided-${a.id}`}> {done[a.id]}</strong>
              : <>
                  {' '}<button data-testid={`approve-${a.id}`} onClick={() => void decide(a.id, 'Approve')} style={{ minHeight: 44 }}>تأیید</button>
                  {' '}<button data-testid={`reject-${a.id}`} onClick={() => void decide(a.id, 'Reject')} style={{ minHeight: 44 }}>رد</button>
                </>}
          </li>
        ))}
      </ul>
      {error && <p role="alert" data-testid="approval-error">{MESSAGES[error] ?? error}</p>}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
