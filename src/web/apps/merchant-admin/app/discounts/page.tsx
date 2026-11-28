'use client';
import { useEffect, useState } from 'react';
import { codeOf, csrf, deleteWithCsrf, MESSAGES, postJson } from '@/lib/client';

type Rule = { id: string; name: string; target: { kind: string; id: string }; percent: number; validFrom: string; validTo: string | null; branchIds: string[] };
type Product = { id: string; name: string; variants: { sku: string }[] };

const pct = (bp: number) => (bp / 100).toLocaleString('fa-IR');
const latin = (v: string) => v.replace(/[۰-۹]/g, (d) => String('۰۱۲۳۴۵۶۷۸۹'.indexOf(d))).replace(/[٫]/g, '.').trim();

/**
 * US-053 / CAT-010 / BR-021: automatic discount rules (when several apply only the largest counts, D-18) and the
 * manual discount each role may give at the register without a manager's approval.
 */
export default function DiscountsPage() {
  const [rules, setRules] = useState<Rule[]>([]);
  const [names, setNames] = useState<Record<string, string>>({});
  const [form, setForm] = useState({ sku: '', name: '', percent: '', days: '' });
  const [caps, setCaps] = useState<Record<string, number> | null>(null);
  const [error, setError] = useState('');
  const [saved, setSaved] = useState('');

  async function load() {
    const r = await fetch('/bff/discount-rules', { cache: 'no-store' });
    if (!r.ok) return setError(await codeOf(r));
    setRules((await r.json()).items);
    const c = await fetch('/bff/discount-caps', { cache: 'no-store' });
    if (c.ok) setCaps((await c.json()).caps);
  }
  useEffect(() => { void load(); }, []);

  async function create(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setSaved('');
    const found = await fetch(`/bff/products?q=${encodeURIComponent(form.sku.trim())}`, { cache: 'no-store' });
    const product: Product | undefined = found.ok ? (await found.json()).items.find((p: Product) => p.variants.some((v) => v.sku === form.sku.trim())) : undefined;
    if (!product) return setError('unknown-sku');
    const days = Number(latin(form.days));
    const r = await postJson('/bff/discount-rules', {
      name: form.name || `تخفیف ${product.name}`, target: { kind: 'Product', id: product.id },
      percent: Math.round(Number(latin(form.percent)) * 100), validFrom: new Date().toISOString(),
      validTo: days > 0 ? new Date(Date.now() + days * 86_400_000).toISOString() : undefined,
    });
    if (!r.ok) return setError(await codeOf(r));
    setNames({ ...names, [product.id]: product.name });
    setForm({ sku: '', name: '', percent: '', days: '' });
    setSaved('rule');
    void load();
  }

  async function end(id: string) {
    setError('');
    const r = await deleteWithCsrf(`/bff/discount-rules/${id}`);
    if (!r.ok) return setError(await codeOf(r));
    void load();
  }

  async function saveCaps(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setSaved('');
    const r = await fetch('/bff/discount-caps', {
      method: 'PUT', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() },
      body: JSON.stringify({ caps }),
    });
    if (!r.ok) return setError(await codeOf(r));
    setSaved('caps');
  }

  const set = (k: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [k]: e.target.value });
  return (
    <section>
      <h1>تخفیف‌ها</h1>
      <h2>قواعد خودکار</h2>
      <p>اگر چند قاعده هم‌زمان روی یک کالا باشد، فقط بزرگ‌ترین اعمال می‌شود (جمع نمی‌شوند).</p>
      <ul data-testid="discount-rules">
        {rules.map((r) => (
          <li key={r.id} data-rule={r.name}>
            {r.name}: {pct(r.percent)}٪ {names[r.target.id] ? `روی ${names[r.target.id]}` : ''}
            {r.validTo ? ` تا ${new Date(r.validTo).toLocaleDateString('fa-IR')}` : ''}
            {r.branchIds.length > 0 ? ' (فقط برخی شعب)' : ''}{' '}
            <button type="button" style={{ minHeight: 44 }} onClick={() => void end(r.id)}>پایان</button>
          </li>
        ))}
        {rules.length === 0 && <li>قاعده فعالی نیست.</li>}
      </ul>
      <form onSubmit={(e) => void create(e)} data-testid="discount-form">
        <label>SKU کالا <input required value={form.sku} onChange={set('sku')} dir="ltr" size={12} data-testid="discount-sku" /></label>{' '}
        <label>نام <input value={form.name} onChange={set('name')} data-testid="discount-name" /></label>{' '}
        <label>درصد <input required value={form.percent} onChange={set('percent')} dir="ltr" inputMode="decimal" size={5} data-testid="discount-percent" /></label>{' '}
        <label>چند روز (خالی: تا پایان دستی) <input value={form.days} onChange={set('days')} dir="ltr" inputMode="numeric" size={4} /></label>{' '}
        <button type="submit" style={{ minHeight: 44 }} data-testid="discount-submit">ثبت قاعده</button>
      </form>

      <h2>سقف تخفیف دستی بدون تأیید مدیر</h2>
      {caps && (
        <form onSubmit={(e) => void saveCaps(e)} data-testid="caps-form">
          {(['Cashier', 'BranchManager'] as const).map((role) => (
            <label key={role}>{role === 'Cashier' ? 'صندوق‌دار' : 'مدیر شعبه'}{' '}
              <input value={String(caps[role] / 100)} dir="ltr" size={5} inputMode="decimal" data-testid={`cap-${role}`}
                onChange={(e) => setCaps({ ...caps, [role]: Math.round(Number(latin(e.target.value) || '0') * 100) })} />٪{' '}
            </label>
          ))}
          <button type="submit" style={{ minHeight: 44 }} data-testid="caps-submit">ذخیره سقف‌ها</button>
        </form>
      )}
      {saved && <p role="status" data-testid="discount-saved">ذخیره شد.</p>}
      {error && <p role="alert" data-testid="discount-error">{error === 'unknown-sku' ? 'کالایی با این SKU پیدا نشد.' : MESSAGES[error] ?? error}</p>}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
