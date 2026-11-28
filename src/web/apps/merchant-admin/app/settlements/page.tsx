'use client';
import { useEffect, useState } from 'react';
import { codeOf, csrf, MESSAGES } from '@/lib/client';

type Warehouse = { id: string; code: string };
type Supplier = { id: string; name: string; balance: string };
type Product = { id: string; name: string; variants: { id: string; sku: string }[] };
type Return = { id: string; supplierId: string; cost: string; agreedAmount: string; variance: string; postedAt: string; lines: { qty: string }[] };
type Line = { sku: string; qty: string };
type Pending = { url: string; body: Record<string, unknown>; owed: string };

const digits = (v: string) => v.replace(/[۰-۹]/g, (d) => String('۰۱۲۳۴۵۶۷۸۹'.indexOf(d))).replace(/[٫]/g, '.').replace(/[,٬\s]/g, '');
const rial = (v: string) => Number(v).toLocaleString('fa-IR');
const ERRORS: Record<string, string> = {
  'unknown-sku': 'کالایی با این SKU پیدا نشد.',
  'insufficient-stock': 'موجودی انبار برای برگشت کافی نیست.',
  'no-open-period': 'دوره مالی باز برای امروز وجود ندارد.',
  'account-role-unmapped': 'حساب لازم برای این سند در نگاشت حساب‌ها تعیین نشده است.',
};

/**
 * US-072 / PRC-003 / PRC-004: paying a supplier (cash or bank) and sending goods back. A return leaves stock at the
 * current average cost; what the supplier agrees to take off the bill may differ, and the gap is booked separately.
 */
export default function SettlementsPage() {
  const [warehouses, setWarehouses] = useState<Warehouse[]>([]);
  const [suppliers, setSuppliers] = useState<Supplier[]>([]);
  const [returns, setReturns] = useState<Return[] | null>(null);
  const [pay, setPay] = useState({ supplierId: '', amount: '', method: 'Cash', reference: '', note: '' });
  const [ret, setRet] = useState({ supplierId: '', warehouseId: '', agreed: '', note: '' });
  const [lines, setLines] = useState<Line[]>([{ sku: '', qty: '' }]);
  const [pending, setPending] = useState<Pending | null>(null);
  const [done, setDone] = useState('');
  const [error, setError] = useState('');

  async function load() {
    const w = await fetch('/bff/warehouses', { cache: 'no-store' });
    if (w.ok) {
      const items: Warehouse[] = (await w.json()).items;
      setWarehouses(items);
      setRet((f) => (f.warehouseId || items.length === 0 ? f : { ...f, warehouseId: items[0].id }));
    }
    const s = await fetch('/bff/suppliers', { cache: 'no-store' });
    if (s.ok) setSuppliers((await s.json()).items);
    const r = await fetch('/bff/purchase-returns', { cache: 'no-store' });
    setReturns(r.ok ? (await r.json()).items : null);
  }
  useEffect(() => { void load(); }, []);

  async function send(url: string, body: Record<string, unknown>, ok: (r: Record<string, string>) => string) {
    setError('');
    setDone('');
    const r = await fetch(url, {
      method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf(), 'idempotency-key': crypto.randomUUID() },
      body: JSON.stringify(body),
    });
    if (r.status === 201) {
      setDone(ok(await r.json()));
      setPending(null);
      void load();
      return true;
    }
    const p = await r.clone().json().catch(() => null) as { code?: string; context?: { warnings?: string[]; owed?: string } } | null;
    if (p?.code === 'confirmation-required' && p.context?.warnings?.includes('exceeds-balance')) {
      setPending({ url, body: { ...body, confirmedWarnings: ['exceeds-balance'] }, owed: p.context.owed ?? '0' });
      return false;
    }
    setError(await codeOf(r));
    return false;
  }

  const paid = (r: Record<string, string>) => `پرداخت ثبت شد؛ مانده بدهی ما: ${rial(r.balanceAfter)} ریال.`;
  const returned = (r: Record<string, string>) =>
    `برگشت ثبت شد؛ بهای کالا ${rial(r.cost)} ریال، مبلغ توافقی ${rial(r.agreedAmount)} ریال، اختلاف ${rial(r.variance)} ریال؛ مانده بدهی ما: ${rial(r.balanceAfter)} ریال.`;

  async function submitPay(e: React.FormEvent) {
    e.preventDefault();
    const sent = await send(`/bff/suppliers/${pay.supplierId}/payments`, {
      amount: digits(pay.amount), method: pay.method, reference: pay.reference.trim() || undefined, note: pay.note.trim() || undefined,
    }, paid);
    if (sent) setPay({ ...pay, amount: '', reference: '', note: '' });
  }

  async function submitReturn(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    const resolved = [];
    for (const l of lines) {
      const found = await fetch(`/bff/products?q=${encodeURIComponent(l.sku.trim())}`, { cache: 'no-store' });
      const products: Product[] = found.ok ? (await found.json()).items : [];
      const variantId = products.flatMap((p) => p.variants).find((v) => v.sku === l.sku.trim())?.id;
      if (!variantId) return setError('unknown-sku');
      resolved.push({ variantId, qty: digits(l.qty) });
    }
    const sent = await send('/bff/purchase-returns', {
      supplierId: ret.supplierId, warehouseId: ret.warehouseId, agreedAmount: digits(ret.agreed), note: ret.note.trim() || undefined, lines: resolved,
    }, returned);
    if (sent) {
      setLines([{ sku: '', qty: '' }]);
      setRet({ ...ret, agreed: '', note: '' });
    }
  }

  const supplierName = (id: string) => suppliers.find((s) => s.id === id)?.name ?? '';
  const supplierSelect = (value: string, set: (v: string) => void, testid: string) => (
    <select required value={value} onChange={(e) => set(e.target.value)} data-testid={testid}>
      <option value="">انتخاب کنید</option>
      {suppliers.map((s) => <option key={s.id} value={s.id}>{s.name} (بدهی ما: {rial(s.balance)})</option>)}
    </select>
  );

  return (
    <section>
      <h1>پرداخت و برگشت از خرید</h1>
      <ul data-testid="settlement-suppliers">
        {suppliers.map((s) => <li key={s.id} data-supplier={s.name} data-balance={s.balance}>{s.name} · بدهی ما: {rial(s.balance)} ریال</li>)}
        {suppliers.length === 0 && <li>هنوز تأمین‌کننده‌ای نیست؛ از صفحه «رسید خرید» اضافه کنید.</li>}
      </ul>

      <h2>پرداخت به تأمین‌کننده</h2>
      <form onSubmit={(e) => void submitPay(e)} data-testid="pay-form">
        <label>تأمین‌کننده {supplierSelect(pay.supplierId, (v) => setPay({ ...pay, supplierId: v }), 'pay-supplier')}</label>{' '}
        <label>مبلغ (ریال) <input required value={pay.amount} onChange={(e) => setPay({ ...pay, amount: e.target.value })} dir="ltr" size={12} inputMode="numeric"
          data-testid="pay-amount" /></label>{' '}
        <label>روش{' '}
          <select value={pay.method} onChange={(e) => setPay({ ...pay, method: e.target.value })} data-testid="pay-method">
            <option value="Cash">نقد (صندوق)</option>
            <option value="Bank">بانک (حواله یا چک)</option>
          </select>
        </label>{' '}
        <label>شماره حواله یا چک <input required={pay.method === 'Bank'} value={pay.reference} onChange={(e) => setPay({ ...pay, reference: e.target.value })}
          dir="ltr" size={12} data-testid="pay-reference" /></label>{' '}
        <label>توضیح <input value={pay.note} onChange={(e) => setPay({ ...pay, note: e.target.value })} size={20} /></label>{' '}
        <button type="submit" style={{ minHeight: 44 }} data-testid="pay-submit">ثبت پرداخت</button>
      </form>

      <h2>برگشت از خرید</h2>
      <form onSubmit={(e) => void submitReturn(e)} data-testid="return-form">
        <label>تأمین‌کننده {supplierSelect(ret.supplierId, (v) => setRet({ ...ret, supplierId: v }), 'return-supplier')}</label>{' '}
        <label>انبار{' '}
          <select value={ret.warehouseId} onChange={(e) => setRet({ ...ret, warehouseId: e.target.value })} data-testid="return-warehouse">
            {warehouses.map((w) => <option key={w.id} value={w.id}>{w.code}</option>)}
          </select>
        </label>
        {lines.map((l, i) => (
          <p key={i}>
            <label>SKU <input required value={l.sku} onChange={(e) => setLines(lines.map((x, j) => (j === i ? { ...x, sku: e.target.value } : x)))}
              dir="ltr" size={12} data-testid={`return-sku-${i}`} /></label>{' '}
            <label>تعداد (واحد پایه) <input required value={l.qty} onChange={(e) => setLines(lines.map((x, j) => (j === i ? { ...x, qty: e.target.value } : x)))}
              dir="ltr" size={6} inputMode="decimal" data-testid={`return-qty-${i}`} /></label>
          </p>
        ))}
        <button type="button" style={{ minHeight: 44 }} onClick={() => setLines([...lines, { sku: '', qty: '' }])}>ردیف دیگر</button>{' '}
        <label>مبلغ توافقی با تأمین‌کننده (ریال) <input required value={ret.agreed} onChange={(e) => setRet({ ...ret, agreed: e.target.value })} dir="ltr" size={12}
          inputMode="numeric" data-testid="return-agreed" /></label>{' '}
        <label>توضیح <input value={ret.note} onChange={(e) => setRet({ ...ret, note: e.target.value })} size={20} /></label>{' '}
        <button type="submit" style={{ minHeight: 44 }} data-testid="return-submit">ثبت برگشت</button>
      </form>

      {pending && (
        <div role="alert" data-testid="settlement-confirm">
          <p>این مبلغ از بدهی ما به این تأمین‌کننده ({rial(pending.owed)} ریال) بیشتر است؛ پس از ثبت، تأمین‌کننده به ما بدهکار می‌شود.</p>
          <button type="button" style={{ minHeight: 44 }} data-testid="settlement-confirm-submit"
            onClick={() => void send(pending.url, pending.body, pending.url.includes('/payments') ? paid : returned)}>تأیید و ثبت</button>{' '}
          <button type="button" style={{ minHeight: 44 }} onClick={() => setPending(null)}>انصراف</button>
        </div>
      )}
      {done && <p role="status" data-testid="settlement-done">{done}</p>}
      {error && <p role="alert" data-testid="settlement-error">{ERRORS[error] ?? MESSAGES[error] ?? error}</p>}

      <h2>برگشت‌های اخیر</h2>
      {returns === null ? <p>دیدن فهرست برگشت‌ها مجوز گزارش لازم دارد.</p> : (
        <ul data-testid="return-list">
          {returns.map((r) => (
            <li key={r.id} data-agreed={r.agreedAmount}>
              {new Date(r.postedAt).toLocaleString('fa-IR')} · {supplierName(r.supplierId)} · {r.lines.length.toLocaleString('fa-IR')} ردیف ·
              بها {rial(r.cost)} · توافقی {rial(r.agreedAmount)} · اختلاف {rial(r.variance)} ریال
            </li>
          ))}
          {returns.length === 0 && <li>هنوز برگشتی ثبت نشده است.</li>}
        </ul>
      )}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
