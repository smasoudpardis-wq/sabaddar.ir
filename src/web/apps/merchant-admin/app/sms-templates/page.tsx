'use client';
import { useEffect, useState } from 'react';
import { codeOf, putJson } from '@/lib/client';

type Tpl = { code: string; version: number; platformText: string; storeName: string | null; signature: string | null; preview: string };

const NAMES: Record<string, string> = { receipt: 'پیامک فاکتور', 'due-reminder': 'یادآوری سررسید' };
const ERRORS: Record<string, string> = {
  validation: 'نام (تا ۳۰ حرف) یا امضا (تا ۴۰ حرف) را در یک خط و بدون لینک بنویسید.',
  'version-conflict': 'این متن هم‌زمان تغییر کرد؛ صفحه دوباره بارگذاری شد.',
  forbidden: 'نقش شما اجازه‌ی تغییر تنظیمات فروشگاه را نمی‌دهد.',
};

/** US-152 / NOT-009: the platform owns the SMS text; the store sets only the name it signs with and a closing signature. */
export default function SmsTemplatesPage() {
  const [items, setItems] = useState<Tpl[] | null>(null);
  const [draft, setDraft] = useState<Record<string, { name: string; sig: string }>>({});
  const [error, setError] = useState('');
  const [saved, setSaved] = useState('');

  async function load() {
    const r = await fetch('/bff/notification-templates', { cache: 'no-store' });
    if (!r.ok) { setError(await codeOf(r)); setItems([]); return; }
    const list: Tpl[] = (await r.json()).items;
    setItems(list);
    setDraft(Object.fromEntries(list.map((t) => [t.code, { name: t.storeName ?? '', sig: t.signature ?? '' }])));
  }
  useEffect(() => { void load(); }, []);

  async function save(t: Tpl) {
    setError(''); setSaved('');
    const d = draft[t.code];
    const r = await putJson(`/bff/notification-templates/${t.code}`, {
      expectedVersion: t.version, storeName: d.name.trim() || undefined, signature: d.sig.trim() || undefined,
    });
    if (!r.ok) { setError(await codeOf(r)); if (r.status === 409) await load(); return; }
    setSaved(t.code);
    await load();
  }

  if (!items) return <p>در حال بارگذاری…</p>;
  return (
    <section>
      <h1>متن پیامک‌ها</h1>
      <p>متن پیامک‌ها را پلتفرم تعیین می‌کند. شما فقط نام فروشگاه در ابتدای پیامک و یک امضای پایانی را می‌توانید تغییر دهید.</p>
      {error && <p role="alert" data-testid="sms-error">{ERRORS[error] ?? error}</p>}
      {items.map((t) => (
        <form key={t.code} data-testid={`tpl-${t.code}`} onSubmit={(e) => { e.preventDefault(); void save(t); }}>
          <h2>{NAMES[t.code] ?? t.code} <small>(نسخه {t.version.toLocaleString('fa-IR')})</small></h2>
          <p>متن پلتفرم: <span dir="rtl">{t.platformText}</span></p>
          <label>نام فروشگاه در پیامک <input name={`name-${t.code}`} maxLength={30} value={draft[t.code]?.name ?? ''}
            onChange={(e) => setDraft((d) => ({ ...d, [t.code]: { ...d[t.code], name: e.target.value } }))} /></label>
          <label>امضای پایانی <input name={`sig-${t.code}`} maxLength={40} value={draft[t.code]?.sig ?? ''}
            onChange={(e) => setDraft((d) => ({ ...d, [t.code]: { ...d[t.code], sig: e.target.value } }))} /></label>
          <button type="submit" data-testid={`tpl-save-${t.code}`} style={{ minHeight: 44 }}>ثبت</button>
          {saved === t.code && <span role="status" data-testid={`tpl-saved-${t.code}`}> ذخیره شد</span>}
          <p data-testid={`tpl-preview-${t.code}`}>پیش‌نمایش: {t.preview}</p>
        </form>
      ))}
    </section>
  );
}
