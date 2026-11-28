'use client';
import { useEffect, useState } from 'react';
import { codeOf, csrf, MESSAGES, postJson } from '@/lib/client';

type Profile = { id?: string; prefix: string; itemCodeLength: number; valueType: 'Weight' | 'Price'; valueLength: number; decimals: number;
  hasInnerCheckDigit: boolean };
type Resolved = { kind: string; normalized: { isWeighted: boolean; weighted?: { value: string; valueType: string } };
  tenantProduct?: { name: string; sku: string } };

const blank: Profile = { prefix: '21', itemCodeLength: 5, valueType: 'Weight', valueLength: 5, decimals: 3, hasInnerCheckDigit: false };
const digits = (p: Profile) => p.prefix.length + p.itemCodeLength + p.valueLength + (p.hasInnerCheckDigit ? 1 : 0);

/** US-045 / CAT-007: the store's scale label formats, and a box to try a printed label. */
export default function WeightedBarcodesPage() {
  const [version, setVersion] = useState(0);
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [draft, setDraft] = useState<Profile>(blank);
  const [branchId, setBranchId] = useState('');
  const [label, setLabel] = useState('');
  const [result, setResult] = useState<Resolved | null>(null);
  const [error, setError] = useState('');
  const [saved, setSaved] = useState(false);

  async function load() {
    const r = await fetch('/bff/weighted-barcode-profiles', { cache: 'no-store' });
    if (!r.ok) return setError(await codeOf(r));
    const set = await r.json();
    setVersion(set.version);
    setProfiles(set.profiles);
  }
  useEffect(() => {
    void load();
    void fetch('/bff/branches', { cache: 'no-store' }).then(async (r) => { if (r.ok) setBranchId((await r.json())[0]?.id ?? ''); });
  }, []);

  async function save(next: Profile[]) {
    setError('');
    setSaved(false);
    const r = await fetch('/bff/weighted-barcode-profiles', {
      method: 'PUT', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() },
      body: JSON.stringify({ expectedVersion: version, profiles: next }),
    });
    if (!r.ok) return setError(await codeOf(r));
    const set = await r.json();
    setVersion(set.version);
    setProfiles(set.profiles);
    setSaved(true);
  }

  async function tryLabel(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setResult(null);
    const r = await postJson('/bff/barcodes/resolve', { barcode: label.trim(), branchId });
    if (!r.ok) return setError(await codeOf(r));
    setResult(await r.json());
  }

  const num = (k: keyof Profile) => (e: React.ChangeEvent<HTMLInputElement>) => setDraft({ ...draft, [k]: Number(e.target.value) });

  return (
    <section>
      <h1>بارکد وزنی (ترازو)</h1>
      <p>برچسب ترازو یک EAN-13 است: پیشوند، کد کالا (همان «کد داخلی» کالا)، رقم کنترل قیمت (اختیاری)، وزن یا مبلغ و رقم کنترل.</p>
      <ul data-testid="weighted-profiles">
        {profiles.map((p, i) => (
          <li key={p.id ?? i} data-prefix={p.prefix}>
            پیشوند <b dir="ltr">{p.prefix}</b> · کد کالا {p.itemCodeLength} رقم · {p.valueType === 'Weight' ? `وزن ${p.valueLength} رقم (${p.decimals} رقم اعشار، کیلوگرم)` : `مبلغ ${p.valueLength} رقم (ریال)`}
            {p.hasInnerCheckDigit ? ' · با رقم کنترل قیمت' : ''}{' '}
            <button type="button" onClick={() => void save(profiles.filter((_, j) => j !== i))} style={{ minHeight: 44 }}>حذف</button>
          </li>
        ))}
        {profiles.length === 0 && <li>هنوز قالبی تعریف نشده است.</li>}
      </ul>

      <h2>افزودن قالب</h2>
      <form onSubmit={(e) => { e.preventDefault(); void save([...profiles, draft]); }} data-testid="weighted-form">
        <label>پیشوند <input value={draft.prefix} onChange={(e) => setDraft({ ...draft, prefix: e.target.value })} dir="ltr" size={3} data-testid="weighted-prefix" /></label>{' '}
        <label>طول کد کالا <input type="number" min={1} max={6} value={draft.itemCodeLength} onChange={num('itemCodeLength')} /></label>{' '}
        <label>نوع <select value={draft.valueType} data-testid="weighted-type"
          onChange={(e) => setDraft({ ...draft, valueType: e.target.value as Profile['valueType'], decimals: e.target.value === 'Price' ? 0 : 3 })}>
          <option value="Weight">وزن</option><option value="Price">مبلغ</option></select></label>{' '}
        <label>طول مقدار <input type="number" min={3} max={6} value={draft.valueLength} onChange={num('valueLength')} data-testid="weighted-length" /></label>{' '}
        {draft.valueType === 'Weight' && <label>رقم اعشار <input type="number" min={0} max={3} value={draft.decimals} onChange={num('decimals')} /></label>}{' '}
        <label><input type="checkbox" checked={draft.hasInnerCheckDigit} data-testid="weighted-inner"
          onChange={(e) => setDraft({ ...draft, hasInnerCheckDigit: e.target.checked })} /> رقم کنترل قیمت</label>{' '}
        <span data-testid="weighted-digits">{digits(draft)} از ۱۲ رقم</span>{' '}
        <button type="submit" style={{ minHeight: 44 }} data-testid="weighted-submit">ذخیره</button>
      </form>
      {saved && <p role="status" data-testid="weighted-saved">ذخیره شد.</p>}

      <h2>آزمایش برچسب</h2>
      <form onSubmit={(e) => void tryLabel(e)}>
        <input aria-label="بارکد برچسب" value={label} onChange={(e) => setLabel(e.target.value)} dir="ltr" inputMode="numeric"
          data-testid="weighted-label" style={{ minHeight: 44 }} />{' '}
        <button type="submit" style={{ minHeight: 44 }} data-testid="weighted-try">بررسی</button>
      </form>
      {result && (
        <p role="status" data-testid="weighted-result">
          {result.tenantProduct ? `${result.tenantProduct.name} (${result.tenantProduct.sku})` : 'کالایی با این کد پیدا نشد'}
          {result.normalized.weighted && (result.normalized.weighted.valueType === 'Weight'
            ? ` — ${result.normalized.weighted.value} کیلوگرم` : ` — ${Number(result.normalized.weighted.value).toLocaleString('fa-IR')} ریال`)}
        </p>
      )}
      {error && <p role="alert" data-testid="weighted-error">{MESSAGES[error] ?? error}</p>}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
