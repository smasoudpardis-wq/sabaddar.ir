'use client';
import { useEffect, useState } from 'react';
import { codeOf, csrf, MESSAGES } from '@/lib/client';

type Warehouse = { id: string; code: string };
type Low = { warehouseId: string; variantId: string; name: string | null; sku: string | null; onHand: string; available: string; reorderPoint: string };
type Product = { id: string; variants: { id: string; sku: string }[] };

const latin = (v: string) => v.replace(/[۰-۹]/g, (d) => String('۰۱۲۳۴۵۶۷۸۹'.indexOf(d))).replace(/[٫]/g, '.').trim();
const num = (v: string) => Number(v).toLocaleString('fa-IR');

/** US-066 / INV-010: what is at or below its reorder point, and setting the point per item and warehouse. */
export default function LowStockPage() {
  const [warehouses, setWarehouses] = useState<Warehouse[]>([]);
  const [low, setLow] = useState<Low[] | null>(null);
  const [form, setForm] = useState({ warehouseId: '', sku: '', point: '' });
  const [error, setError] = useState('');
  const [saved, setSaved] = useState(false);

  async function load() {
    const w = await fetch('/bff/warehouses', { cache: 'no-store' });
    if (w.ok) {
      const items: Warehouse[] = (await w.json()).items;
      setWarehouses(items);
      setForm((f) => (f.warehouseId || items.length === 0 ? f : { ...f, warehouseId: items[0].id }));
    }
    const r = await fetch('/bff/inventory/low', { cache: 'no-store' });
    setLow(r.ok ? (await r.json()).items : null);
  }
  useEffect(() => { void load(); }, []);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setSaved(false);
    const found = await fetch(`/bff/products?q=${encodeURIComponent(form.sku.trim())}`, { cache: 'no-store' });
    const variantId = (found.ok ? ((await found.json()).items as Product[]) : []).flatMap((p) => p.variants).find((v) => v.sku === form.sku.trim())?.id;
    if (!variantId) return setError('unknown-sku');
    const r = await fetch('/bff/inventory/reorder-points', {
      method: 'PUT', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() },
      body: JSON.stringify({ warehouseId: form.warehouseId, variantId, reorderPoint: form.point.trim() ? latin(form.point) : null }),
    });
    if (!r.ok) return setError(await codeOf(r));
    setSaved(true);
    setForm({ ...form, sku: '', point: '' });
    void load();
  }

  const code = (id: string) => warehouses.find((w) => w.id === id)?.code ?? '';
  return (
    <section>
      <h1>موجودی کم</h1>
      {low === null ? <p>دیدن موجودی کم مجوز دیدن موجودی لازم دارد.</p> : (
        <>
          <p data-testid="low-count" data-count={low.length}>{low.length === 0 ? 'هیچ کالایی به نقطه سفارش نرسیده است.' : `${num(String(low.length))} قلم به نقطه سفارش رسیده یا پایین‌تر است.`}</p>
          <table data-testid="low-list">
            <thead><tr><th>کالا</th><th>انبار</th><th>قابل فروش</th><th>نقطه سفارش</th></tr></thead>
            <tbody>
              {low.map((l) => (
                <tr key={`${l.warehouseId}-${l.variantId}`} data-sku={l.sku ?? ''}>
                  <td>{l.name ?? l.variantId} {l.sku ? <span dir="ltr">({l.sku})</span> : null}</td>
                  <td>{code(l.warehouseId)}</td><td>{num(l.available)}</td><td>{num(l.reorderPoint)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
      <h2>تعیین نقطه سفارش</h2>
      <form onSubmit={(e) => void save(e)} data-testid="reorder-form">
        <label>انبار{' '}
          <select value={form.warehouseId} onChange={(e) => setForm({ ...form, warehouseId: e.target.value })}>
            {warehouses.map((w) => <option key={w.id} value={w.id}>{w.code}</option>)}
          </select>
        </label>{' '}
        <label>SKU <input required value={form.sku} onChange={(e) => setForm({ ...form, sku: e.target.value })} dir="ltr" size={12} data-testid="reorder-sku" /></label>{' '}
        <label>نقطه سفارش (خالی: حذف) <input value={form.point} onChange={(e) => setForm({ ...form, point: e.target.value })} dir="ltr" size={6}
          inputMode="decimal" data-testid="reorder-point" /></label>{' '}
        <button type="submit" style={{ minHeight: 44 }} data-testid="reorder-submit">ذخیره</button>
      </form>
      {saved && <p role="status" data-testid="reorder-saved">ذخیره شد.</p>}
      {error && <p role="alert">{error === 'unknown-sku' ? 'کالایی با این SKU پیدا نشد.' : MESSAGES[error] ?? error}</p>}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
