'use client';
import { useEffect, useState } from 'react';
import { codeOf, MESSAGES } from '@/lib/client';

type Branch = { id: string; code: string; name: string };
type Row = Record<string, string | number | boolean | null>;
type Report = 'sales-daily' | 'gross-profit' | 'stock';
type Result = { items: Row[]; meta: { generatedAt: string; dataAsOf: string } };

const rial = (v: unknown) => Number(v).toLocaleString('fa-IR');
const iso = (d: Date) => d.toISOString();
const dayStart = (v: string) => new Date(`${v}T00:00:00`);
const ERRORS: Record<string, string> = {
  forbidden: 'این گزارش یا خروجی آن برای نقش شما مجاز نیست.',
  'report-too-large': 'ردیف‌های گزارش از سقف مجاز بیشتر است؛ بازه یا شعبه را کوچک‌تر کنید.',
};
const TITLES: Record<Report, string> = { 'sales-daily': 'فروش روزانه', 'gross-profit': 'سود ناخالص', stock: 'موجودی و ارزش' };

/**
 * US-161 / RPT-001..003: daily sales, gross profit and stock value for the branches the role reaches. The sales and profit
 * figures are for finalized sales on the store's own day; profit is on the moving average cost of the moment of sale, and a
 * line sold with no known cost is flagged. CSV export (opens in Excel) needs the data-export right and is audited.
 */
export default function ReportsPage() {
  const today = new Date();
  const [report, setReport] = useState<Report>('sales-daily');
  const [from, setFrom] = useState(() => new Date(today.getTime() - 6 * 86_400_000).toISOString().slice(0, 10));
  const [to, setTo] = useState(() => today.toISOString().slice(0, 10));
  const [branches, setBranches] = useState<Branch[]>([]);
  const [branchId, setBranchId] = useState('');
  const [groupBy, setGroupBy] = useState('');
  const [data, setData] = useState<Result | null>(null);
  const [error, setError] = useState('');

  useEffect(() => {
    void (async () => {
      const r = await fetch('/bff/branches', { cache: 'no-store' });
      if (r.ok) setBranches(await r.json());
    })();
  }, []);

  function query(format?: string) {
    const q = new URLSearchParams();
    if (report !== 'stock') {
      q.set('from', iso(dayStart(from)));
      q.set('to', iso(new Date(dayStart(to).getTime() + 86_400_000)));
    }
    if (branchId) q.set('branchId', branchId);
    if (groupBy && report !== 'stock') q.set('groupBy', groupBy);
    if (format) q.set('format', format);
    return `/bff/reports/${report}?${q.toString()}`;
  }

  async function show() {
    setError('');
    const r = await fetch(query(), { cache: 'no-store' });
    if (!r.ok) { setData(null); return setError(await codeOf(r)); }
    setData(await r.json());
  }

  async function exportCsv() {
    setError('');
    const r = await fetch(query('csv'), { cache: 'no-store' });
    if (!r.ok) return setError(await codeOf(r));
    const a = document.createElement('a');
    a.href = URL.createObjectURL(await r.blob());
    a.download = `${report}.csv`;
    a.click();
    URL.revokeObjectURL(a.href);
  }

  const branchName = (id: unknown) => branches.find((b) => b.id === id)?.name ?? String(id ?? '');
  const money = (r: Row, k: string) => rial(r[k]);

  return (
    <section>
      <h1>گزارش‌های فروش و موجودی</h1>
      <p>
        {(Object.keys(TITLES) as Report[]).map((k) => (
          <button key={k} type="button" onClick={() => { setReport(k); setGroupBy(''); setData(null); }} aria-pressed={report === k} style={{ minHeight: 44, marginInlineEnd: 8 }}
            data-testid={`tab-${k}`}>{TITLES[k]}</button>
        ))}
      </p>
      <form onSubmit={(e) => { e.preventDefault(); void show(); }} data-testid="report-form">
        {report !== 'stock' && (
          <>
            <label>از <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} data-testid="rp-from" /></label>{' '}
            <label>تا <input type="date" value={to} onChange={(e) => setTo(e.target.value)} data-testid="rp-to" /></label>{' '}
          </>
        )}
        <label>شعبه{' '}
          <select value={branchId} onChange={(e) => setBranchId(e.target.value)} data-testid="rp-branch">
            <option value="">همه‌ی شعبه‌های مجاز</option>
            {branches.map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
          </select>
        </label>{' '}
        {report === 'sales-daily' && (
          <label>تفکیک{' '}
            <select value={groupBy} onChange={(e) => setGroupBy(e.target.value)} data-testid="rp-group">
              <option value="">شعبه و روز</option>
              <option value="register">+ صندوق</option>
              <option value="cashier">+ صندوق‌دار</option>
              <option value="register,cashier">+ صندوق و صندوق‌دار</option>
            </select>
          </label>
        )}
        {report === 'gross-profit' && (
          <label>تفکیک{' '}
            <select value={groupBy} onChange={(e) => setGroupBy(e.target.value)} data-testid="rp-group">
              <option value="">روز</option>
              <option value="branch">شعبه</option>
              <option value="product">کالا</option>
            </select>
          </label>
        )}{' '}
        <button type="submit" style={{ minHeight: 44 }} data-testid="rp-show">نمایش</button>{' '}
        <button type="button" style={{ minHeight: 44 }} onClick={() => void exportCsv()} data-testid="rp-export">خروجی Excel (CSV)</button>
      </form>
      {error && <p role="alert" data-testid="error">{ERRORS[error] ?? MESSAGES[error] ?? error}</p>}

      {data && (
        <div data-testid="report" data-report={report}>
          <p data-testid="report-meta">به‌روزرسانی داده: {new Date(data.meta.dataAsOf).toLocaleString('fa-IR')}</p>
          {data.items.length === 0 && <p>برای این بازه داده‌ای نیست.</p>}
          {data.items.length > 0 && report === 'sales-daily' && (
            <table>
              <thead><tr><th>تاریخ</th><th>شعبه</th><th>فاکتور</th><th>ناخالص</th><th>تخفیف</th><th>مالیات</th><th>خالص</th></tr></thead>
              <tbody>
                {data.items.map((r, i) => (
                  <tr key={i} data-testid="row" data-net={String(r.net)} data-invoices={String(r.invoices)}>
                    <td>{String(r.jalaliDate)}</td><td>{branchName(r.branchId)}</td><td>{Number(r.invoices).toLocaleString('fa-IR')}</td>
                    <td>{money(r, 'gross')}</td><td>{money(r, 'discount')}</td><td>{money(r, 'tax')}</td><td>{money(r, 'net')}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {data.items.length > 0 && report === 'gross-profit' && (
            <>
              <p>بر اساس میانگین موزون بها در لحظه‌ی فروش. ردیف‌های ⚠ کالایی با بهای صفر یا نامعلوم دارند و سودشان بیش از واقع است.</p>
              <table>
                <thead><tr><th>{groupBy === 'branch' ? 'شعبه' : groupBy === 'product' ? 'کالا' : 'روز'}</th><th>فروش خالص</th><th>بها</th><th>سود ناخالص</th><th>حاشیه سود</th><th></th></tr></thead>
                <tbody>
                  {data.items.map((r, i) => (
                    <tr key={i} data-testid="row" data-profit={String(r.grossProfit)} data-zero-cost={String(r.itemsWithZeroCost)}>
                      <td>{groupBy === 'branch' ? branchName(r.key) : (r.name as string | null) ?? String(r.key)}</td>
                      <td>{money(r, 'netSales')}</td><td>{money(r, 'cost')}</td><td>{money(r, 'grossProfit')}</td>
                      <td>{(Number(r.marginBp) / 100).toLocaleString('fa-IR')}٪</td>
                      <td>{Number(r.itemsWithZeroCost) > 0 && <span title="بهای صفر یا نامعلوم">⚠ {Number(r.itemsWithZeroCost).toLocaleString('fa-IR')}</span>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )}
          {data.items.length > 0 && report === 'stock' && (
            <table>
              <thead><tr><th>کالا</th><th>SKU</th><th>موجودی</th><th>ارزش (ریال)</th><th></th></tr></thead>
              <tbody>
                {data.items.map((r, i) => (
                  <tr key={i} data-testid="row" data-sku={String(r.sku)} data-value={String(r.value)}>
                    <td>{String(r.name ?? '')}</td><td dir="ltr">{String(r.sku ?? '')}</td><td>{Number(r.onHand).toLocaleString('fa-IR')}</td><td>{money(r, 'value')}</td>
                    <td>{r.belowReorderPoint === true && '⚠ کم‌موجودی'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
