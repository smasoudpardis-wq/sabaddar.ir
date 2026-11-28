'use client';
import { useEffect, useState } from 'react';
import { codeOf, csrf, MESSAGES } from '@/lib/client';

type Line = { lineNo: number; terminalNo: string | null; referenceNo: string; transactionDate: string; amount: string; fee: string; outcome: string };
type Import = {
  id: string; fileName: string; lineCount: number; matchedCount: number; matchedAmount: string; fees: string; deposited: string; importedAt: string;
  lines?: Line[];
};
type RowError = { line: number; field: string; message: string };
type Open = { paymentId: string; invoiceNo: string; referenceNo: string; terminalNo: string | null; amount: string; businessDate: string; cashierName: string | null };
type Report = { total: { count: number; amount: string }; byCashier: { cashierId: string; displayName: string | null; count: number; amount: string }[]; items: Open[]; truncated: boolean };

const rial = (v: string) => Number(v).toLocaleString('fa-IR');
const OUTCOMES: Record<string, string> = {
  Matched: 'تطبیق شد',
  AmountMismatch: 'مبلغ متفاوت',
  NotFound: 'پرداختی با این مرجع نیست',
  Ambiguous: 'چند پرداخت مشابه؛ دستی بررسی شود',
  AlreadySettled: 'قبلاً تسویه شده',
  DuplicateInFile: 'تکراری در همین فایل',
  Unfinalized: 'فاکتور هنوز نهایی نشده',
};
const FIELDS: Record<string, string> = { reference: 'شماره مرجع', terminal: 'شماره پایانه', date: 'تاریخ', amount: 'مبلغ', fee: 'کارمزد', file: 'فایل' };
const ERRORS: Record<string, string> = {
  'already-imported': 'این فایل قبلاً وارد شده است.',
  'invalid-settlement-file': 'فایل خوانا نیست و هیچ سطری وارد نشد:',
  'no-open-period': 'دوره مالی باز برای امروز وجود ندارد.',
  'account-role-unmapped': 'حساب بانک، کارمزد یا وجوه کارتخوان در نگاشت حساب‌ها تعیین نشده است.',
};

/**
 * US-105 / PAY-002.2 / AB-14: the accountant imports the bank's settlement report (CSV); each line settles the card slip with
 * its reference and amount, the settled sum moves from «وجوه کارتخوان در راه» to the bank (less the bank's fee), and what no
 * file settled is listed per cashier.
 */
export default function CardSettlementsPage() {
  const [file, setFile] = useState<File | null>(null);
  const [result, setResult] = useState<Import | null>(null);
  const [imports, setImports] = useState<Import[] | null>(null);
  const [rowErrors, setRowErrors] = useState<RowError[]>([]);
  const [range, setRange] = useState({ from: '', to: '' });   // empty: the store's last 30 days (its own time zone)
  const [report, setReport] = useState<Report | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  async function loadImports() {
    const r = await fetch('/bff/settlements/imports', { cache: 'no-store' });
    if (r.ok) setImports((await r.json()).items);
    else setError(await codeOf(r));
  }
  async function loadReport() {
    const r = await fetch(`/bff/settlements/unreconciled?from=${range.from}&to=${range.to}`, { cache: 'no-store' });
    if (r.ok) setReport(await r.json());
    else setError(await codeOf(r));
  }
  useEffect(() => { void loadImports(); void loadReport(); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  async function upload(e: React.FormEvent) {
    e.preventDefault();
    if (!file) return;
    setBusy(true);
    setError('');
    setRowErrors([]);
    setResult(null);
    const r = await fetch('/bff/settlements/imports', {
      method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf(), 'idempotency-key': crypto.randomUUID() },
      body: JSON.stringify({ fileName: file.name, content: await file.text() }),
    });
    setBusy(false);
    if (r.status === 201) {
      setResult(await r.json());
      void loadImports();
      void loadReport();
      return;
    }
    const p = await r.clone().json().catch(() => null) as { code?: string; context?: { rows?: RowError[] } } | null;
    if (p?.code === 'invalid-settlement-file') setRowErrors(p.context?.rows ?? []);
    setError(await codeOf(r));
  }

  async function open(id: string) {
    const r = await fetch(`/bff/settlements/imports/${id}`, { cache: 'no-store' });
    if (r.ok) setResult(await r.json());
    else setError(await codeOf(r));
  }

  return (
    <section>
      <h1>تسویه کارتخوان</h1>
      <p>فایل گزارش تسویه بانک (CSV) را وارد کنید. ستون‌های لازم: شماره مرجع، تاریخ و مبلغ (ریال)؛ شماره پایانه و کارمزد اختیاری‌اند.</p>
      <form onSubmit={(e) => void upload(e)} data-testid="settlement-form">
        <input type="file" accept=".csv,text/csv,text/plain" required onChange={(e) => setFile(e.target.files?.[0] ?? null)} data-testid="settlement-file" />{' '}
        <button type="submit" disabled={busy || !file} style={{ minHeight: 44 }} data-testid="settlement-submit">ورود و تطبیق</button>
      </form>
      {error && <p role="alert" data-testid="error">{ERRORS[error] ?? MESSAGES[error] ?? error}</p>}
      {rowErrors.length > 0 && (
        <ul data-testid="file-errors">
          {rowErrors.map((e) => <li key={`${e.line}-${e.field}`}>سطر {e.line.toLocaleString('fa-IR')}: {FIELDS[e.field] ?? e.field} ({e.message})</li>)}
        </ul>
      )}

      {result && (
        <div data-testid="settlement-result">
          <h2>{result.fileName}</h2>
          <p>
            <span data-testid="result-matched">{result.matchedCount.toLocaleString('fa-IR')}</span> از {result.lineCount.toLocaleString('fa-IR')} سطر تطبیق شد؛
            واریز به بانک <span data-testid="result-deposited" data-value={result.deposited}>{rial(result.deposited)}</span> ریال،
            کارمزد <span data-testid="result-fees" data-value={result.fees}>{rial(result.fees)}</span> ریال.
          </p>
          {result.lines && (
            <table>
              <thead><tr><th>سطر</th><th>پایانه</th><th>مرجع</th><th>تاریخ</th><th>مبلغ</th><th>کارمزد</th><th>نتیجه</th></tr></thead>
              <tbody>
                {result.lines.map((l) => (
                  <tr key={l.lineNo}>
                    <td>{l.lineNo.toLocaleString('fa-IR')}</td><td dir="ltr">{l.terminalNo ?? '—'}</td><td dir="ltr">{l.referenceNo}</td><td dir="ltr">{l.transactionDate}</td>
                    <td>{rial(l.amount)}</td><td>{rial(l.fee)}</td>
                    <td data-testid={`line-outcome-${l.lineNo}`} data-outcome={l.outcome}>{OUTCOMES[l.outcome] ?? l.outcome}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      <h2>پرداخت‌های کارتخوان بدون تطبیق</h2>
      <p>پیش‌فرض: ۳۰ روز اخیر فروشگاه.</p>
      <form onSubmit={(e) => { e.preventDefault(); void loadReport(); }}>
        <label>از <input type="date" value={range.from} onChange={(e) => setRange({ ...range, from: e.target.value })} data-testid="report-from" /></label>{' '}
        <label>تا <input type="date" value={range.to} onChange={(e) => setRange({ ...range, to: e.target.value })} data-testid="report-to" /></label>{' '}
        <button type="submit" style={{ minHeight: 44 }} data-testid="report-refresh">نمایش</button>
      </form>
      {report && (
        <div data-testid="unreconciled">
          <p data-testid="unreconciled-total" data-count={report.total.count}>
            {report.total.count.toLocaleString('fa-IR')} پرداخت، جمع {rial(report.total.amount)} ریال{report.truncated && ' (فقط ۲۰۰۰ مورد اول؛ بازه را کوتاه‌تر کنید)'}
          </p>
          <ul>
            {report.byCashier.map((c) => (
              <li key={c.cashierId} data-testid={`cashier-${c.displayName ?? c.cashierId}`} data-count={c.count}>
                {c.displayName ?? 'کاربر حذف‌شده'}: {c.count.toLocaleString('fa-IR')} پرداخت، {rial(c.amount)} ریال
              </li>
            ))}
          </ul>
          {report.items.length > 0 && (
            <table>
              <thead><tr><th>تاریخ</th><th>فاکتور</th><th>پایانه</th><th>مرجع</th><th>مبلغ</th><th>صندوق‌دار</th></tr></thead>
              <tbody>
                {report.items.map((p) => (
                  <tr key={p.paymentId} data-testid={`unreconciled-${p.referenceNo}`}>
                    <td dir="ltr">{p.businessDate}</td><td dir="ltr">{p.invoiceNo}</td><td dir="ltr">{p.terminalNo ?? '—'}</td><td dir="ltr">{p.referenceNo}</td>
                    <td>{rial(p.amount)}</td><td>{p.cashierName ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      <h2>فایل‌های واردشده</h2>
      {imports && imports.length === 0 && <p>هنوز فایلی وارد نشده است.</p>}
      {imports && imports.length > 0 && (
        <table data-testid="imports">
          <thead><tr><th>فایل</th><th>زمان</th><th>تطبیق</th><th>واریز</th><th></th></tr></thead>
          <tbody>
            {imports.map((i) => (
              <tr key={i.id} data-testid={`import-${i.fileName}`}>
                <td>{i.fileName}</td><td>{new Date(i.importedAt).toLocaleString('fa-IR')}</td>
                <td>{i.matchedCount.toLocaleString('fa-IR')} از {i.lineCount.toLocaleString('fa-IR')}</td><td>{rial(i.deposited)}</td>
                <td><button type="button" onClick={() => void open(i.id)} data-testid={`open-${i.fileName}`}>جزئیات</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
