'use client';
import { useEffect, useState } from 'react';
import { codeOf, csrf, MESSAGES, postJson } from '@/lib/client';

type Warehouse = { id: string; code: string };
type Supplier = { id: string; name: string; nationalId: string | null; balance: string };
type Product = { id: string; name: string; variants: { id: string; sku: string }[] };
type ReceiptLine = { lineNo: number; variantId: string; qty: string; unit: string; lineCost: string; newWac: string | null };
type Receipt = { id: string; supplierId: string; warehouseId: string; supplierInvoiceNo: string | null; total: string; lines: ReceiptLine[]; postedAt: string };
type Line = { sku: string; qty: string; unit: string; unitCost: string; discount: string; inputVat: string };

const EMPTY: Line = { sku: '', qty: '', unit: 'EA', unitCost: '', discount: '', inputVat: '' };
const UNITS: [string, string][] = [['EA', 'عدد'], ['PK', 'بسته'], ['CT', 'کارتن'], ['KG', 'کیلوگرم']];
const digits = (v: string) => v.replace(/[۰-۹]/g, (d) => String('۰۱۲۳۴۵۶۷۸۹'.indexOf(d))).replace(/[٫]/g, '.').replace(/[,٬\s]/g, '');
const rial = (v: string) => Number(v).toLocaleString('fa-IR');

const WARNINGS: Record<string, string> = {
  'zero-cost': 'ردیفی با بهای صفر دارد (هدیه یا جایزه؟)',
  'duplicate-supplier-invoice': 'این شماره فاکتور قبلاً از همین تأمین‌کننده ثبت شده است',
};
const ERRORS: Record<string, string> = {
  'unknown-sku': 'کالایی با این SKU پیدا نشد.',
  'unknown-unit': 'این واحد برای کالا تعریف نشده است (بارکد بسته‌بندی با ضریب لازم است).',
  'duplicate-national-id': 'تأمین‌کننده‌ای با این شناسه ملی وجود دارد.',
  'no-open-period': 'دوره مالی باز برای امروز وجود ندارد.',
  'account-role-unmapped': 'حساب موجودی، مالیات خرید یا پرداختنی در نگاشت حساب‌ها تعیین نشده است.',
};

/**
 * US-071 / PRC-001 / PRC-002 / UC-05: suppliers and goods receipts. A receipt moves the stock, re-averages the cost and
 * books what the store owes the supplier, all at once; a zero cost or a repeated invoice number is confirmed first.
 */
export default function ReceiptsPage() {
  const [warehouses, setWarehouses] = useState<Warehouse[]>([]);
  const [suppliers, setSuppliers] = useState<Supplier[]>([]);
  const [receipts, setReceipts] = useState<Receipt[] | null>(null);
  const [newSupplier, setNewSupplier] = useState({ name: '', nationalId: '', phone: '' });
  const [form, setForm] = useState({ supplierId: '', warehouseId: '', invoice: '' });
  const [lines, setLines] = useState<Line[]>([{ ...EMPTY }]);
  const [confirm, setConfirm] = useState<{ body: Record<string, unknown>; warnings: string[] } | null>(null);
  const [done, setDone] = useState<Receipt | null>(null);
  const [error, setError] = useState('');

  async function load() {
    const w = await fetch('/bff/warehouses', { cache: 'no-store' });
    if (w.ok) {
      const items: Warehouse[] = (await w.json()).items;
      setWarehouses(items);
      setForm((f) => (f.warehouseId || items.length === 0 ? f : { ...f, warehouseId: items[0].id }));
    }
    const s = await fetch('/bff/suppliers', { cache: 'no-store' });
    if (s.ok) setSuppliers((await s.json()).items);
    const r = await fetch('/bff/inventory/receipts', { cache: 'no-store' });
    setReceipts(r.ok ? (await r.json()).items : null);
  }
  useEffect(() => { void load(); }, []);

  async function addSupplier(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    const r = await postJson('/bff/suppliers', {
      name: newSupplier.name, nationalId: digits(newSupplier.nationalId) || undefined, phone: digits(newSupplier.phone) || undefined,
    });
    if (!r.ok) return setError(await codeOf(r));
    const created: Supplier = await r.json();
    setNewSupplier({ name: '', nationalId: '', phone: '' });
    setForm((f) => ({ ...f, supplierId: created.id }));
    void load();
  }

  async function send(body: Record<string, unknown>) {
    setError('');
    const r = await fetch('/bff/inventory/receipts', {
      method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf(), 'idempotency-key': crypto.randomUUID() },
      body: JSON.stringify(body),
    });
    if (r.status === 201) {
      setDone(await r.json());
      setConfirm(null);
      setLines([{ ...EMPTY }]);
      setForm((f) => ({ ...f, invoice: '' }));
      void load();
      return;
    }
    const p = await r.clone().json().catch(() => null) as { code?: string; context?: { warnings?: string[] } } | null;
    if (p?.code === 'confirmation-required' && p.context?.warnings) {
      setConfirm({ body, warnings: [...((body.confirmedWarnings as string[] | undefined) ?? []), ...p.context.warnings] });
      return;
    }
    setError(await codeOf(r));
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setDone(null);
    const resolved = [];
    for (const l of lines) {
      const found = await fetch(`/bff/products?q=${encodeURIComponent(l.sku.trim())}`, { cache: 'no-store' });
      const products: Product[] = found.ok ? (await found.json()).items : [];
      const variantId = products.flatMap((p) => p.variants).find((v) => v.sku === l.sku.trim())?.id;
      if (!variantId) return setError('unknown-sku');
      resolved.push({
        variantId, qty: digits(l.qty), unit: l.unit, unitCost: digits(l.unitCost),
        discount: digits(l.discount) || undefined, inputVat: digits(l.inputVat) || undefined,
      });
    }
    await send({ supplierId: form.supplierId, warehouseId: form.warehouseId, supplierInvoiceNo: form.invoice.trim() || undefined, lines: resolved });
  }

  const setLine = (i: number, k: keyof Line) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) =>
    setLines(lines.map((l, j) => (j === i ? { ...l, [k]: e.target.value } : l)));
  const supplierName = (id: string) => suppliers.find((s) => s.id === id)?.name ?? '';

  return (
    <section>
      <h1>رسید خرید</h1>
      <h2>تأمین‌کنندگان</h2>
      <ul data-testid="suppliers">
        {suppliers.map((s) => <li key={s.id} data-supplier={s.name}>{s.name}{s.nationalId ? ` (${s.nationalId})` : ''} · بدهی ما: {rial(s.balance)} ریال</li>)}
        {suppliers.length === 0 && <li>هنوز تأمین‌کننده‌ای تعریف نشده است.</li>}
      </ul>
      <form onSubmit={(e) => void addSupplier(e)} data-testid="supplier-form">
        <label>نام <input required value={newSupplier.name} onChange={(e) => setNewSupplier({ ...newSupplier, name: e.target.value })} data-testid="supplier-name" /></label>{' '}
        <label>شناسه ملی (اختیاری) <input value={newSupplier.nationalId} onChange={(e) => setNewSupplier({ ...newSupplier, nationalId: e.target.value })} dir="ltr" size={11} /></label>{' '}
        <label>تلفن (اختیاری) <input value={newSupplier.phone} onChange={(e) => setNewSupplier({ ...newSupplier, phone: e.target.value })} dir="ltr" size={11} /></label>{' '}
        <button type="submit" style={{ minHeight: 44 }} data-testid="supplier-submit">افزودن تأمین‌کننده</button>
      </form>

      <h2>ثبت رسید</h2>
      <form onSubmit={(e) => void submit(e)} data-testid="receipt-form">
        <label>تأمین‌کننده{' '}
          <select required value={form.supplierId} onChange={(e) => setForm({ ...form, supplierId: e.target.value })} data-testid="receipt-supplier">
            <option value="">انتخاب کنید</option>
            {suppliers.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
          </select>
        </label>{' '}
        <label>انبار{' '}
          <select value={form.warehouseId} onChange={(e) => setForm({ ...form, warehouseId: e.target.value })} data-testid="receipt-warehouse">
            {warehouses.map((w) => <option key={w.id} value={w.id}>{w.code}</option>)}
          </select>
        </label>{' '}
        <label>شماره فاکتور تأمین‌کننده <input value={form.invoice} onChange={(e) => setForm({ ...form, invoice: e.target.value })} dir="ltr" size={10}
          data-testid="receipt-invoice" /></label>
        {lines.map((l, i) => (
          <p key={i}>
            <label>SKU <input required value={l.sku} onChange={setLine(i, 'sku')} dir="ltr" size={12} data-testid={`receipt-sku-${i}`} /></label>{' '}
            <label>مقدار <input required value={l.qty} onChange={setLine(i, 'qty')} dir="ltr" size={6} inputMode="decimal" data-testid={`receipt-qty-${i}`} /></label>{' '}
            <label>واحد{' '}
              <select value={l.unit} onChange={setLine(i, 'unit')} data-testid={`receipt-unit-${i}`}>
                {UNITS.map(([k, v]) => <option key={k} value={k}>{v}</option>)}
              </select>
            </label>{' '}
            <label>بهای واحد (ریال) <input required value={l.unitCost} onChange={setLine(i, 'unitCost')} dir="ltr" size={10} inputMode="numeric"
              data-testid={`receipt-cost-${i}`} /></label>{' '}
            <label>تخفیف <input value={l.discount} onChange={setLine(i, 'discount')} dir="ltr" size={8} inputMode="numeric" /></label>{' '}
            <label>مالیات خرید <input value={l.inputVat} onChange={setLine(i, 'inputVat')} dir="ltr" size={8} inputMode="numeric"
              data-testid={`receipt-vat-${i}`} /></label>
          </p>
        ))}
        <button type="button" style={{ minHeight: 44 }} onClick={() => setLines([...lines, { ...EMPTY }])}>ردیف دیگر</button>{' '}
        <button type="submit" style={{ minHeight: 44 }} data-testid="receipt-submit">ثبت رسید</button>
      </form>
      {confirm && (
        <div role="alert" data-testid="receipt-confirm">
          <p>پیش از ثبت تأیید کنید:</p>
          <ul>{confirm.warnings.map((w) => <li key={w}>{WARNINGS[w] ?? w}</li>)}</ul>
          <button type="button" style={{ minHeight: 44 }} data-testid="receipt-confirm-submit"
            onClick={() => void send({ ...confirm.body, confirmedWarnings: confirm.warnings })}>تأیید و ثبت</button>{' '}
          <button type="button" style={{ minHeight: 44 }} onClick={() => setConfirm(null)}>انصراف</button>
        </div>
      )}
      {done && (
        <p role="status" data-testid="receipt-done">
          رسید ثبت شد؛ جمع {rial(done.total)} ریال.{' '}
          {done.lines.map((l) => (l.newWac ? `بهای میانگین جدید ردیف ${l.lineNo.toLocaleString('fa-IR')}: ${rial(l.newWac)} ریال. ` : ''))}
        </p>
      )}
      {error && <p role="alert" data-testid="receipt-error">{ERRORS[error] ?? MESSAGES[error] ?? error}</p>}

      <h2>رسیدهای اخیر</h2>
      {receipts === null ? <p>دیدن فهرست رسیدها مجوز گزارش لازم دارد.</p> : (
        <ul data-testid="receipt-list">
          {receipts.map((r) => (
            <li key={r.id} data-invoice={r.supplierInvoiceNo ?? ''}>
              {new Date(r.postedAt).toLocaleString('fa-IR')} · {supplierName(r.supplierId)}{r.supplierInvoiceNo ? ` · فاکتور ${r.supplierInvoiceNo}` : ''} ·{' '}
              {r.lines.length.toLocaleString('fa-IR')} ردیف · {rial(r.total)} ریال
            </li>
          ))}
          {receipts.length === 0 && <li>هنوز رسیدی ثبت نشده است.</li>}
        </ul>
      )}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
