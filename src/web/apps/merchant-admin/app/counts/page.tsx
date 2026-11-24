'use client';
import { useCallback, useEffect, useState } from 'react';
import { codeOf, csrf, MESSAGES } from '@/lib/client';

type Warehouse = { id: string; code: string };
type Count = { id: string; warehouseId: string; blind: boolean; status: string; snapshotAt: string; version: number; counters: number; adjustmentId: string | null };
type Line = { variantId: string; systemAtSnapshot: string; movementsSince: string; counted: string; difference: string; value: string };
type Result = { lines: Line[]; totalValueDifference: string; approvalRequired: boolean };
type Product = { id: string; name: string; variants: { id: string; sku: string }[] };
type Batch = { countId: string; clientBatchId: string; entries: { variantId: string; qty: string }[] };

const QUEUE = 'rc-count-queue';
const latin = (v: string) => v.replace(/[۰-۹]/g, (d) => String('۰۱۲۳۴۵۶۷۸۹'.indexOf(d))).replace(/[٫]/g, '.').replace(/[−–]/g, '-').trim();
const num = (v: string) => Number(v).toLocaleString('fa-IR');
const ERRORS: Record<string, string> = {
  'unknown-sku': 'کالایی با این SKU پیدا نشد.',
  'count-open': 'برای این انبار یک انبارگردانی باز وجود دارد.',
  'count-not-open': 'شمارش این انبارگردانی بسته شده است.',
  'count-not-closed': 'اول شمارش را ببندید.',
  'insufficient-stock': 'پس از بستن شمارش، موجودی کمتر از اصلاح لازم شده است؛ دوباره شمارش کنید.',
};

// a batch is kept on the device until the server has it: counting goes on without the network (UC-09)
const queue = {
  read: (): Batch[] => { try { return JSON.parse(localStorage.getItem(QUEUE) ?? '[]'); } catch { return []; } },
  write: (b: Batch[]) => { try { localStorage.setItem(QUEUE, JSON.stringify(b)); } catch { /* private mode: sent at once or lost with the page */ } },
};

/** US-065 / INV-009 / UC-09: a stock count with a snapshot, several counters, and the confirmed correction. */
export default function CountsPage() {
  const [warehouses, setWarehouses] = useState<Warehouse[]>([]);
  const [counts, setCounts] = useState<Count[]>([]);
  const [open, setOpen] = useState({ warehouseId: '', blind: true });
  const [entry, setEntry] = useState({ sku: '', qty: '1' });
  const [pending, setPending] = useState(0);
  const [result, setResult] = useState<{ countId: string; data: Result } | null>(null);
  const [approval, setApproval] = useState<{ countId: string; id: string } | null>(null);
  const [error, setError] = useState('');

  const load = useCallback(async () => {
    const w = await fetch('/bff/warehouses', { cache: 'no-store' });
    if (w.ok) {
      const items: Warehouse[] = (await w.json()).items;
      setWarehouses(items);
      setOpen((o) => (o.warehouseId || items.length === 0 ? o : { ...o, warehouseId: items[0].id }));
    }
    const c = await fetch('/bff/inventory/counts', { cache: 'no-store' });
    if (c.ok) setCounts((await c.json()).items);
  }, []);

  const flush = useCallback(async () => {
    let left = queue.read();
    for (const b of [...left]) {
      try {
        const r = await fetch(`/bff/inventory/counts/${b.countId}/entries`, {
          method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() },
          body: JSON.stringify({ clientBatchId: b.clientBatchId, entries: b.entries }),
        });
        if (!r.ok && r.status < 500) setError(await codeOf(r));   // refused for good (closed count…): dropped, the person is told
        if (r.ok || r.status < 500) left = left.filter((x) => x.clientBatchId !== b.clientBatchId);
      } catch {
        break;   // no network: keep the rest for later
      }
    }
    queue.write(left);
    setPending(left.length);
  }, []);

  useEffect(() => {
    void load();
    void flush();
    const online = () => void flush();
    window.addEventListener('online', online);
    return () => window.removeEventListener('online', online);
  }, [load, flush]);

  async function start(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    const r = await fetch('/bff/inventory/counts', {
      method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf(), 'idempotency-key': crypto.randomUUID() },
      body: JSON.stringify(open),
    });
    if (!r.ok) return setError(await codeOf(r));
    void load();
  }

  async function addEntry(e: React.FormEvent, count: Count) {
    e.preventDefault();
    setError('');
    const found = await fetch(`/bff/products?q=${encodeURIComponent(entry.sku.trim())}`, { cache: 'no-store' }).catch(() => null);
    const products: Product[] = found?.ok ? (await found.json()).items : [];
    const variantId = products.flatMap((p) => p.variants).find((v) => v.sku === entry.sku.trim())?.id;
    if (!variantId) return setError('unknown-sku');
    queue.write([...queue.read(), { countId: count.id, clientBatchId: crypto.randomUUID(), entries: [{ variantId, qty: latin(entry.qty) }] }]);
    setEntry({ sku: '', qty: '1' });
    await flush();
    await load();
  }

  async function close(count: Count) {
    setError('');
    await flush();
    const r = await fetch(`/bff/inventory/counts/${count.id}/close`, {
      method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() }, body: JSON.stringify({ expectedVersion: count.version }),
    });
    if (!r.ok) return setError(await codeOf(r));
    await load();
    await showResult(count.id);
  }

  async function showResult(id: string) {
    setError('');
    const r = await fetch(`/bff/inventory/counts/${id}/result`, { cache: 'no-store' });
    if (!r.ok) return setError(await codeOf(r));
    setResult({ countId: id, data: await r.json() });
  }

  async function confirm(count: Count) {
    setError('');
    const headers: Record<string, string> = { 'content-type': 'application/json', 'x-csrf-token': csrf(), 'idempotency-key': crypto.randomUUID() };
    if (approval?.countId === count.id) headers['x-approval-id'] = approval.id;
    const r = await fetch(`/bff/inventory/counts/${count.id}/confirm`, { method: 'POST', headers, body: JSON.stringify({ expectedVersion: count.version }) });
    const p = await r.json().catch(() => null);
    if (p?.code === 'approval-required') {
      setApproval({ countId: count.id, id: p.approvalRequestId });
      return setError(approval ? 'approval-not-yet' : 'count-approval');
    }
    if (!r.ok) return setError(p?.code ?? `http-${r.status}`);
    setApproval(null);
    setResult(null);
    void load();
  }

  const code = (id: string) => warehouses.find((w) => w.id === id)?.code ?? '';
  return (
    <section>
      <h1>انبارگردانی</h1>
      <form onSubmit={(e) => void start(e)} data-testid="count-start-form">
        <label>انبار{' '}
          <select value={open.warehouseId} onChange={(e) => setOpen({ ...open, warehouseId: e.target.value })} data-testid="count-warehouse">
            {warehouses.map((w) => <option key={w.id} value={w.id}>{w.code}</option>)}
          </select>
        </label>{' '}
        <label><input type="checkbox" checked={open.blind} onChange={(e) => setOpen({ ...open, blind: e.target.checked })} /> شمارش کور (موجودی سیستم به شمارنده نشان داده نمی‌شود)</label>{' '}
        <button type="submit" style={{ minHeight: 44 }} data-testid="count-start">شروع انبارگردانی</button>
      </form>
      {pending > 0 && <p role="status" data-testid="count-pending">{num(String(pending))} شمارش هنوز ارسال نشده؛ با برقراری اتصال ارسال می‌شود.</p>}
      <ul data-testid="count-list">
        {counts.map((c) => (
          <li key={c.id} data-status={c.status} data-warehouse={code(c.warehouseId)}>
            {code(c.warehouseId)} · {new Date(c.snapshotAt).toLocaleString('fa-IR')} · {c.status === 'Open' ? 'در حال شمارش' : c.status === 'Closed' ? 'بسته، منتظر تأیید' : 'تأییدشده'}
            {' '}· {num(String(c.counters))} شمارنده
            {c.status === 'Open' && (
              <>
                <form onSubmit={(e) => void addEntry(e, c)} data-testid="count-entry-form">
                  <label>SKU <input required value={entry.sku} onChange={(e) => setEntry({ ...entry, sku: e.target.value })} dir="ltr" size={12} data-testid="count-sku" /></label>{' '}
                  <label>تعداد <input required value={entry.qty} onChange={(e) => setEntry({ ...entry, qty: e.target.value })} dir="ltr" size={5} inputMode="decimal"
                    data-testid="count-qty" /></label>{' '}
                  <button type="submit" style={{ minHeight: 44 }} data-testid="count-add">ثبت شمارش</button>
                </form>
                <button type="button" style={{ minHeight: 44 }} onClick={() => void close(c)} data-testid="count-close">پایان شمارش</button>
              </>
            )}
            {c.status === 'Closed' && (
              <>
                {' '}<button type="button" style={{ minHeight: 44 }} onClick={() => void showResult(c.id)}>نتیجه</button>{' '}
                <button type="button" style={{ minHeight: 44 }} onClick={() => void confirm(c)} data-testid="count-confirm">تأیید و اصلاح موجودی</button>
              </>
            )}
          </li>
        ))}
        {counts.length === 0 && <li>هنوز انبارگردانی‌ای نیست.</li>}
      </ul>
      {result && (
        <div data-testid="count-result" data-total={result.data.totalValueDifference}>
          <h2>اختلاف‌ها</h2>
          <table>
            <thead><tr><th>کالا</th><th>شروع</th><th>گردش حین شمارش</th><th>شمارش‌شده</th><th>اختلاف</th><th>ارزش (ریال)</th></tr></thead>
            <tbody>
              {result.data.lines.filter((l) => l.difference !== '0').map((l) => (
                <tr key={l.variantId}><td dir="ltr">{l.variantId.slice(0, 8)}</td><td>{num(l.systemAtSnapshot)}</td><td>{num(l.movementsSince)}</td>
                  <td>{num(l.counted)}</td><td>{num(l.difference)}</td><td>{num(l.value)}</td></tr>
              ))}
            </tbody>
          </table>
          <p>جمع ارزش اختلاف: {num(result.data.totalValueDifference)} ریال{result.data.approvalRequired ? ' — تأیید مدیر دیگری لازم است.' : ''}</p>
        </div>
      )}
      {error && <p role="alert" data-testid="count-error">{ERRORS[error] ?? (error === 'count-approval'
        ? 'شما خودتان شمرده‌اید یا ارزش اختلاف زیاد است: مدیر دیگری باید در «تأییدها» تأیید کند، سپس دوباره «تأیید» بزنید.' : MESSAGES[error] ?? error)}</p>}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
