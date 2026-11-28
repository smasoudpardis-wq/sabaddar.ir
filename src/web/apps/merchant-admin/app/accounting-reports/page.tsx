'use client';
import { useEffect, useState } from 'react';
import { codeOf, MESSAGES } from '@/lib/client';

type Account = { id: string; code: string; name: string; isPostable: boolean; requiresDetail: boolean };
type Period = { id: string; fiscalYear: number; periodNo: number; start: string; end: string; status: string };
type TbRow = {
  accountId: string; accountCode: string; accountName: string; postable: boolean; level: number;
  openingDebit: string; openingCredit: string; periodDebit: string; periodCredit: string; closingDebit: string; closingCredit: string;
};
type Amounts = Pick<TbRow, 'openingDebit' | 'openingCredit' | 'periodDebit' | 'periodCredit' | 'closingDebit' | 'closingCredit'>;
type Tb = { from: string; to: string; items: TbRow[]; totals: Amounts & { balanced: boolean } };
type LRow = { journalId: string; effectiveAt: string; description: string | null; accountCode: string; detailId: string | null; debit: string; credit: string; runningBalance: string };
type Ledger = {
  account: { id: string; code: string; name: string; postable: boolean }; opening: { debit: string; credit: string }; closing: { debit: string; credit: string };
  periodDebit: string; periodCredit: string; items: LRow[]; page: { nextCursor: string | null; hasMore: boolean };
};

const MONTHS = ['فروردین', 'اردیبهشت', 'خرداد', 'تیر', 'مرداد', 'شهریور', 'مهر', 'آبان', 'آذر', 'دی', 'بهمن', 'اسفند'];
const rial = (v: string) => Number(v).toLocaleString('fa-IR');
const side = (net: number) => (net >= 0 ? `${rial(String(net))} بد` : `${rial(String(-net))} بس`);
const day = (iso: string) => new Date(iso).toLocaleDateString('fa-IR');
const label = (p: Period) => `${MONTHS[p.periodNo - 1] ?? p.periodNo} ${p.fiscalYear.toLocaleString('fa-IR', { useGrouping: false })}`;

/** A CSV (UTF-8 with BOM, so Excel reads Persian) of what is on screen. */
function download(name: string, header: string[], rows: (string | number)[][]) {
  const cell = (v: string | number) => (/[",\n]/.test(String(v)) ? `"${String(v).replace(/"/g, '""')}"` : String(v));
  const csv = '﻿' + [header, ...rows].map((r) => r.map(cell).join(',')).join('\r\n');
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
  a.download = name;
  a.click();
  URL.revokeObjectURL(a.href);
}

/**
 * US-114 / ACC-007 / ACC-008: the trial balance (2- and 4-column) and the ledger of an account (دفتر معین), a group (دفتر کل)
 * or one detail (گردش تفصیلی), over a range of Jalali fiscal months; the cash account's ledger is the cash balance (تراز صندوق).
 * Export: CSV for Excel, and the browser's print to PDF.
 */
export default function AccountingReportsPage() {
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [periods, setPeriods] = useState<Period[]>([]);
  const [fromId, setFromId] = useState('');
  const [toId, setToId] = useState('');
  const [view, setView] = useState<'tb' | 'ledger'>('tb');
  const [columns, setColumns] = useState<2 | 4>(4);
  const [tb, setTb] = useState<Tb | null>(null);
  const [accountId, setAccountId] = useState('');
  const [detailId, setDetailId] = useState('');
  const [ledger, setLedger] = useState<Ledger | null>(null);
  const [error, setError] = useState('');

  useEffect(() => {
    void (async () => {
      const a = await fetch('/bff/accounting/accounts', { cache: 'no-store' });
      if (a.ok) setAccounts(await a.json());
      else setError(await codeOf(a));
      const p = await fetch('/bff/accounting/periods', { cache: 'no-store' });
      if (!p.ok) return;
      const list: Period[] = await p.json();
      setPeriods(list);
      const now = Date.now();
      const current = list.find((x) => Date.parse(x.start) <= now && now < Date.parse(x.end)) ?? list[list.length - 1];
      if (current) {
        setToId(current.id);
        setFromId(list.find((x) => x.fiscalYear === current.fiscalYear && x.periodNo === 1)?.id ?? current.id);
      }
    })();
  }, []);

  const range = () => {
    const f = periods.find((p) => p.id === fromId);
    const t = periods.find((p) => p.id === toId);
    return f && t ? `from=${encodeURIComponent(f.start)}&to=${encodeURIComponent(t.end)}` : '';
  };

  async function showTb() {
    setError('');
    const r = await fetch(`/bff/accounting/reports/trial-balance?${range()}`, { cache: 'no-store' });
    if (r.ok) { setTb(await r.json()); setView('tb'); } else setError(await codeOf(r));
  }

  async function showLedger(id = accountId, cursor?: string) {
    if (!id) return;
    setError('');
    const q = `${range()}${detailId.trim() ? `&detailId=${encodeURIComponent(detailId.trim())}` : ''}${cursor ? `&cursor=${cursor}` : ''}&limit=200`;
    const r = await fetch(`/bff/accounting/reports/ledger/${id}?${q}`, { cache: 'no-store' });
    if (!r.ok) return setError(await codeOf(r));
    const next: Ledger = await r.json();
    setLedger((prev) => (cursor && prev ? { ...next, items: [...prev.items, ...next.items] } : next));
    setAccountId(id);
    setView('ledger');
  }

  const cashAccount = accounts.find((a) => a.code === '1101');
  const amounts = (r: Amounts) => (columns === 4
    ? [r.openingDebit, r.openingCredit, r.periodDebit, r.periodCredit, r.closingDebit, r.closingCredit]
    : [r.closingDebit, r.closingCredit]);
  const tbHeader = columns === 4
    ? ['کد', 'حساب', 'بدهکار اول دوره', 'بستانکار اول دوره', 'گردش بدهکار', 'گردش بستانکار', 'مانده بدهکار', 'مانده بستانکار']
    : ['کد', 'حساب', 'مانده بدهکار', 'مانده بستانکار'];

  function exportTb() {
    if (!tb) return;
    download(`trial-balance-${columns}.csv`, tbHeader, [
      ...tb.items.map((r) => [r.accountCode, r.accountName, ...amounts(r)]),
      ['', 'جمع', ...amounts(tb.totals)],
    ]);
  }

  function exportLedger() {
    if (!ledger) return;
    download(`ledger-${ledger.account.code}.csv`, ['تاریخ', 'شرح', 'حساب', 'بدهکار', 'بستانکار', 'مانده'],
      ledger.items.map((l) => [day(l.effectiveAt), l.description ?? '', l.accountCode, l.debit, l.credit, l.runningBalance]));
  }

  return (
    <section>
      <style>{'@media print { .no-print { display: none; } } table.report td.n { text-align: left; direction: ltr; }'}</style>
      <h1>گزارش‌های حسابداری</h1>
      <div className="no-print">
        <label>از دوره{' '}
          <select value={fromId} onChange={(e) => setFromId(e.target.value)} data-testid="report-from-period">
            {periods.map((p) => <option key={p.id} value={p.id}>{label(p)}</option>)}
          </select>
        </label>{' '}
        <label>تا دوره{' '}
          <select value={toId} onChange={(e) => setToId(e.target.value)} data-testid="report-to-period">
            {periods.map((p) => <option key={p.id} value={p.id}>{label(p)}</option>)}
          </select>
        </label>
        <p>
          <button type="button" style={{ minHeight: 44 }} onClick={() => void showTb()} data-testid="show-trial-balance">تراز آزمایشی</button>{' '}
          <label><input type="radio" checked={columns === 4} onChange={() => setColumns(4)} data-testid="tb-cols-4" /> ۴ ستونی</label>{' '}
          <label><input type="radio" checked={columns === 2} onChange={() => setColumns(2)} data-testid="tb-cols-2" /> ۲ ستونی</label>
        </p>
        <p>
          <label>حساب{' '}
            <select value={accountId} onChange={(e) => setAccountId(e.target.value)} data-testid="ledger-account">
              <option value="">انتخاب کنید</option>
              {accounts.map((a) => <option key={a.id} value={a.id}>{a.code} {a.name}{a.isPostable ? '' : ' (کل)'}</option>)}
            </select>
          </label>{' '}
          <label>تفصیلی (اختیاری) <input value={detailId} onChange={(e) => setDetailId(e.target.value)} dir="ltr" size={36} placeholder="شناسه مشتری یا تأمین‌کننده"
            data-testid="ledger-detail" /></label>{' '}
          <button type="button" style={{ minHeight: 44 }} onClick={() => void showLedger()} disabled={!accountId} data-testid="show-ledger">دفتر حساب</button>{' '}
          {cashAccount && <button type="button" style={{ minHeight: 44 }} onClick={() => { setDetailId(''); void showLedger(cashAccount.id); }} data-testid="show-cash">تراز صندوق</button>}
        </p>
      </div>
      {error && <p role="alert" data-testid="error">{MESSAGES[error] ?? error}</p>}

      {view === 'tb' && tb && (
        <div data-testid="trial-balance" data-balanced={String(tb.totals.balanced)}>
          <h2>تراز آزمایشی {columns === 4 ? '۴' : '۲'} ستونی · {day(tb.from)} تا {day(tb.to)}</h2>
          <p className="no-print">
            <button type="button" onClick={exportTb} data-testid="export-tb">خروجی Excel (CSV)</button>{' '}
            <button type="button" onClick={() => window.print()}>چاپ / PDF</button>
          </p>
          <table className="report">
            <thead><tr>{tbHeader.map((h) => <th key={h}>{h}</th>)}</tr></thead>
            <tbody>
              {tb.items.map((r) => (
                <tr key={r.accountId} data-testid={`tb-${r.accountCode}`} data-closing-debit={r.closingDebit} data-closing-credit={r.closingCredit}
                  style={{ fontWeight: r.postable ? 'normal' : 'bold' }}>
                  <td>{r.accountCode}</td>
                  <td style={{ paddingInlineStart: `${r.level}em` }}>
                    <button type="button" className="no-print" onClick={() => { setDetailId(''); void showLedger(r.accountId); }}>{r.accountName}</button>
                  </td>
                  {amounts(r).map((v, i) => <td key={i} className="n">{rial(v)}</td>)}
                </tr>
              ))}
              <tr data-testid="tb-totals"><td></td><td>جمع {tb.totals.balanced ? '✓ تراز' : '✗ تراز نیست'}</td>{amounts(tb.totals).map((v, i) => <td key={i} className="n">{rial(v)}</td>)}</tr>
            </tbody>
          </table>
        </div>
      )}

      {view === 'ledger' && ledger && (
        <div data-testid="ledger" data-account={ledger.account.code}>
          <h2>{ledger.account.postable ? 'دفتر معین' : 'دفتر کل'} {ledger.account.code} {ledger.account.name}{detailId.trim() && ' · گردش تفصیلی'}</h2>
          <p>
            مانده اول دوره: {side(Number(ledger.opening.debit) - Number(ledger.opening.credit))} · گردش: {rial(ledger.periodDebit)} بد، {rial(ledger.periodCredit)} بس ·
            مانده پایان: <span data-testid="ledger-closing" data-net={Number(ledger.closing.debit) - Number(ledger.closing.credit)}>
              {side(Number(ledger.closing.debit) - Number(ledger.closing.credit))}</span>
          </p>
          <p className="no-print">
            <button type="button" onClick={exportLedger} data-testid="export-ledger">خروجی Excel (CSV)</button>{' '}
            <button type="button" onClick={() => window.print()}>چاپ / PDF</button>
          </p>
          <table className="report">
            <thead><tr><th>تاریخ</th><th>شرح</th><th>حساب</th><th>بدهکار</th><th>بستانکار</th><th>مانده</th></tr></thead>
            <tbody>
              {ledger.items.map((l, i) => (
                <tr key={`${l.journalId}-${i}`} data-testid="ledger-line">
                  <td>{day(l.effectiveAt)}</td><td>{l.description ?? ''}</td><td>{l.accountCode}</td>
                  <td className="n">{rial(l.debit)}</td><td className="n">{rial(l.credit)}</td><td className="n">{side(Number(l.runningBalance))}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {ledger.page.hasMore && <button type="button" className="no-print" onClick={() => void showLedger(accountId, ledger.page.nextCursor ?? undefined)}>بیشتر</button>}
        </div>
      )}
      <p className="no-print"><a href="/">بازگشت</a></p>
    </section>
  );
}
