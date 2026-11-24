'use client';
import { useCallback, useEffect, useRef, useState } from 'react';
import { csrf, MESSAGES } from '@/lib/client';

type Register = { id: string; code: string; mayOpenShift: boolean; shift: { id: string; status: string; mine: boolean } | null };
type Context = { terminalId: string; branchId: string; registers: Register[] };
type Line = { lineNo: number; name: string; sku: string; qty: string; unit: string; weighted: boolean; unitPrice: string; lineTotal: string;
  discounts: { amount: string }[] };
type Tender = { seq: number; method: string; amount: string; reference: string | null; tendered: string | null; change: string | null };
type Sale = { id: string; version: number; lines: Line[]; tenders: Tender[];
  totals: { gross: string; discount: string; tax: string; total: string; paid: string; remaining: string; change: string } };

/** SAL-002.1 / AT-061: the same code again within this window is a double read, not a second item (configurable later). */
const DUPLICATE_MS = 1500;
const UNITS = ['EA', 'PK', 'CT', 'KG'];
const rial = (v: string) => Number(v).toLocaleString('fa-IR');
const latin = (v: string) => v.replace(/[۰-۹]/g, (d) => String('۰۱۲۳۴۵۶۷۸۹'.indexOf(d))).replace(/[٫]/g, '.').trim();

const ERRORS: Record<string, string> = {
  'barcode-not-found': 'کالا یافت نشد. آن را در «کالاها» تعریف کنید یا با نام جستجو کنید.',
  'invalid-barcode': 'بارکد معتبر نیست؛ دوباره اسکن کنید.',
  'price-missing': 'این کالا هنوز قیمت ندارد.',
  'not-sellable': 'این کالا غیرفعال است.',
  'unknown-unit': 'این واحد برای کالا تعریف نشده است.',
  'weighted-line': 'مقدار کالای وزنی از برچسب خوانده می‌شود؛ حذف و دوباره اسکن کنید.',
  'version-conflict': 'سبد هم‌زمان تغییر کرد؛ دوباره امتحان کنید.',
  'shift-not-open': 'شیفت باز نیست.',
  'terminal-not-registered': 'این دستگاه صندوق ثبت‌شده این شعبه نیست.',
  'sale-has-payments': 'برای این فاکتور پرداخت ثبت شده است؛ اقلام دیگر تغییر نمی‌کنند.',
  'amount-exceeds-remaining': 'مبلغ از باقیمانده فاکتور بیشتر است.',
  'duplicate-reference': 'این شماره پیگیری امروز برای همین کارتخوان ثبت شده است.',
};

const store = {
  get: (k: string) => { try { return localStorage.getItem(k); } catch { return null; } },
  set: (k: string, v: string | null) => { try { if (v === null) localStorage.removeItem(k); else localStorage.setItem(k, v); } catch { /* private mode */ } },
};

/**
 * US-082 / SAL-001 / SAL-002: the register screen. A barcode reader that types like a keyboard (keyboard wedge) is read
 * on this page wherever the focus is; a code can also be typed by hand, or read by the camera where the browser can.
 * Prices, discounts and tax come from the server; the cart is a draft until payment (EP-07).
 */
export default function PosPage() {
  const [context, setContext] = useState<Context | null>(null);
  const [registerId, setRegisterId] = useState('');
  const [opening, setOpening] = useState('0');
  const [sale, setSale] = useState<Sale | null>(null);
  const [manual, setManual] = useState('');
  const [error, setError] = useState('');
  const [ignored, setIgnored] = useState(0);
  const [camera, setCamera] = useState(false);
  const [pay, setPay] = useState({ cash: '', tendered: '', card: '', reference: '', terminalNo: '', last4: '' });
  const [override, setOverride] = useState<{ approvalId: string; body: Record<string, unknown> } | null>(null);
  const last = useRef<{ code: string; at: number } | null>(null);
  const buffer = useRef({ text: '', at: 0 });
  const busy = useRef<Promise<void>>(Promise.resolve());
  const video = useRef<HTMLVideoElement | null>(null);
  const current = useRef<Sale | null>(null);   // queued scans see the latest cart, not the render they were queued in
  useEffect(() => { current.current = sale; }, [sale]);

  const load = useCallback(async () => {
    const r = await fetch('/bff/pos/context', { cache: 'no-store' });
    if (!r.ok) {
      const p = await r.json().catch(() => null) as { code?: string } | null;
      setError(p?.code ?? `http-${r.status}`);
      return;
    }
    const c: Context = await r.json();
    setContext(c);
    const mine = c.registers.find((x) => x.shift?.mine) ?? c.registers.find((x) => !x.shift && x.mayOpenShift);
    if (mine) setRegisterId((id) => id || mine.id);
  }, []);
  useEffect(() => { void load(); }, [load]);

  const register = context?.registers.find((r) => r.id === registerId);
  const draftKey = `rc-pos-draft-${registerId}`;

  // the cart of this register survives a reload: its clientDraftId is kept on the device (DI-010)
  useEffect(() => {
    const kept = registerId ? store.get(draftKey) : null;
    if (!kept || !register?.shift?.mine) return;
    void (async () => {
      const r = await fetch('/bff/pos/sales', {
        method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() },
        body: JSON.stringify({ registerId, clientDraftId: kept }),
      });
      const s = r.ok ? await r.json() : null;
      if (s?.status === 'Draft') setSale(s); else store.set(draftKey, null);
    })();
  }, [registerId, register?.shift?.mine, draftKey]);

  async function openShift(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    const r = await fetch('/bff/shifts', {
      method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf(), 'idempotency-key': crypto.randomUUID() },
      body: JSON.stringify({ registerId, openingCash: latin(opening).replace(/[,٬]/g, '') }),
    });
    if (!r.ok) return setError((await r.json().catch(() => null))?.code ?? `http-${r.status}`);
    void load();
  }

  async function ensureSale(): Promise<Sale | null> {
    if (current.current) return current.current;
    const draft = store.get(draftKey) ?? crypto.randomUUID();
    const r = await fetch('/bff/pos/sales', {
      method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() },
      body: JSON.stringify({ registerId, clientDraftId: draft }),
    });
    if (!r.ok) { setError((await r.json().catch(() => null))?.code ?? `http-${r.status}`); return null; }
    store.set(draftKey, draft);
    const s: Sale = await r.json();
    current.current = s;
    setSale(s);
    return s;
  }

  async function addCode(code: string) {
    setError('');
    const s = await ensureSale();
    if (!s) return;
    const r = await fetch(`/bff/pos/sales/${s.id}/lines`, {
      method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() }, body: JSON.stringify({ barcode: code, qty: '1' }),
    });
    const body = await r.json().catch(() => null);
    if (r.ok) { current.current = body; setSale(body); } else setError(body?.code ?? `http-${r.status}`);
  }

  // every read goes through here: keyboard wedge, manual entry and camera alike; scans are applied in order
  const onScan = useCallback((raw: string) => {
    const code = latin(raw);
    if (!code) return;
    const now = Date.now();
    if (last.current && last.current.code === code && now - last.current.at < DUPLICATE_MS) {
      setIgnored((n) => n + 1);
      return;
    }
    last.current = { code, at: now };
    busy.current = busy.current.then(() => addCode(code));
  }, [registerId]);

  // SAL-002: a keyboard-wedge reader types the code and Enter; read it wherever the focus is (not inside another field)
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      const t = e.target as HTMLElement | null;
      if (t && ['INPUT', 'SELECT', 'TEXTAREA'].includes(t.tagName)) return;
      if (!register?.shift?.mine) return;
      const now = Date.now();
      if (now - buffer.current.at > 300) buffer.current.text = '';
      buffer.current.at = now;
      if (e.key === 'Enter') {
        const text = buffer.current.text;
        buffer.current.text = '';
        if (text.length >= 3) { e.preventDefault(); onScan(text); }
      } else if (e.key.length === 1) {
        buffer.current.text += e.key;
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onScan, register?.shift?.mine]);

  // SAL-002: continuous camera reading where the browser has BarcodeDetector (HTTPS and the user's permission; SP-06)
  useEffect(() => {
    if (!camera) return;
    let stream: MediaStream | null = null;
    let timer: ReturnType<typeof setInterval> | undefined;
    void (async () => {
      try {
        const Detector = (window as unknown as { BarcodeDetector?: new (o: { formats: string[] }) => { detect: (v: HTMLVideoElement) => Promise<{ rawValue: string }[]> } }).BarcodeDetector;
        if (!Detector) { setError('camera-unsupported'); setCamera(false); return; }
        stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } });
        if (video.current) { video.current.srcObject = stream; await video.current.play(); }
        const detector = new Detector({ formats: ['ean_13', 'ean_8', 'upc_a', 'code_128'] });
        timer = setInterval(async () => {
          if (!video.current) return;
          for (const c of await detector.detect(video.current).catch(() => [])) onScan(c.rawValue);
        }, 300);
      } catch {
        setError('camera-denied');
        setCamera(false);
      }
    })();
    return () => { if (timer) clearInterval(timer); stream?.getTracks().forEach((t) => t.stop()); };
  }, [camera, onScan]);

  async function change(line: Line, patch: { qty?: string; unit?: string }) {
    if (!sale) return;
    setError('');
    const r = await fetch(`/bff/pos/sales/${sale.id}/lines/${line.lineNo}`, {
      method: 'PATCH', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() },
      body: JSON.stringify({ expectedVersion: sale.version, ...patch }),
    });
    const body = await r.json().catch(() => null);
    if (r.ok) setSale(body); else setError(body?.code ?? `http-${r.status}`);
  }

  async function remove(line: Line) {
    if (!sale) return;
    setError('');
    const r = await fetch(`/bff/pos/sales/${sale.id}/lines/${line.lineNo}?expectedVersion=${sale.version}`, {
      method: 'DELETE', headers: { 'x-csrf-token': csrf() },
    });
    const body = await r.json().catch(() => null);
    if (r.ok) setSale(body); else setError(body?.code ?? `http-${r.status}`);
  }

  // US-101: one payment at a time; a repeated card slip waits for a manager and is then sent again with the approval
  async function tender(body: Record<string, unknown>) {
    if (!sale) return;
    setError('');
    const r = await fetch(`/bff/pos/sales/${sale.id}/tenders`, {
      method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf(), 'idempotency-key': crypto.randomUUID() },
      body: JSON.stringify(body),
    });
    const p = await r.json().catch(() => null);
    if (r.ok) {
      current.current = p;
      setSale(p);
      setOverride(null);
      setPay({ cash: '', tendered: '', card: '', reference: '', terminalNo: '', last4: '' });
      return;
    }
    if (p?.code === 'duplicate-reference' && p.context?.approvalRequestId) {
      setOverride({ approvalId: p.context.approvalRequestId, body: { ...body, approvalId: undefined } });
      if (body.approvalId) setError('approval-not-yet');
      else setError('duplicate-reference');
      return;
    }
    setError(p?.code ?? `http-${r.status}`);
  }
  const money = (v: string) => latin(v).replace(/[,٬]/g, '');

  if (!context) {
    return (
      <section>
        <h1>صندوق فروش</h1>
        {error && <p role="alert" data-testid="pos-error">{ERRORS[error] ?? MESSAGES[error] ?? error}</p>}
        {error === 'terminal-not-registered' && <p><a href="/terminals">ثبت این دستگاه به‌عنوان صندوق</a></p>}
        <p><a href="/">بازگشت</a></p>
      </section>
    );
  }

  const step = (q: string, d: number) => String(Math.max(0, Number(q) + d));
  return (
    <section>
      <h1>صندوق فروش</h1>
      <label>صندوق{' '}
        <select value={registerId} onChange={(e) => { setRegisterId(e.target.value); setSale(null); }} data-testid="pos-register">
          {context.registers.map((r) => (
            <option key={r.id} value={r.id}>{r.code}{r.shift ? (r.shift.mine ? ' (شیفت شما)' : ' (شیفت دیگری)') : ''}</option>
          ))}
        </select>
      </label>
      {register && !register.shift && (
        <form onSubmit={(e) => void openShift(e)} data-testid="pos-open-shift">
          <label>نقد ابتدای شیفت (ریال) <input value={opening} onChange={(e) => setOpening(e.target.value)} dir="ltr" inputMode="numeric" size={10}
            data-testid="pos-opening" /></label>{' '}
          <button type="submit" style={{ minHeight: 44 }} data-testid="pos-open-submit" disabled={!register.mayOpenShift}>باز کردن شیفت</button>
        </form>
      )}
      {register?.shift && !register.shift.mine && <p role="status">شیفت این صندوق مال کاربر دیگری است.</p>}

      {register?.shift?.mine && (
        <>
          <p data-testid="pos-ready">بارکدخوان آماده است؛ کافی است اسکن کنید.</p>
          <form onSubmit={(e) => { e.preventDefault(); onScan(manual); setManual(''); }}>
            <label>ورود دستی کد <input value={manual} onChange={(e) => setManual(e.target.value)} dir="ltr" size={16} inputMode="numeric"
              data-testid="pos-manual" /></label>{' '}
            <button type="submit" style={{ minHeight: 44 }} data-testid="pos-manual-submit">افزودن</button>{' '}
            <button type="button" style={{ minHeight: 44 }} onClick={() => setCamera(!camera)}>{camera ? 'خاموش کردن دوربین' : 'اسکن با دوربین'}</button>
          </form>
          {camera && <video ref={video} muted playsInline style={{ maxWidth: '100%', maxHeight: 240 }} />}
          {ignored > 0 && <p role="status" data-testid="pos-ignored" data-count={ignored}>اسکن تکراری (کمتر از ۱٫۵ ثانیه) نادیده گرفته شد.</p>}
          <table data-testid="pos-lines">
            <thead><tr><th>کالا</th><th>مقدار</th><th>واحد</th><th>فی</th><th>تخفیف</th><th>جمع</th><th /></tr></thead>
            <tbody>
              {(sale?.lines ?? []).map((l) => (
                <tr key={l.lineNo} data-sku={l.sku} data-qty={l.qty}>
                  <td>{l.name}</td>
                  <td>
                    {l.weighted || (sale?.tenders.length ?? 0) > 0 ? Number(l.qty).toLocaleString('fa-IR') : (
                      <>
                        <button type="button" style={{ minHeight: 44, minWidth: 44 }} aria-label="یکی کمتر" disabled={Number(l.qty) <= 1}
                          onClick={() => void change(l, { qty: step(l.qty, -1) })}>−</button>{' '}
                        {Number(l.qty).toLocaleString('fa-IR')}{' '}
                        <button type="button" style={{ minHeight: 44, minWidth: 44 }} aria-label="یکی بیشتر" data-testid={`pos-plus-${l.sku}`}
                          onClick={() => void change(l, { qty: step(l.qty, 1) })}>+</button>
                      </>
                    )}
                  </td>
                  <td>
                    {l.weighted || (sale?.tenders.length ?? 0) > 0 ? l.unit : (
                      <select value={l.unit} onChange={(e) => void change(l, { unit: e.target.value })} aria-label="واحد">
                        {[...new Set([l.unit, ...UNITS])].map((u) => <option key={u} value={u}>{u}</option>)}
                      </select>
                    )}
                  </td>
                  <td>{rial(l.unitPrice)}</td>
                  <td>{l.discounts.length > 0 ? rial(l.discounts[0].amount) : '—'}</td>
                  <td>{rial(l.lineTotal)}</td>
                  <td>{(sale?.tenders.length ?? 0) === 0 && (
                    <button type="button" style={{ minHeight: 44 }} onClick={() => void remove(l)} data-testid={`pos-remove-${l.sku}`}>حذف</button>)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {sale && (
            <p data-testid="pos-total" data-total={sale.totals.total}>
              جمع: {rial(sale.totals.gross)} · تخفیف: {rial(sale.totals.discount)} · مالیات: {rial(sale.totals.tax)} ·{' '}
              <strong>قابل پرداخت: {rial(sale.totals.total)} ریال</strong>
            </p>
          )}
          {sale && sale.lines.length > 0 && (
            <div data-testid="pos-pay" data-remaining={sale.totals.remaining}>
              <h2>پرداخت</h2>
              <ul data-testid="pos-tenders">
                {sale.tenders.map((t) => (
                  <li key={t.seq} data-method={t.method}>
                    {t.method === 'Cash' ? 'نقد' : 'کارتخوان'}: {rial(t.amount)} ریال
                    {t.reference ? ` (پیگیری ${t.reference})` : ''}
                    {t.change && t.change !== '0' ? ` · دریافتی ${rial(t.tendered ?? '0')} · باقیمانده به مشتری ${rial(t.change)}` : ''}
                  </li>
                ))}
              </ul>
              <p>پرداخت‌شده: {rial(sale.totals.paid)} · <strong data-testid="pos-remaining">مانده: {rial(sale.totals.remaining)} ریال</strong></p>
              {sale.totals.remaining !== '0' ? (
                <>
                  <form onSubmit={(e) => {
                    e.preventDefault();
                    const amount = money(pay.cash) || sale.totals.remaining;
                    void tender({ method: 'Cash', amount, tendered: money(pay.tendered) || amount });
                  }} data-testid="pos-cash-form">
                    <label>نقد (ریال) <input value={pay.cash} placeholder={rial(sale.totals.remaining)} onChange={(e) => setPay({ ...pay, cash: e.target.value })}
                      dir="ltr" inputMode="numeric" size={10} data-testid="pos-cash-amount" /></label>{' '}
                    <label>مبلغ دریافتی <input value={pay.tendered} onChange={(e) => setPay({ ...pay, tendered: e.target.value })} dir="ltr" inputMode="numeric"
                      size={10} data-testid="pos-cash-tendered" /></label>{' '}
                    {pay.tendered && (
                      <span data-testid="pos-change-preview">باقیمانده به مشتری: {rial(String(Math.max(0,
                        Number(money(pay.tendered)) - Number(money(pay.cash) || sale.totals.remaining))))}</span>
                    )}{' '}
                    <button type="submit" style={{ minHeight: 44 }} data-testid="pos-cash-submit">ثبت نقد</button>
                  </form>
                  <form onSubmit={(e) => {
                    e.preventDefault();
                    void tender({
                      method: 'ExternalCardTerminal', amount: money(pay.card) || sale.totals.remaining, referenceNo: latin(pay.reference),
                      terminalNo: latin(pay.terminalNo) || undefined, cardLast4: latin(pay.last4) || undefined,
                    });
                  }} data-testid="pos-card-form">
                    <label>کارتخوان (ریال) <input value={pay.card} placeholder={rial(sale.totals.remaining)} onChange={(e) => setPay({ ...pay, card: e.target.value })}
                      dir="ltr" inputMode="numeric" size={10} data-testid="pos-card-amount" /></label>{' '}
                    <label>شماره پیگیری <input required value={pay.reference} onChange={(e) => setPay({ ...pay, reference: e.target.value })} dir="ltr" size={14}
                      data-testid="pos-card-reference" /></label>{' '}
                    <label>شماره پایانه <input value={pay.terminalNo} onChange={(e) => setPay({ ...pay, terminalNo: e.target.value })} dir="ltr" size={8} /></label>{' '}
                    <label>۴ رقم آخر کارت <input value={pay.last4} onChange={(e) => setPay({ ...pay, last4: e.target.value })} dir="ltr" size={4} inputMode="numeric" /></label>{' '}
                    <button type="submit" style={{ minHeight: 44 }} data-testid="pos-card-submit">ثبت کارتخوان</button>
                  </form>
                  {override && (
                    <p role="status" data-testid="pos-override">
                      برای ثبت دوباره این شماره پیگیری، تأیید مدیر لازم است (<a href="/approvals">تأییدها</a>).{' '}
                      <button type="button" style={{ minHeight: 44 }} onClick={() => void tender({ ...override.body, approvalId: override.approvalId })}>
                        ثبت پس از تأیید</button>
                    </p>
                  )}
                </>
              ) : (
                <p role="status" data-testid="pos-paid">مبلغ فاکتور کامل پرداخت شد. نهایی‌سازی و صدور فاکتور در گام بعد (US-091) اضافه می‌شود.</p>
              )}
            </div>
          )}
        </>
      )}
      {error && <p role="alert" data-testid="pos-error">{ERRORS[error] ?? MESSAGES[error] ?? (error === 'camera-unsupported'
        ? 'این مرورگر خواندن بارکد با دوربین را پشتیبانی نمی‌کند؛ از بارکدخوان یا ورود دستی استفاده کنید.'
        : error === 'camera-denied' ? 'اجازه دوربین داده نشد.' : error)}</p>}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
