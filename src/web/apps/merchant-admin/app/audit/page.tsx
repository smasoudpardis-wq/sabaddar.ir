'use client';
import { useEffect, useState } from 'react';
import { codeOf, MESSAGES } from '@/lib/client';

type Rec = {
  seq: number; occurredAt: string; actorKind: string; actorId: string | null; actorRoles: string[]; action: string;
  resourceType: string; resourceId: string | null; reason: string | null; terminalId: string | null; clientIp: string | null;
  details: Record<string, unknown>;
};
type Page = { items: Rec[]; page: { nextCursor: string | null; hasMore: boolean } };

const ACTIONS: Record<string, string> = {
  'platform.tenant.registered': 'ثبت فروشگاه', 'platform.branch.created': 'ایجاد شعبه', 'platform.register.created': 'ایجاد صندوق',
  'platform.pairing-code.issued': 'صدور کد جفت‌سازی', 'platform.terminal.paired': 'جفت شدن دستگاه', 'platform.terminal.revoked': 'لغو دستگاه',
  'iam.tenant.selected': 'ورود به فروشگاه', 'iam.invitation.created': 'دعوت همکار', 'iam.invitation.accepted': 'پذیرش دعوت',
  'iam.role.assigned': 'تخصیص نقش', 'iam.role.removed': 'حذف نقش', 'iam.pin.set': 'تعیین PIN', 'iam.pin.failed': 'PIN نادرست',
  'iam.pin.locked': 'قفل PIN', 'iam.register.unlocked': 'باز کردن صندوق', 'iam.approval.approved': 'تأیید درخواست',
  'iam.approval.rejected': 'رد درخواست',
};
const GROUPS: [string, string][] = [['', 'همه'], ['iam.', 'دسترسی و کاربران'], ['platform.', 'فروشگاه و دستگاه‌ها']];

/** US-032 / AUD-001.1: the tenant's audit trail for Owner, Admin and Auditor (audit.view), newest first. */
export default function AuditPage() {
  const [action, setAction] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [rows, setRows] = useState<Rec[] | null>(null);
  const [cursor, setCursor] = useState<string | null>(null);
  const [error, setError] = useState('');

  async function load(next?: string) {
    setError('');
    const q = new URLSearchParams({ limit: '50' });
    if (action) q.set('action', action);
    if (from) q.set('from', new Date(from).toISOString());
    if (to) q.set('to', new Date(to).toISOString());
    if (next) q.set('cursor', next);
    const r = await fetch(`/bff/audit?${q}`, { cache: 'no-store' });
    if (!r.ok) return setError(await codeOf(r));
    const p: Page = await r.json();
    setRows(next ? [...(rows ?? []), ...p.items] : p.items);
    setCursor(p.page.hasMore ? p.page.nextCursor : null);
  }
  useEffect(() => { void load(); }, []);

  return (
    <section>
      <h1>سابقه ممیزی</h1>
      <form onSubmit={(e) => { e.preventDefault(); void load(); }}>
        <label>نوع رویداد{' '}
          <select data-testid="audit-group" value={action} onChange={(e) => setAction(e.target.value)}>
            {GROUPS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
            {Object.entries(ACTIONS).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
          </select>
        </label>{' '}
        <label>از <input type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} /></label>{' '}
        <label>تا <input type="datetime-local" value={to} onChange={(e) => setTo(e.target.value)} /></label>{' '}
        <button type="submit" data-testid="audit-search" style={{ minHeight: 44 }}>جستجو</button>
      </form>
      {error && <p role="alert" data-testid="audit-error">{MESSAGES[error] ?? error}</p>}
      {rows && rows.length === 0 && <p data-testid="audit-empty">رکوردی یافت نشد.</p>}
      {rows && rows.length > 0 && (
        <table data-testid="audit-rows">
          <thead><tr><th>#</th><th>زمان</th><th>رویداد</th><th>کاربر و نقش</th><th>منبع</th><th>از کجا</th><th>جزئیات</th></tr></thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.seq} data-action={r.action}>
                <td>{r.seq}</td>
                <td>{new Date(r.occurredAt).toLocaleString('fa-IR')}</td>
                <td>{ACTIONS[r.action] ?? r.action}</td>
                <td><code>{r.actorId?.slice(0, 8) ?? r.actorKind}</code> {r.actorRoles.join('، ')}</td>
                <td>{r.resourceType} <code>{r.resourceId?.slice(0, 8)}</code></td>
                <td>{r.clientIp ?? '—'}{r.terminalId ? ' · دستگاه ثبت‌شده' : ''}</td>
                <td><code dir="ltr">{JSON.stringify(r.details)}</code>{r.reason ? ` — ${r.reason}` : ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {cursor && <p><button data-testid="audit-more" onClick={() => void load(cursor)} style={{ minHeight: 44 }}>موارد قدیمی‌تر</button></p>}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
