'use client';
import { useEffect, useState } from 'react';
import { codeOf, csrf, MESSAGES } from '@/lib/client';

type Template = {
  industryTemplateVersion: string; industryName: string; lockedFields: string[]; legalText: string;
  overrides: { footerText: string | null; signature: string | null }; version: number;
};

const FIELDS: Record<string, string> = {
  legalName: 'نام فروشنده', economicCode: 'کد اقتصادی', nationalId: 'شناسه ملی', businessLicenseNo: 'شماره پروانه کسب',
  branchAddress: 'نشانی شعبه', invoiceNo: 'شماره فاکتور', issuedAt: 'تاریخ و ساعت', cashier: 'صندوق‌دار', lines: 'اقلام',
  totals: 'جمع‌ها', tax: 'مالیات بر ارزش افزوده', payments: 'پرداخت‌ها', qrCode: 'QR فاکتور دیجیتال',
};

/**
 * US-172 / BR-086 / DI-085: the receipt template. The legal text and the fields every receipt must show come from the
 * industry's template (set by the platform, versioned); the store sets only its footer text and signature.
 */
export default function ReceiptTemplatePage() {
  const [template, setTemplate] = useState<Template | null>(null);
  const [form, setForm] = useState({ footerText: '', signature: '' });
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState('');

  async function load() {
    const r = await fetch('/bff/templates/receipt', { cache: 'no-store' });
    if (!r.ok) return setError(await codeOf(r));
    const t: Template = await r.json();
    setTemplate(t);
    setForm({ footerText: t.overrides.footerText ?? '', signature: t.overrides.signature ?? '' });
  }
  useEffect(() => { void load(); }, []);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setSaved(false);
    const r = await fetch('/bff/templates/receipt', {
      method: 'PUT', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() },
      body: JSON.stringify({ expectedVersion: template?.version ?? 0, overrides: { footerText: form.footerText, signature: form.signature } }),
    });
    if (!r.ok) return setError(await codeOf(r));
    const t: Template = await r.json();
    setTemplate(t);
    setSaved(true);
  }

  return (
    <section>
      <h1>قالب فاکتور</h1>
      {template && (
        <>
          <p data-testid="template-version" data-version={template.industryTemplateVersion}>
            قالب صنف «{template.industryName}» — نسخه {template.industryTemplateVersion.split('@')[1]}
          </p>
          <h2>بخش‌های ثابت (از قالب صنف؛ قابل تغییر نیستند)</h2>
          <p data-testid="template-legal" style={{ border: '1px solid #999', padding: 8 }}>{template.legalText}</p>
          <p>فیلدهایی که روی هر فاکتور می‌آیند: <span data-testid="template-locked">{template.lockedFields.map((f) => FIELDS[f] ?? f).join('، ')}</span></p>
          <h2>بخش‌های فروشگاه</h2>
          <form onSubmit={(e) => void save(e)} data-testid="template-form">
            <p><label>متن پایین فاکتور{' '}
              <textarea value={form.footerText} onChange={(e) => setForm({ ...form, footerText: e.target.value })} maxLength={300} rows={3} cols={40}
                data-testid="template-footer" /></label></p>
            <p><label>امضا یا نام روی فاکتور{' '}
              <input value={form.signature} onChange={(e) => setForm({ ...form, signature: e.target.value })} maxLength={100} size={30}
                data-testid="template-signature" /></label></p>
            <button type="submit" style={{ minHeight: 44 }} data-testid="template-save">ذخیره</button>
          </form>
          <h2>پیش‌نمایش پایین فاکتور</h2>
          <div data-testid="template-preview" style={{ border: '1px dashed #999', padding: 8, maxWidth: 320 }}>
            <p>{template.legalText}</p>
            {template.overrides.footerText && <p>{template.overrides.footerText}</p>}
            {template.overrides.signature && <p>— {template.overrides.signature}</p>}
          </div>
          <p>لوگو با چاپ فاکتور اضافه می‌شود.</p>
        </>
      )}
      {saved && <p role="status" data-testid="template-saved">ذخیره شد.</p>}
      {error && <p role="alert" data-testid="template-error">{MESSAGES[error] ?? error}</p>}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
