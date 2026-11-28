'use client';
import { useEffect, useState } from 'react';
import { codeOf, deleteWithCsrf, putJson } from '@/lib/client';

type Row = { category: string; minimumDays: number; defaultDays: number; overrideDays: number | null; effectiveDays: number; legalHold: boolean; holdReason: string | null; holdUntil: string | null };
type Run = { category: string; outcome: string; cutoff: string; deletedRows: number; ranAt: string };

const NAMES: Record<string, string> = {
  FinancialDocuments: 'اسناد مالی و فاکتور', TaxInvoices: 'صورتحساب‌های مالیاتی', InventoryLedger: 'گردش انبار', Audit: 'ممیزی',
  CustomerCrm: 'داده‌ی مشتریان عضو', CustomerIdentity: 'هویت مشتری', Notifications: 'پیامک و اعلان‌ها', TechnicalLogs: 'لاگ فنی',
};
const OUTCOME: Record<string, string> = { Purged: 'حذف شد', Held: 'به‌دلیل Legal Hold متوقف ماند' };
const ERRORS: Record<string, string> = {
  validation: 'تعداد روز باید عددی بین ۱ و ۳۶٬۵۰۰ باشد.',
  'below-retention-floor': 'کمتر از حداقل نگهداری پلتفرم نمی‌شود؛ فقط می‌توانید بیشتر نگه دارید.',
  forbidden: 'نقش شما اجازه‌ی تغییر تنظیمات فروشگاه را نمی‌دهد.',
};
const fa = (d: string) => new Date(d).toLocaleDateString('fa-IR');
const years = (days: number) => (days >= 365 ? ` (≈ ${(days / 365).toLocaleString('fa-IR', { maximumFractionDigits: 1 })} سال)` : '');

/** US-173 / TEN-005.1: how long each kind of data is kept. The platform sets the minimum; a store may only keep longer. */
export default function RetentionPage() {
  const [rows, setRows] = useState<Row[] | null>(null);
  const [runs, setRuns] = useState<Run[]>([]);
  const [draft, setDraft] = useState<Record<string, string>>({});
  const [error, setError] = useState('');

  async function load() {
    const [a, b] = await Promise.all([fetch('/bff/retention', { cache: 'no-store' }), fetch('/bff/retention/runs', { cache: 'no-store' })]);
    if (!a.ok) { setError(await codeOf(a)); setRows([]); return; }
    setRows((await a.json()).items);
    if (b.ok) setRuns((await b.json()).items);
  }
  useEffect(() => { void load(); }, []);

  async function save(r: Row) {
    setError('');
    const res = await putJson(`/bff/retention/${r.category}`, { days: Number(draft[r.category]) });
    if (!res.ok) return setError(await codeOf(res));
    setDraft((d) => ({ ...d, [r.category]: '' }));
    await load();
  }
  async function reset(r: Row) {
    setError('');
    const res = await deleteWithCsrf(`/bff/retention/${r.category}`);
    if (res.status !== 204) return setError(await codeOf(res));
    await load();
  }

  if (!rows) return <p>در حال بارگذاری…</p>;
  return (
    <section>
      <h1>مدت نگهداری داده‌ها</h1>
      <p>حداقل نگهداری هر دسته را پلتفرم (طبق قانون) تعیین می‌کند. شما می‌توانید داده را بیشتر از پیش‌فرض نگه دارید، نه کمتر از حداقل. پس از پایان مدت، داده‌ی پیامک‌ها هر شب حذف می‌شود؛ اسناد مالی بایگانی می‌شوند و حذف نمی‌شوند.</p>
      {error && <p role="alert" data-testid="retention-error">{ERRORS[error] ?? error}</p>}
      <table data-testid="retention">
        <thead><tr><th>دسته</th><th>حداقل (روز)</th><th>پیش‌فرض (روز)</th><th>انتخاب شما</th><th>مدت اعمال‌شده</th><th /></tr></thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.category} data-testid={`ret-${r.category}`}>
              <td>{NAMES[r.category] ?? r.category}{r.legalHold && <strong data-testid={`hold-${r.category}`}> — Legal Hold{r.holdReason ? `: ${r.holdReason}` : ''}{r.holdUntil ? ` تا ${fa(r.holdUntil)}` : ''}</strong>}</td>
              <td>{r.minimumDays.toLocaleString('fa-IR')}</td>
              <td>{r.defaultDays.toLocaleString('fa-IR')}</td>
              <td>{r.overrideDays === null ? '—' : r.overrideDays.toLocaleString('fa-IR')}</td>
              <td data-testid={`ret-effective-${r.category}`}>{r.effectiveDays.toLocaleString('fa-IR')}{years(r.effectiveDays)}</td>
              <td>
                <input aria-label={`روز نگهداری ${NAMES[r.category] ?? r.category}`} name={`days-${r.category}`} type="number" min={r.minimumDays} max={36500}
                  value={draft[r.category] ?? ''} onChange={(e) => setDraft((d) => ({ ...d, [r.category]: e.target.value }))} />{' '}
                <button disabled={!draft[r.category]} onClick={() => void save(r)} data-testid={`ret-save-${r.category}`} style={{ minHeight: 44 }}>ثبت</button>{' '}
                {r.overrideDays !== null && <button onClick={() => void reset(r)} data-testid={`ret-reset-${r.category}`}>بازگشت به پیش‌فرض</button>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <h2>اجراهای اخیر</h2>
      {runs.length === 0 ? <p data-testid="no-runs">هنوز داده‌ای حذف نشده است.</p> : (
        <table data-testid="runs">
          <thead><tr><th>زمان</th><th>دسته</th><th>نتیجه</th><th>تعداد ردیف</th><th>قدیمی‌تر از</th></tr></thead>
          <tbody>{runs.map((x, i) => (
            <tr key={i}><td>{fa(x.ranAt)}</td><td>{NAMES[x.category] ?? x.category}</td><td>{OUTCOME[x.outcome] ?? x.outcome}</td><td>{x.deletedRows.toLocaleString('fa-IR')}</td><td>{fa(x.cutoff)}</td></tr>
          ))}</tbody>
        </table>
      )}
    </section>
  );
}
