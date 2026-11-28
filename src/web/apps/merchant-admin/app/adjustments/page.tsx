'use client';
import { useEffect, useState } from 'react';
import { codeOf, csrf, MESSAGES } from '@/lib/client';

type Warehouse = { id: string; code: string };
type Adjustment = { id: string; warehouseId: string; reason: string; note: string | null; lineCount: number; value: string; approvalId: string | null; postedAt: string };
type Product = { id: string; name: string; variants: { id: string; sku: string }[] };
type Line = { sku: string; delta: string };

const REASONS: [string, string][] = [['Damage', 'آسیب'], ['Expiry', 'انقضا'], ['Theft', 'سرقت'], ['CountError', 'خطای شمارش'], ['Other', 'سایر (با توضیح)']];
const reasonFa = (r: string) => REASONS.find(([k]) => k === r)?.[1] ?? r;
const latin = (v: string) => v.replace(/[۰-۹]/g, (d) => String('۰۱۲۳۴۵۶۷۸۹'.indexOf(d))).replace(/[٫]/g, '.').replace(/[−–]/g, '-').replace(/\s/g, '');
const rial = (v: string) => Number(v).toLocaleString('fa-IR');

const ERRORS: Record<string, string> = {
  'unknown-sku': 'کالایی با این SKU پیدا نشد.',
  'insufficient-stock': 'موجودی کافی نیست؛ مقدار کاهش از موجودی فعلی بیشتر است.',
  'no-open-period': 'دوره مالی باز برای امروز وجود ندارد.',
  'account-role-unmapped': 'حساب کسری یا اضافی انبار در نگاشت حساب‌ها تعیین نشده است.',
};

/**
 * US-063 / INV-002 / BR-033: a manual adjustment with a reason. Lines are signed (−3: three fewer). An adjustment worth
 * more than the store's threshold waits for another manager's approval and is then sent again, unchanged, with it.
 */
export default function AdjustmentsPage() {
  const [warehouses, setWarehouses] = useState<Warehouse[]>([]);
  const [list, setList] = useState<Adjustment[] | null>(null);
  const [form, setForm] = useState({ warehouseId: '', reason: 'Damage', note: '' });
  const [lines, setLines] = useState<Line[]>([{ sku: '', delta: '' }]);
  const [pending, setPending] = useState<{ approvalId: string; key: string; body: string; value: string } | null>(null);
  const [error, setError] = useState('');
  const [done, setDone] = useState<Adjustment | null>(null);

  async function load() {
    const w = await fetch('/bff/warehouses', { cache: 'no-store' });
    if (w.ok) {
      const items: Warehouse[] = (await w.json()).items;
      setWarehouses(items);
      setForm((f) => (f.warehouseId || items.length === 0 ? f : { ...f, warehouseId: items[0].id }));
    }
    const a = await fetch('/bff/inventory/adjustments', { cache: 'no-store' });
    setList(a.ok ? (await a.json()).items : null);   // the list needs reports.view
  }
  useEffect(() => { void load(); }, []);

  async function variantOf(sku: string): Promise<string | undefined> {
    const r = await fetch(`/bff/products?q=${encodeURIComponent(sku)}`, { cache: 'no-store' });
    const products: Product[] = r.ok ? (await r.json()).items : [];
    return products.flatMap((p) => p.variants).find((v) => v.sku === sku)?.id;
  }

  async function send(body: string, key: string, approvalId?: string) {
    setError('');
    const headers: Record<string, string> = { 'content-type': 'application/json', 'x-csrf-token': csrf(), 'idempotency-key': key };
    if (approvalId) headers['x-approval-id'] = approvalId;
    const r = await fetch('/bff/inventory/adjustments', { method: 'POST', headers, body });
    if (r.status === 201) {
      setDone(await r.json());
      setPending(null);
      setLines([{ sku: '', delta: '' }]);
      setForm((f) => ({ ...f, note: '' }));
      void load();
      return;
    }
    const p = await r.clone().json().catch(() => null) as { code?: string; approvalRequestId?: string; context?: { value?: string } } | null;
    if (p?.code === 'approval-required' && p.approvalRequestId) {
      setPending({ approvalId: p.approvalRequestId, key, body, value: p.context?.value ?? '' });
      if (approvalId) setError('approval-not-yet');
      return;
    }
    setError(await codeOf(r));
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setDone(null);
    const resolved = [];
    for (const l of lines.filter((x) => x.sku.trim() || x.delta.trim())) {
      const variantId = await variantOf(l.sku.trim());
      if (!variantId) return setError('unknown-sku');
      resolved.push({ variantId, qtyDelta: latin(l.delta) });
    }
    const body = JSON.stringify({ warehouseId: form.warehouseId, reason: form.reason, note: form.note.trim() || undefined, lines: resolved });
    await send(body, crypto.randomUUID());
  }

  const setLine = (i: number, k: keyof Line) => (e: React.ChangeEvent<HTMLInputElement>) =>
    setLines(lines.map((l, j) => (j === i ? { ...l, [k]: e.target.value } : l)));
  const code = (id: string) => warehouses.find((w) => w.id === id)?.code ?? '';

  return (
    <section>
      <h1>تعدیل موجودی</h1>
      <p>برای کاهش، مقدار را با منفی بنویسید (مثلاً ‎-3‎). تعدیل با ارزش بیشتر از آستانه فروشگاه، تأیید مدیر دیگری لازم دارد.</p>
      <form onSubmit={(e) => void submit(e)} data-testid="adjust-form">
        <label>انبار{' '}
          <select value={form.warehouseId} onChange={(e) => setForm({ ...form, warehouseId: e.target.value })} data-testid="adjust-warehouse">
            {warehouses.map((w) => <option key={w.id} value={w.id}>{w.code}</option>)}
          </select>
        </label>{' '}
        <label>دلیل{' '}
          <select value={form.reason} onChange={(e) => setForm({ ...form, reason: e.target.value })} data-testid="adjust-reason">
            {REASONS.map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
        </label>{' '}
        <label>توضیح <input value={form.note} onChange={(e) => setForm({ ...form, note: e.target.value })} maxLength={500}
          required={form.reason === 'Other'} data-testid="adjust-note" /></label>
        {lines.map((l, i) => (
          <p key={i}>
            <label>SKU <input required value={l.sku} onChange={setLine(i, 'sku')} dir="ltr" size={12} data-testid={`adjust-sku-${i}`} /></label>{' '}
            <label>تغییر مقدار <input required value={l.delta} onChange={setLine(i, 'delta')} dir="ltr" size={8} data-testid={`adjust-delta-${i}`} /></label>
          </p>
        ))}
        <button type="button" style={{ minHeight: 44 }} onClick={() => setLines([...lines, { sku: '', delta: '' }])}>ردیف دیگر</button>{' '}
        <button type="submit" style={{ minHeight: 44 }} data-testid="adjust-submit">ثبت تعدیل</button>
      </form>
      {pending && (
        <p role="status" data-testid="adjust-pending">
          ارزش این تعدیل {pending.value ? `${rial(pending.value)} ریال` : ''} است و تأیید مدیر دیگری لازم دارد (<a href="/approvals">تأییدها</a>).{' '}
          <button type="button" style={{ minHeight: 44 }} onClick={() => void send(pending.body, pending.key, pending.approvalId)}
            data-testid="adjust-retry">ثبت پس از تأیید</button>
        </p>
      )}
      {done && <p role="status" data-testid="adjust-done">تعدیل ثبت شد؛ ارزش {rial(done.value)} ریال{done.approvalId ? ' (با تأیید)' : ''}.</p>}
      {error && <p role="alert" data-testid="adjust-error">{ERRORS[error] ?? MESSAGES[error] ?? error}</p>}

      <h2>تعدیل‌های اخیر</h2>
      {list === null ? <p>دیدن فهرست و ارزش تعدیل‌ها مجوز گزارش لازم دارد.</p> : (
        <ul data-testid="adjust-list">
          {list.map((a) => (
            <li key={a.id} data-reason={a.reason}>
              {new Date(a.postedAt).toLocaleString('fa-IR')} · {code(a.warehouseId)} · {reasonFa(a.reason)}{a.note ? ` (${a.note})` : ''} ·{' '}
              {a.lineCount.toLocaleString('fa-IR')} ردیف · {rial(a.value)} ریال{a.approvalId ? ' · با تأیید' : ''}
            </li>
          ))}
          {list.length === 0 && <li>هنوز تعدیلی ثبت نشده است.</li>}
        </ul>
      )}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
