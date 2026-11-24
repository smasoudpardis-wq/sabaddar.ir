'use client';
import { useEffect, useState } from 'react';
import { codeOf, csrf, MESSAGES } from '@/lib/client';

type Account = { id: string; code: string; name: string; isPostable: boolean; requiresDetail: boolean };
type Supplier = { id: string; name: string };
type JLine = { lineNo: number; accountId: string; accountCode: string; detailId: string | null; debit: string; credit: string; description: string | null };
type Journal = {
  id: string; status: string; effectiveAt: string; description: string | null; source: { type: string; purpose: string } | null;
  reversalOf: string | null; reversedBy: string | null; lines: JLine[];
};
type Line = { accountId: string; detailId: string; debit: string; credit: string };
type Period = { id: string; fiscalYear: number; periodNo: number; start: string; end: string; status: string; closedAt: string | null; version: number };

const EMPTY: Line = { accountId: '', detailId: '', debit: '', credit: '' };
const digits = (v: string) => v.replace(/[۰-۹]/g, (d) => String('۰۱۲۳۴۵۶۷۸۹'.indexOf(d))).replace(/[,٬\s]/g, '');
const rial = (v: string) => Number(v).toLocaleString('fa-IR');
const STATUS: Record<string, string> = { Draft: 'پیش‌نویس', Posted: 'قطعی', Reversed: 'برگشت‌خورده' };
const ERRORS: Record<string, string> = {
  'journal-not-draft': 'این سند دیگر پیش‌نویس نیست.',
  'journal-already-reversed': 'این سند قبلاً برگشت خورده است.',
  'period-closed': 'دوره مالی این تاریخ بسته است.',
  'no-open-period': 'برای این تاریخ دوره مالی باز وجود ندارد.',
  unbalanced: 'جمع بدهکار و بستانکار برابر نیست.',
  'period-not-ended': 'این دوره هنوز تمام نشده است؛ دوره پس از پایانش بسته می‌شود.',
  'earlier-period-open': 'اول دوره‌های قبلی را ببندید.',
  'period-has-drafts': 'پیش‌نویس‌های این دوره را اول ثبت قطعی یا حذف کنید.',
};

/**
 * US-112 / ACC-003 / ACC-006: the accountant's manual entries. A draft is checked for balance as it is written, posted
 * once, or deleted while still a draft; a posted entry is never changed, only reversed by its mirror image, dated today.
 * US-113 / ACC-009: a period that has ended is closed, in order; nothing is dated in it afterwards and it is never reopened.
 */
export default function JournalsPage() {
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [suppliers, setSuppliers] = useState<Supplier[]>([]);
  const [journals, setJournals] = useState<Journal[] | null>(null);
  const [periods, setPeriods] = useState<Period[]>([]);
  const [description, setDescription] = useState('');
  const [lines, setLines] = useState<Line[]>([{ ...EMPTY }, { ...EMPTY }]);
  const [reason, setReason] = useState<Record<string, string>>({});
  const [error, setError] = useState('');

  async function load() {
    const a = await fetch('/bff/accounting/accounts', { cache: 'no-store' });
    if (a.ok) setAccounts(((await a.json()) as Account[]).filter((x) => x.isPostable));
    const s = await fetch('/bff/suppliers', { cache: 'no-store' });
    if (s.ok) setSuppliers((await s.json()).items);
    const j = await fetch('/bff/accounting/journals?limit=50', { cache: 'no-store' });
    setJournals(j.ok ? (await j.json()).items : null);
    const p = await fetch('/bff/accounting/periods', { cache: 'no-store' });
    if (p.ok) setPeriods(await p.json());
  }
  useEffect(() => { void load(); }, []);

  const account = (id: string) => accounts.find((a) => a.id === id);
  const debit = lines.reduce((t, l) => t + (Number(digits(l.debit)) || 0), 0);
  const credit = lines.reduce((t, l) => t + (Number(digits(l.credit)) || 0), 0);

  async function call(url: string, method: string, body?: unknown) {
    setError('');
    const headers: Record<string, string> = { 'content-type': 'application/json', 'x-csrf-token': csrf() };
    if (method === 'POST') headers['idempotency-key'] = crypto.randomUUID();
    const r = await fetch(url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    if (!r.ok) {
      const p = await r.clone().json().catch(() => null) as { code?: string; errors?: Record<string, string[]>; context?: { errors?: Record<string, string[]> } } | null;
      const lineError = (p?.errors ?? p?.context?.errors)?.lines?.[0];
      setError(lineError?.includes('unbalanced') ? 'unbalanced' : lineError ?? await codeOf(r));
      return false;
    }
    void load();
    return true;
  }

  async function draft(e: React.FormEvent) {
    e.preventDefault();
    const ok = await call('/bff/accounting/journals', 'POST', {
      effectiveAt: new Date().toISOString(), description: description.trim(),
      lines: lines.filter((l) => l.accountId).map((l) => ({
        accountId: l.accountId, detailType: l.detailId ? 'Supplier' : undefined, detailId: l.detailId || undefined,
        debit: digits(l.debit) || undefined, credit: digits(l.credit) || undefined,
      })),
    });
    if (ok) {
      setDescription('');
      setLines([{ ...EMPTY }, { ...EMPTY }]);
    }
  }

  const setLine = (i: number, k: keyof Line) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) =>
    setLines(lines.map((l, j) => (j === i ? { ...l, [k]: e.target.value, ...(k === 'accountId' ? { detailId: '' } : {}) } : l)));

  return (
    <section>
      <h1>اسناد حسابداری</h1>
      <h2>سند دستی تازه</h2>
      <form onSubmit={(e) => void draft(e)} data-testid="journal-form">
        <label>شرح <input required value={description} onChange={(e) => setDescription(e.target.value)} size={40} data-testid="journal-description" /></label>
        {lines.map((l, i) => (
          <p key={i}>
            <label>حساب{' '}
              <select value={l.accountId} onChange={setLine(i, 'accountId')} data-testid={`journal-account-${i}`}>
                <option value="">انتخاب کنید</option>
                {accounts.map((a) => <option key={a.id} value={a.id}>{a.code} {a.name}</option>)}
              </select>
            </label>{' '}
            {account(l.accountId)?.requiresDetail && (
              <label>تفصیلی (تأمین‌کننده){' '}
                <select required value={l.detailId} onChange={setLine(i, 'detailId')} data-testid={`journal-detail-${i}`}>
                  <option value="">انتخاب کنید</option>
                  {suppliers.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
                </select>
              </label>
            )}{' '}
            <label>بدهکار <input value={l.debit} onChange={setLine(i, 'debit')} dir="ltr" size={12} inputMode="numeric" data-testid={`journal-debit-${i}`} /></label>{' '}
            <label>بستانکار <input value={l.credit} onChange={setLine(i, 'credit')} dir="ltr" size={12} inputMode="numeric" data-testid={`journal-credit-${i}`} /></label>
          </p>
        ))}
        <p data-testid="journal-totals" data-balanced={debit === credit && debit > 0}>
          جمع بدهکار {rial(String(debit))} · جمع بستانکار {rial(String(credit))}{debit !== credit ? ` · اختلاف ${rial(String(Math.abs(debit - credit)))} ریال` : ''}
        </p>
        <button type="button" style={{ minHeight: 44 }} onClick={() => setLines([...lines, { ...EMPTY }])}>ردیف دیگر</button>{' '}
        <button type="submit" style={{ minHeight: 44 }} data-testid="journal-submit">ذخیره پیش‌نویس</button>
      </form>
      {error && <p role="alert" data-testid="journal-error">{ERRORS[error] ?? MESSAGES[error] ?? error}</p>}

      <h2>اسناد اخیر</h2>
      {journals === null ? <p>دیدن دفتر روزنامه مجوز حسابداری لازم دارد.</p> : (
        <ul data-testid="journal-list">
          {journals.map((j) => (
            <li key={j.id} data-description={j.description ?? ''} data-status={j.status}>
              {new Date(j.effectiveAt).toLocaleString('fa-IR')} · {j.description} · {STATUS[j.status] ?? j.status}
              {j.source ? ' · خودکار' : ' · دستی'}{j.reversalOf ? ' · سند معکوس' : ''}
              <ul>{j.lines.map((l) => (
                <li key={l.lineNo}>{l.accountCode} {account(l.accountId)?.name ?? ''} · بدهکار {rial(l.debit)} · بستانکار {rial(l.credit)}</li>
              ))}</ul>
              {j.status === 'Draft' && (
                <>
                  <button type="button" style={{ minHeight: 44 }} data-testid="journal-post"
                    onClick={() => void call(`/bff/accounting/journals/${j.id}/post`, 'POST')}>ثبت قطعی</button>{' '}
                  <button type="button" style={{ minHeight: 44 }} data-testid="journal-delete"
                    onClick={() => void call(`/bff/accounting/journals/${j.id}`, 'DELETE')}>حذف پیش‌نویس</button>
                </>
              )}
              {j.status === 'Posted' && !j.reversalOf && (
                <>
                  <input placeholder="دلیل برگشت" value={reason[j.id] ?? ''} onChange={(e) => setReason({ ...reason, [j.id]: e.target.value })}
                    data-testid="journal-reason" />{' '}
                  <button type="button" style={{ minHeight: 44 }} data-testid="journal-reverse"
                    onClick={() => void call(`/bff/accounting/journals/${j.id}/reverse`, 'POST', { reason: (reason[j.id] ?? '').trim() })}>سند معکوس</button>
                </>
              )}
            </li>
          ))}
          {journals.length === 0 && <li>هنوز سندی ثبت نشده است.</li>}
        </ul>
      )}

      <h2>دوره‌های مالی</h2>
      <ul data-testid="period-list">
        {periods.map((p) => (
          <li key={p.id} data-period={`${p.fiscalYear}-${p.periodNo}`} data-status={p.status}>
            {p.fiscalYear.toLocaleString('fa-IR', { useGrouping: false })}/{p.periodNo.toLocaleString('fa-IR')} ·{' '}
            {new Date(p.start).toLocaleDateString('fa-IR')} تا {new Date(new Date(p.end).getTime() - 1).toLocaleDateString('fa-IR')} ·{' '}
            {p.status === 'Open' ? 'باز' : `بسته${p.closedAt ? ` (${new Date(p.closedAt).toLocaleDateString('fa-IR')})` : ''}`}
            {p.status === 'Open' && (
              <>{' '}<button type="button" style={{ minHeight: 44 }} data-testid={`period-close-${p.fiscalYear}-${p.periodNo}`}
                onClick={() => void call(`/bff/accounting/periods/${p.id}/close`, 'POST', { expectedVersion: p.version })}>بستن دوره</button></>
            )}
          </li>
        ))}
      </ul>
      <p>دوره بسته دوباره باز نمی‌شود؛ رویدادهای دیررسِ دوره‌ی بسته (مثلاً برگشت) با تاریخ امروز در دوره‌ی باز ثبت می‌شوند.</p>
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
