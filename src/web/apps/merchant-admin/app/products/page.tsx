'use client';
import { useEffect, useState } from 'react';
import { codeOf, csrf, MESSAGES } from '@/lib/client';

type Barcode = { id: string; code: string; codeNormalized: string; packagingUnit: string; unitFactor: string; active: boolean };
type Candidate = { source: string; confidence: string; license: string | null; attribution: string | null; catalogProductId: string | null;
  fields: { name: string | null; brand: string | null; category: string | null; netContent: string | null; imageUrl: string | null };
  conflictsWith: number[]; preselect: boolean };
const SOURCES: Record<string, string> = { global: 'کاتالوگ عمومی (بررسی‌شده)', open_food_facts: 'Open Food Facts' };
type Product = { id: string; name: string; brand: string | null; baseUnit: string; taxRate: number; status: string; version: number;
  axes: string[];
  variants: { id: string; sku: string; barcodes: Barcode[]; unitPrice: string | null; status: string; axisValues: Record<string, string> }[] };

const split = (v: string) => v.split(/[,،]/).map((x) => x.trim()).filter(Boolean);
const toLatin = (v: string) => v.replace(/[۰-۹]/g, (d) => String('۰۱۲۳۴۵۶۷۸۹'.indexOf(d))).replace(/[,٬\s]/g, '');

/**
 * US-051 / CAT-008: colours × sizes, with the combinations the store does not sell left out. Each variant gets its own
 * SKU (base-colour#-size#), an optional common first price, and later its own price, barcode and stock.
 */
function MatrixForm({ onCreated }: { onCreated: (name: string) => void }) {
  const [m, setM] = useState({ name: '', sku: '', colors: '', sizes: '', price: '' });
  const [off, setOff] = useState<Set<string>>(new Set());
  const [key, setKey] = useState(() => crypto.randomUUID());
  const [error, setError] = useState('');
  const colors = split(m.colors), sizes = split(m.sizes);
  const cell = (ci: number, si: number) => `${ci}-${si}`;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    const variants = colors.flatMap((c, ci) => sizes.map((z, si) => ({ c, z, ci, si })))
      .filter((x) => !off.has(cell(x.ci, x.si)))
      .map((x) => ({ sku: `${m.sku}-${x.ci + 1}-${x.si + 1}`, axisValues: { color: x.c, size: x.z }, unitPrice: m.price ? toLatin(m.price) : undefined }));
    const r = await fetch('/bff/products', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-csrf-token': csrf(), 'idempotency-key': key },
      body: JSON.stringify({ name: m.name, baseUnit: 'EA', axes: ['color', 'size'], confirmation: { confirmed: true }, variants }),
    });
    if (!r.ok) return setError(await codeOf(r));
    setKey(crypto.randomUUID());
    setOff(new Set());
    onCreated(m.name);
    setM({ name: '', sku: '', colors: '', sizes: '', price: '' });
  }

  const set = (k: keyof typeof m) => (e: React.ChangeEvent<HTMLInputElement>) => setM({ ...m, [k]: e.target.value });
  return (
    <form onSubmit={(e) => void submit(e)} data-testid="matrix-form">
      <label>نام <input required value={m.name} onChange={set('name')} data-testid="matrix-name" /></label>{' '}
      <label>SKU پایه <input required value={m.sku} onChange={set('sku')} dir="ltr" size={10} data-testid="matrix-sku" /></label>{' '}
      <label>رنگ‌ها (با ، جدا کنید) <input value={m.colors} onChange={set('colors')} data-testid="matrix-colors" /></label>{' '}
      <label>سایزها <input value={m.sizes} onChange={set('sizes')} dir="ltr" data-testid="matrix-sizes" /></label>{' '}
      <label>قیمت همه (ریال، اختیاری) <input value={m.price} onChange={set('price')} dir="ltr" inputMode="numeric" size={10} /></label>
      {colors.length > 0 && sizes.length > 0 && (
        <table data-testid="matrix-grid">
          <thead><tr><th />{sizes.map((z) => <th key={z}>{z}</th>)}</tr></thead>
          <tbody>
            {colors.map((c, ci) => (
              <tr key={c}><th>{c}</th>{sizes.map((z, si) => (
                <td key={z}>
                  <input type="checkbox" aria-label={`${c} ${z}`} checked={!off.has(cell(ci, si))} onChange={(e) => {
                    const next = new Set(off);
                    if (e.target.checked) next.delete(cell(ci, si)); else next.add(cell(ci, si));
                    setOff(next);
                  }} />
                </td>
              ))}</tr>
            ))}
          </tbody>
        </table>
      )}
      <button type="submit" style={{ minHeight: 44 }} data-testid="matrix-submit">
        ساخت {colors.length * sizes.length - off.size > 0 ? colors.length * sizes.length - off.size : ''} ترکیب (اطلاعات را تأیید می‌کنم)
      </button>
      {error && <p role="alert" data-testid="matrix-error">{MESSAGES[error] ?? error}</p>}
    </form>
  );
}

const rial = (v: string) => Number(v).toLocaleString('fa-IR');

/**
 * US-052 / CAT-009 / BR-041: a new price for one variant, from now. A big change needs another manager's approval:
 * the request waits on the approvals page and, once approved, the same change is sent again with the approval.
 */
/**
 * US-044 / CAT-006 / CAT-005.1: a duplicate is merged into the product it duplicates (found by its SKU), and a product can be
 * suggested to the global catalog — only after the store says so, and only its name, brand, category and public barcodes.
 */
function ProductActions({ product, onDone }: { product: Product; onDone: () => void }) {
  const [mode, setMode] = useState<'none' | 'merge' | 'suggest'>('none');
  const [targetSku, setTargetSku] = useState('');
  const [reason, setReason] = useState('');
  const [consent, setConsent] = useState(false);
  const [note, setNote] = useState('');
  const [key, setKey] = useState(() => crypto.randomUUID());

  async function merge(e: React.FormEvent) {
    e.preventDefault();
    setNote('');
    const found = await fetch(`/bff/products?q=${encodeURIComponent(targetSku.trim())}`, { cache: 'no-store' });
    const target = found.ok ? ((await found.json()).items as Product[]).find((p) => p.variants.some((v) => v.sku === targetSku.trim())) : undefined;
    if (!target || target.id === product.id) return setNote('کالای مقصد با این SKU پیدا نشد.');
    const r = await fetch(`/bff/products/${product.id}/merge`, {
      method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf(), 'idempotency-key': key },
      body: JSON.stringify({ targetProductId: target.id, expectedVersion: product.version, reason }),
    });
    if (!r.ok) {
      const code = await codeOf(r);
      return setNote(MERGE_ERRORS[code] ?? MESSAGES[code] ?? code);
    }
    setKey(crypto.randomUUID());
    setMode('none');
    onDone();
  }

  async function suggest(e: React.FormEvent) {
    e.preventDefault();
    setNote('');
    const r = await fetch(`/bff/products/${product.id}/catalog-suggestions`, {
      method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() }, body: JSON.stringify({ consent }),
    });
    if (!r.ok) {
      const code = await codeOf(r);
      return setNote(code === 'no-public-barcode' ? 'فقط کالایی با بارکد عمومی (نه کد داخلی یا وزنی) پیشنهاد می‌شود.' : MESSAGES[code] ?? code);
    }
    const p = await r.json();
    setNote(p.submitted.length > 0 ? 'برای بررسی ناظر کاتالوگ فرستاده شد.' : 'این کالا قبلاً پیشنهاد شده است.');
    setMode('none');
  }

  return (
    <span data-testid={`product-actions-${product.id}`}>
      {' '}<button type="button" style={{ minHeight: 44 }} onClick={() => setMode(mode === 'merge' ? 'none' : 'merge')} data-testid="product-merge-open">ادغام در کالای دیگر</button>
      {' '}<button type="button" style={{ minHeight: 44 }} onClick={() => setMode(mode === 'suggest' ? 'none' : 'suggest')} data-testid="product-suggest-open">پیشنهاد به کاتالوگ عمومی</button>
      {mode === 'merge' && (
        <form onSubmit={(e) => void merge(e)} data-testid="product-merge-form">
          این کالا تکراری است و در کالای با SKU{' '}
          <input required value={targetSku} onChange={(e) => setTargetSku(e.target.value)} dir="ltr" size={12} data-testid="product-merge-target" />{' '}
          ادغام شود؛ دلیل <input required minLength={3} value={reason} onChange={(e) => setReason(e.target.value)} data-testid="product-merge-reason" />{' '}
          <button type="submit" style={{ minHeight: 44 }} data-testid="product-merge-submit">ادغام</button>
          <br />بارکدها به کالای مقصد منتقل می‌شوند و این کالا حذف نمی‌شود (در سابقه می‌ماند). موجودی آن باید صفر باشد.
        </form>
      )}
      {mode === 'suggest' && (
        <form onSubmit={(e) => void suggest(e)} data-testid="product-suggest-form">
          <label><input type="checkbox" required checked={consent} onChange={(e) => setConsent(e.target.checked)} data-testid="product-suggest-consent" />{' '}
            موافقم نام، برند و بارکد عمومی این کالا (بدون قیمت، موجودی و نام فروشگاه) برای کاتالوگ عمومی فرستاده شود</label>{' '}
          <button type="submit" style={{ minHeight: 44 }} data-testid="product-suggest-submit">فرستادن</button>
        </form>
      )}
      {note && <span role="status" data-testid="product-action-note"> {note}</span>}
    </span>
  );
}

const MERGE_ERRORS: Record<string, string> = {
  'base-unit-mismatch': 'واحد پایه دو کالا یکی نیست؛ ادغام ممکن نیست.',
  'merge-source-has-stock': 'این کالا هنوز موجودی دارد؛ اول موجودی آن را صفر کنید (انتقال یا تعدیل).',
  'merge-variant-mismatch': 'حالت‌های (رنگ/سایز) دو کالا با هم جور نیستند.',
  'product-merged': 'یکی از دو کالا قبلاً ادغام شده است.',
};

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
  // US-043 / CAT-004: suggestions for an unknown public barcode; using one fills the form, saving it is the confirmation
  const [candidates, setCandidates] = useState<Candidate[] | null>(null);
  const [degraded, setDegraded] = useState(false);
  const [picked, setPicked] = useState<{ index: number; catalogProductId: string | null } | null>(null);
  const [checked, setChecked] = useState(false);
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

  async function toggle(p: Product, variantId: string, status: string) {
    setError('');
    const r = await fetch(`/bff/products/${p.id}/variants/${variantId}`, {
      method: 'PATCH', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() },
      body: JSON.stringify({ expectedVersion: p.version, status }),
    });
    if (!r.ok) return setError(await codeOf(r));
    void find(search);
  }

  async function suggest() {
    setError('');
    setCandidates(null);
    setPicked(null);
    // the resolve is asked in a branch the user may define products in (CAT-003): the first one they see
    const b = await fetch('/bff/branches', { cache: 'no-store' });
    const branchId: string | undefined = b.ok ? ((await b.json()) as { id: string }[])[0]?.id : undefined;
    if (!branchId) return setError('forbidden');
    const r = await fetch('/bff/barcodes/resolve', {
      method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() },
      body: JSON.stringify({ barcode: form.barcode.trim(), branchId }),
    });
    if (!r.ok) return setError(await codeOf(r));
    const p = await r.json();
    if (p.kind === 'known') return setError('barcode-already-assigned');
    setDegraded(p.degraded);
    setCandidates(p.candidates);
    const pre = (p.candidates as Candidate[]).findIndex((c) => c.preselect);
    if (pre >= 0) use(p.candidates[pre], pre);
  }

  function use(c: Candidate, index: number) {
    setPicked({ index, catalogProductId: c.catalogProductId });
    setChecked(false);
    setForm((f) => ({ ...f, name: c.fields.name ?? f.name, brand: c.fields.brand ?? f.brand }));
  }

  async function create(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setCreated('');
    const r = await fetch('/bff/products', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-csrf-token': csrf(), 'idempotency-key': key },
      body: JSON.stringify({
        name: form.name, brand: form.brand || undefined, baseUnit: 'EA', taxRate: Math.round(Number(form.taxRate) * 100),
        // CAT-004.1: data taken from a suggestion is saved only once the user ticked that they checked it
        confirmation: picked ? { confirmed: checked, sourceCandidate: picked.index } : { confirmed: true },
        catalogProductId: picked?.catalogProductId ?? undefined,
        variants: [{ sku: form.sku, barcodes: form.barcode ? [{ code: form.barcode, packagingUnit: form.packagingUnit, unitFactor: form.unitFactor, isInternal: internal || undefined }] : [] }],
      }),
    });
    if (!r.ok) return setError(await codeOf(r));
    const p: Product = await r.json();
    setCreated(p.name);
    setKey(crypto.randomUUID());   // a new product is a new attempt
    setForm({ ...form, name: '', brand: '', sku: '', barcode: '' });
    setCandidates(null);
    setPicked(null);
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
            <ProductActions product={p} onDone={() => void find(search)} />
            {p.variants.map((v) => (
              <div key={v.id} data-variant={v.sku} data-status={v.status}>
                {v.sku}{Object.keys(v.axisValues ?? {}).length > 0 ? ` (${Object.values(v.axisValues).join(' / ')})` : ''}:{' '}
                {v.barcodes.filter((b) => b.active).map((b) => b.code).join('، ') || 'بدون بارکد'}
                {v.status === 'Inactive' && ' — غیرفعال'}
                {' — '}<span data-testid={`price-${v.sku}`}>{v.unitPrice ? `${rial(v.unitPrice)} ریال` : 'بدون قیمت'}</span>{' '}
                {listId && v.status === 'Active' && <PriceForm listId={listId} variantId={v.id} onDone={() => void find(search)} />}
                {p.axes?.length > 0 && (
                  <button type="button" style={{ minHeight: 44 }} onClick={() => void toggle(p, v.id, v.status === 'Active' ? 'Inactive' : 'Active')}>
                    {v.status === 'Active' ? 'غیرفعال کن' : 'فعال کن'}
                  </button>
                )}
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
        <button type="button" style={{ minHeight: 44 }} disabled={!form.barcode.trim() || internal} onClick={() => void suggest()} data-testid="product-suggest">
          پیشنهاد مشخصات</button>{' '}
        <label><input type="checkbox" checked={internal} onChange={(e) => setInternal(e.target.checked)} data-testid="product-internal" /> کد داخلی (مثلاً کد کالا در ترازو)</label>{' '}
        {candidates && (
          <div data-testid="product-candidates">
            {degraded && <p role="status">منبع بیرونی پاسخ نداد؛ فقط پیشنهادهای موجود نمایش داده می‌شوند.</p>}
            {candidates.length === 0 && <p>پیشنهادی پیدا نشد؛ مشخصات را خودتان وارد کنید.</p>}
            <ul>
              {candidates.map((c, i) => (
                <li key={i} data-source={c.source} data-confidence={c.confidence}>
                  {c.fields.name}{c.fields.brand ? ` — ${c.fields.brand}` : ''}{c.fields.netContent ? ` (${c.fields.netContent})` : ''} ·{' '}
                  {SOURCES[c.source] ?? c.source} · اطمینان {Math.round(Number(c.confidence) * 100).toLocaleString('fa-IR')}٪
                  {c.attribution ? ` · ${c.attribution} (${c.license})` : ''}
                  {c.conflictsWith.length > 0 && ' · با پیشنهاد دیگری متفاوت است'}{' '}
                  <button type="button" style={{ minHeight: 44 }} onClick={() => use(c, i)} data-testid={`product-use-${i}`}>
                    {picked?.index === i ? 'انتخاب‌شده' : 'استفاده'}</button>
                </li>
              ))}
            </ul>
          </div>
        )}
        {picked && (
          <label><input type="checkbox" checked={checked} onChange={(e) => setChecked(e.target.checked)} data-testid="product-confirm-suggestion" />{' '}
            مشخصات پیشنهادی را بررسی کردم و درست است</label>
        )}{' '}
        <label>بسته‌بندی <select value={form.packagingUnit} onChange={set('packagingUnit')}>{UNITS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select></label>{' '}
        <label>تعداد در بسته <input value={form.unitFactor} onChange={set('unitFactor')} dir="ltr" size={4} /></label>{' '}
        <label>نرخ مالیات ٪ <input value={form.taxRate} onChange={set('taxRate')} dir="ltr" size={4} /></label>{' '}
        <button type="submit" style={{ minHeight: 44 }} data-testid="product-submit">ثبت کالا (اطلاعات را تأیید می‌کنم)</button>
      </form>
      <h2>کالای چندحالته (رنگ × سایز)</h2>
      <MatrixForm onCreated={(name) => { setCreated(name); setSearch(name); void find(name); }} />
      {created && <p role="status" data-testid="product-created">«{created}» ثبت شد.</p>}
      {error && <p role="alert" data-testid="product-error">{error === 'confirmation-required'
        ? 'مشخصات پیشنهادی را بررسی و گزینه‌ی «بررسی کردم» را علامت بزنید.' : MESSAGES[error] ?? error}</p>}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
