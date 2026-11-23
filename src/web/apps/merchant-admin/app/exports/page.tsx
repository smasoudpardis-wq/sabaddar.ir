'use client';
import { useEffect, useRef, useState } from 'react';
import { codeOf, MESSAGES, postJson } from '@/lib/client';

type Job = { id: string; status: 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Expired'; rowCount: number | null; downloadUrl: string | null };

const CATEGORIES: [string, string][] = [
  ['Products', 'کالاها و قیمت‌ها'], ['Customers', 'مشتریان عضو (بدون شماره تماس)'], ['Sales', 'فروش و شیفت‌ها'],
  ['Inventory', 'انبار و گردش کالا'], ['Journals', 'اسناد حسابداری'], ['Receivables', 'حساب‌های نسیه'], ['TaxInvoices', 'صورتحساب‌های مؤدیان'],
];
const STATUS: Record<Job['status'], string> = {
  Queued: 'در صف', Running: 'در حال تهیه', Completed: 'آماده', Failed: 'ناموفق؛ دوباره درخواست دهید', Expired: 'منقضی شده (۲۴ ساعت)',
};

/** US-016 / TEN-005: full data export as a ZIP of CSV files; the link is fetched fresh (5 minutes) on each download. */
export default function ExportsPage() {
  const [selected, setSelected] = useState<string[]>(CATEGORIES.map(([c]) => c));
  const [job, setJob] = useState<Job | null>(null);
  const [error, setError] = useState('');
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  function stepUp() {
    window.location.assign('/auth/login?' + new URLSearchParams({ acr: 'mfa', returnTo: '/exports' }));
  }

  async function poll(id: string) {
    const r = await fetch(`/bff/exports/${id}`, { cache: 'no-store' });
    if (!r.ok) return setError(await codeOf(r));
    const j: Job = await r.json();
    setJob(j);
    if (j.status === 'Queued' || j.status === 'Running') timer.current = setTimeout(() => void poll(id), 2000);
  }
  useEffect(() => () => { if (timer.current) clearTimeout(timer.current); }, []);

  async function request(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    const r = await postJson('/bff/exports', { categories: selected });
    if (!r.ok) {
      const code = await codeOf(r);
      if (code === 'step-up-required' || code === 'mfa-required') return stepUp();
      return setError(code);
    }
    const j: Job = await r.json();
    setJob(j);
    void poll(j.id);
  }

  async function download() {
    if (!job) return;
    const r = await fetch(`/bff/exports/${job.id}`, { cache: 'no-store' });   // a fresh 5-minute link, audited
    if (!r.ok) return setError(await codeOf(r));
    const j: Job = await r.json();
    setJob(j);
    if (j.downloadUrl) window.location.assign(j.downloadUrl);
  }

  return (
    <section>
      <h1>خروجی کامل داده‌ها</h1>
      <p>فایل ZIP شامل CSV هر بخش و یک فهرست (manifest) است؛ ۲۴ ساعت نگه داشته می‌شود و هر دریافت ثبت ممیزی می‌شود.</p>
      <form onSubmit={(e) => void request(e)} data-testid="export-form">
        {CATEGORIES.map(([c, label]) => (
          <label key={c} style={{ display: 'block', minHeight: 44 }}>
            <input type="checkbox" checked={selected.includes(c)}
              onChange={(e) => setSelected(e.target.checked ? [...selected, c] : selected.filter((x) => x !== c))} /> {label}
          </label>
        ))}
        <button type="submit" disabled={selected.length === 0} style={{ minHeight: 44 }} data-testid="export-submit">درخواست خروجی</button>
      </form>
      {job && (
        <p data-testid="export-status" data-status={job.status} data-export-id={job.id}>
          وضعیت: {STATUS[job.status]}{job.rowCount !== null ? ` — ${job.rowCount.toLocaleString('fa-IR')} ردیف` : ''}
          {job.status === 'Completed' && <> {' '}<button onClick={() => void download()} style={{ minHeight: 44 }} data-testid="export-download">دریافت فایل</button></>}
        </p>
      )}
      {error && <p role="alert" data-testid="export-error">{MESSAGES[error] ?? error}</p>}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
