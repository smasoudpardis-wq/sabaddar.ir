'use client';
import { useEffect, useState } from 'react';
import { codeOf, csrf, MESSAGES } from '@/lib/client';

type Barcode = { id: string; code: string; codeNormalized: string; packagingUnit: string; unitFactor: string; active: boolean };
type Product = { id: string; name: string; brand: string | null; baseUnit: string; taxRate: number; status: string;
  variants: { id: string; sku: string; barcodes: Barcode[]; unitPrice: string | null }[] };

const rial = (v: string) => Number(v).toLocaleString('fa-IR');

/**
 * US-052 / CAT-009 / BR-041: a new price for one variant, from now. A big change needs another manager's approval:
 * the request waits on the approvals page and, once approved, the same change is sent again with the approval.
 */
function PriceForm({ listId, variantId, onDone }: { listId: string; variantId: string; onDone: () => void }) {
  const [price, setPrice] = useState('');
  const [pending, setPending] = useState<{ approvalId: string; key: string; body: string } | null>(null);
  const [error, setError] = useState('');

  async function send(body: string, key: string, approvalId?: string) {
    setError('');
    const headers: Record<string, string> = { 'content-type': 'application/json', 'x-csrf-token': csrf(), 'idempotency-key': key };
    if (approvalId) headers['x-approval-id'] = approvalId;
    const r = await fetch(`/bff/price-lists/${listId}/prices`, { method: 'POST', headers, body });
    if (r.status === 204) { setPending(null); setPrice(''); onDone(); return; }
    const p = await r.json().catch(() => null) as { code?: string; approvalRequestId?: string } | null;
    if (p?.code === 'approval-required' && p.approvalRequestId) {
      // the same change (body and key) is retried after approval; the approval fits only this change
      setPending({ approvalId: p.approvalRequestId, key, body });
      if (approvalId) setError('approval-not-yet');
      return;
    }
    setError(p?.code ?? `http-${r.status}`);
  }

  return (
    <span>
      <form style={{ display: 'inline' }} onSubmit={(e) => {
        e.preventDefault();
        const digits = price.replace(/[۰-۹]/g, (d) => String('۰۱۲۳۴۵۶۷۸۹'.indexOf(d))).replace(/[,٬\s]/g, '');
        void send(JSON.stringify({ items: [{ variantId, unitPrice: digits }], validFrom: new Date().toISOString() }), crypto.randomUUID());
      }}>
        <input aria-label="قیمت جدید (ریال)" value={price} onChange={(e) => setPrice(e.target.value)} dir="ltr" inputMode="numeric" size={10}
          data-testid={`price-input-${variantId}`} />{' '}
        <button type="submit" style={{ minHeight: 44 }} data-testid={`price-submit-${variantId}`}>ثبت قیمت</button>
      </form>
      {pending && (
        <span role="status" data-testid={`price-pending-${variantId}`}>
          {' '}تغییر بزرگ است و تأیید مدیر دیگری لازم دارد (<a href="/approvals">تأییدها</a>).{' '}
          <button type="button" style={{ minHeight: 44 }} onClick={() => void send(pending.body, pending.key, pending.approvalId)}>اعمال پس از تأیید</button>
        </span>
      )}
      {error && <span role="alert" data-testid={`price-error-${variantId}`}> {MESSAGES[error] ?? error}</span>}
    </span>
  );
}

const UNITS: [string, string][] = [['EA', 'عدد'], ['PK', 'بسته'], ['CT', 'کارتن']];

/** US-041 / CAT-001..003: search by name (Persian-normalised), SKU or scanned barcode, and define a product. */
export default function ProductsPage() {
  const [search, setSearch] = useState('');
  const [items, setItems] = useState<Product[]>([]);
  const [form, setForm] = useState({ name: '', brand: '', sku: '', barcode: '', packagingUnit: 'EA', unitFactor: '1', taxRate: '10' });
  const [internal, setInternal] = useState(false);   // the store's own code, e.g. a scale item code (CAT-003, CAT-007)
  const [key, setKey] = useState(() => crypto.randomUUID());
  const [error, setError] = useState('');
  const [created, setCreated] = useState('');
  const [listId, setListId] = useState('');

  async function find(term = search) {
    setError('');
    const t = term.trim();
    const q = new URLSearchParams(/^[0-9۰-۹]{8,14}$/.test(t) ? { barcode: t } : t ? { q: t } : {});
    const r = await fetch(`/bff/products?${q}`, { cache: 'no-store' });
    if (!r.ok) return setError(await codeOf(r));
    setItems((await r.json()).items);
  }
  useEffect(() => {
    void find('');
    void fetch('/bff/price-lists', { cache: 'no-store' }).then(async (r) => {
      if (r.ok) setListId(((await r.json()).items as { id: string; isDefault: boolean }[]).find((l) => l.isDefault)?.id ?? '');
    });
  }, []);

  async function create(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setCreated('');
    const r = await fetch('/bff/products', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-csrf-token': csrf(), 'idempotency-key': key },
      body: JSON.stringify({
        name: form.name, brand: form.brand || undefined, baseUnit: 'EA', taxRate: Math.round(Number(form.taxRate) * 100),
        confirmation: { confirmed: true },
        variants: [{ sku: form.sku, barcodes: form.barcode ? [{ code: form.barcode, packagingUnit: form.packagingUnit, unitFactor: form.unitFactor, isInternal: internal || undefined }] : [] }],
      }),
    });
    if (!r.ok) return setError(await codeOf(r));
    const p: Product = await r.json();
    setCreated(p.name);
    setKey(crypto.randomUUID());   // a new product is a new attempt
    setForm({ ...form, name: '', brand: '', sku: '', barcode: '' });
    void find(p.name);
  }

  const set = (k: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => setForm({ ...form, [k]: e.target.value });

  return (
    <section>
      <h1>کالاها</h1>
      <form onSubmit={(e) => { e.preventDefault(); void find(); }}>
        <input aria-label="جستجو" data-testid="product-search" placeholder="نام، SKU یا اسکن بارکد" value={search}
          onChange={(e) => setSearch(e.target.value)} style={{ minHeight: 44 }} />
        {' '}<button type="submit" data-testid="product-search-submit" style={{ minHeight: 44 }}>جستجو</button>
      </form>
      <ul data-testid="products">
        {items.map((p) => (
          <li key={p.id} data-product={p.id}>
            {p.name}{p.brand ? ` (${p.brand})` : ''}
            {p.variants.map((v) => (
              <div key={v.id} data-variant={v.sku}>
                {v.sku}: {v.barcodes.filter((b) => b.active).map((b) => b.code).join('، ') || 'بدون بارکد'}
                {' — '}<span data-testid={`price-${v.sku}`}>{v.unitPrice ? `${rial(v.unitPrice)} ریال` : 'بدون قیمت'}</span>{' '}
                {listId && <PriceForm listId={listId} variantId={v.id} onDone={() => void find(search)} />}
              </div>
            ))}
          </li>
        ))}
      </ul>

      <h2>تعریف کالا</h2>
      <form onSubmit={(e) => void create(e)} data-testid="product-form">
        <label>نام <input required value={form.name} onChange={set('name')} data-testid="product-name" /></label>{' '}
        <label>برند <input value={form.brand} onChange={set('brand')} /></label>{' '}
        <label>SKU <input required value={form.sku} onChange={set('sku')} dir="ltr" data-testid="product-sku" /></label>{' '}
        <label>بارکد <input value={form.barcode} onChange={set('barcode')} dir="ltr" inputMode="numeric" data-testid="product-barcode" /></label>{' '}
        <label><input type="checkbox" checked={internal} onChange={(e) => setInternal(e.target.checked)} data-testid="product-internal" /> کد داخلی (مثلاً کد کالا در ترازو)</label>{' '}
        <label>بسته‌بندی <select value={form.packagingUnit} onChange={set('packagingUnit')}>{UNITS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select></label>{' '}
        <label>تعداد در بسته <input value={form.unitFactor} onChange={set('unitFactor')} dir="ltr" size={4} /></label>{' '}
        <label>نرخ مالیات ٪ <input value={form.taxRate} onChange={set('taxRate')} dir="ltr" size={4} /></label>{' '}
        <button type="submit" style={{ minHeight: 44 }} data-testid="product-submit">ثبت کالا (اطلاعات را تأیید می‌کنم)</button>
      </form>
      {created && <p role="status" data-testid="product-created">«{created}» ثبت شد.</p>}
      {error && <p role="alert" data-testid="product-error">{MESSAGES[error] ?? error}</p>}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
