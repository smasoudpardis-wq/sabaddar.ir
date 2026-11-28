'use client';
import { useEffect, useState } from 'react';
import { codeOf, csrf, MESSAGES } from '@/lib/client';

type Warehouse = { id: string; code: string };
type Transfer = { id: string; fromWarehouseId: string; toWarehouseId: string; lineCount: number; value: string; note: string | null; postedAt: string };
type Product = { id: string; name: string; variants: { id: string; sku: string }[] };

const latin = (v: string) => v.replace(/[۰-۹]/g, (d) => String('۰۱۲۳۴۵۶۷۸۹'.indexOf(d))).replace(/[٫]/g, '.').trim();
const rial = (v: string) => Number(v).toLocaleString('fa-IR');
const ERRORS: Record<string, string> = {
  'unknown-sku': 'کالایی با این SKU پیدا نشد.',
  'insufficient-stock': 'موجودی انبار مبدأ کافی نیست.',
};

/** US-064 / INV-008: move stock between two warehouses in one step; it arrives at the source's average cost. */
export default function TransfersPage() {
  const [warehouses, setWarehouses] = useState<Warehouse[]>([]);
  const [list, setList] = useState<Transfer[] | null>(null);
  const [form, setForm] = useState({ from: '', to: '', sku: '', qty: '', note: '' });
  const [done, setDone] = useState<Transfer | null>(null);
  const [error, setError] = useState('');

  async function load() {
    const w = await fetch('/bff/warehouses', { cache: 'no-store' });
    if (w.ok) {
      const items: Warehouse[] = (await w.json()).items;
      setWarehouses(items);
      setForm((f) => (f.from || items.length < 2 ? f : { ...f, from: items[0].id, to: items[1].id }));
    }
    const t = await fetch('/bff/inventory/transfers', { cache: 'no-store' });
    setList(t.ok ? (await t.json()).items : null);
  }
  useEffect(() => { void load(); }, []);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setDone(null);
    const found = await fetch(`/bff/products?q=${encodeURIComponent(form.sku.trim())}`, { cache: 'no-store' });
    const variantId = (found.ok ? ((await found.json()).items as Product[]) : []).flatMap((p) => p.variants).find((v) => v.sku === form.sku.trim())?.id;
    if (!variantId) return setError('unknown-sku');
    const r = await fetch('/bff/inventory/transfers', {
      method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf(), 'idempotency-key': crypto.randomUUID() },
      body: JSON.stringify({ fromWarehouseId: form.from, toWarehouseId: form.to, note: form.note.trim() || undefined,
        lines: [{ variantId, qty: latin(form.qty) }] }),
    });
    if (!r.ok) return setError(await codeOf(r));
    setDone(await r.json());
    setForm({ ...form, sku: '', qty: '', note: '' });
    void load();
  }

  const code = (id: string) => warehouses.find((w) => w.id === id)?.code ?? '';
  const select = (k: 'from' | 'to', label: string) => (
    <label>{label}{' '}
      <select value={form[k]} onChange={(e) => setForm({ ...form, [k]: e.target.value })} data-testid={`transfer-${k}`}>
        {warehouses.map((w) => <option key={w.id} value={w.id}>{w.code}</option>)}
      </select>
    </label>
  );
  return (
    <section>
      <h1>انتقال بین انبارها</h1>
      {warehouses.length < 2 ? <p>برای انتقال دست‌کم دو انبار لازم است.</p> : (
        <form onSubmit={(e) => void submit(e)} data-testid="transfer-form">
          {select('from', 'از')}{' '}{select('to', 'به')}{' '}
          <label>SKU <input required value={form.sku} onChange={(e) => setForm({ ...form, sku: e.target.value })} dir="ltr" size={12} data-testid="transfer-sku" /></label>{' '}
          <label>مقدار <input required value={form.qty} onChange={(e) => setForm({ ...form, qty: e.target.value })} dir="ltr" size={6} inputMode="decimal"
            data-testid="transfer-qty" /></label>{' '}
          <label>توضیح <input value={form.note} onChange={(e) => setForm({ ...form, note: e.target.value })} maxLength={500} /></label>{' '}
          <button type="submit" style={{ minHeight: 44 }} data-testid="transfer-submit">ثبت انتقال</button>
        </form>
      )}
      {done && <p role="status" data-testid="transfer-done">انتقال ثبت شد؛ ارزش {rial(done.value)} ریال با بهای میانگین مبدأ.</p>}
      {error && <p role="alert" data-testid="transfer-error">{ERRORS[error] ?? MESSAGES[error] ?? error}</p>}
      <h2>انتقال‌های اخیر</h2>
      {list === null ? <p>دیدن فهرست انتقال‌ها مجوز گزارش لازم دارد.</p> : (
        <ul data-testid="transfer-list">
          {list.map((t) => (
            <li key={t.id} data-from={code(t.fromWarehouseId)} data-to={code(t.toWarehouseId)}>
              {new Date(t.postedAt).toLocaleString('fa-IR')} · {code(t.fromWarehouseId)} ← {code(t.toWarehouseId)} · {t.lineCount.toLocaleString('fa-IR')} ردیف ·{' '}
              {rial(t.value)} ریال{t.note ? ` (${t.note})` : ''}
            </li>
          ))}
          {list.length === 0 && <li>هنوز انتقالی ثبت نشده است.</li>}
        </ul>
      )}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
