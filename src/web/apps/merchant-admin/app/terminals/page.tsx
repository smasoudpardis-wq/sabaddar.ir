'use client';
import { useEffect, useState } from 'react';
import { codeOf, csrf, MESSAGES, postJson } from '@/lib/client';

type Terminal = { id: string; branchId: string; deviceName: string; status: string };
type Branch = { id: string; name: string };

/** US-013 / TEN-002.1: issue pairing codes, pair this device, revoke devices. */
export default function TerminalsPage() {
  const [terminals, setTerminals] = useState<Terminal[] | null>(null);
  const [branches, setBranches] = useState<Branch[]>([]);
  const [thisDevice, setThisDevice] = useState<string | null>(null);
  const [branchId, setBranchId] = useState('');
  const [issued, setIssued] = useState('');
  const [pair, setPair] = useState({ code: '', name: '' });
  const [error, setError] = useState('');
  const [myPin, setMyPin] = useState('');
  const [pinSaved, setPinSaved] = useState(false);

  async function load() {
    const [t, b, me] = await Promise.all([fetch('/bff/terminals', { cache: 'no-store' }), fetch('/bff/branches'), fetch('/bff/me', { cache: 'no-store' })]);
    if (b.ok) { const list = await b.json(); setBranches(list); setBranchId((x) => x || list[0]?.id || ''); }
    if (me.ok) setThisDevice((await me.json()).terminal?.id ?? null);
    setTerminals(t.ok ? await t.json() : []);
  }
  useEffect(() => { void load(); }, []);

  async function issue() {
    setError(''); setIssued('');
    const r = await postJson(`/bff/branches/${branchId}/pairing-codes`);
    if (r.status !== 201) return setError(await codeOf(r));
    setIssued((await r.json()).code);
  }

  async function pairThis(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    const r = await postJson('/bff/terminals/pair', { pairingCode: pair.code, deviceName: pair.name });
    if (r.status !== 201) return setError(await codeOf(r));
    setPair({ code: '', name: '' });
    await load();
  }

  async function revoke(id: string) {
    const r = await fetch(`/bff/terminals/${id}`, { method: 'DELETE', headers: { 'x-csrf-token': csrf() } });
    if (r.status !== 204) return setError(await codeOf(r));
    await load();
  }

  if (!terminals) return <p>در حال بارگذاری…</p>;
  return (
    <section>
      <h1>دستگاه‌های صندوق</h1>
      <p data-testid="this-device">{thisDevice ? 'این دستگاه صندوق ثبت‌شده است.' : 'این دستگاه صندوق ثبت‌شده نیست.'}</p>
      <ul data-testid="terminals">
        {terminals.map((t) => (
          <li key={t.id}>
            {t.deviceName} — {branches.find((b) => b.id === t.branchId)?.name ?? ''} — {t.status === 'Active' ? 'فعال' : 'ابطال‌شده'}
            {t.status === 'Active' && <button onClick={() => void revoke(t.id)} data-testid={`revoke-${t.deviceName}`}>ابطال</button>}
          </li>
        ))}
      </ul>
      <h2>صدور کد جفت‌سازی (۱۰ دقیقه)</h2>
      <select value={branchId} onChange={(e) => setBranchId(e.target.value)} name="branch">
        {branches.map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
      </select>
      <button onClick={() => void issue()} data-testid="issue-code" style={{ minHeight: 44 }}>صدور کد</button>
      {issued && <p>کد: <strong data-testid="pairing-code">{issued}</strong></p>}
      <h2>PIN من (برای باز کردن قفل صندوق)</h2>
      <form onSubmit={(e) => { e.preventDefault(); void (async () => {
        setError('');
        const r = await fetch('/bff/pin', { method: 'PUT', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() }, body: JSON.stringify({ pin: myPin }) });
        setMyPin('');
        if (r.status !== 204) return setError(await codeOf(r));
        setPinSaved(true);
      })(); }} data-testid="pin-form">
        <input name="newPin" inputMode="numeric" pattern="[0-9]{4,6}" maxLength={6} required value={myPin} onChange={(e) => setMyPin(e.target.value)} aria-label="PIN جدید" />
        <button type="submit" data-testid="pin-submit" style={{ minHeight: 44 }}>ذخیره PIN</button>
        {pinSaved && <span data-testid="pin-saved"> ذخیره شد</span>}
      </form>
      <h2>ثبت همین دستگاه</h2>
      <form onSubmit={(e) => void pairThis(e)} data-testid="pair-form">
        <label>کد <input name="pairingCode" required value={pair.code} onChange={(e) => setPair({ ...pair, code: e.target.value })} /></label>
        <label>نام دستگاه <input name="deviceName" required maxLength={100} value={pair.name} onChange={(e) => setPair({ ...pair, name: e.target.value })} /></label>
        <button type="submit" style={{ minHeight: 44 }} data-testid="pair-submit">ثبت دستگاه</button>
      </form>
      {error && <p role="alert" data-testid="terminal-error">{MESSAGES[error] ?? error}</p>}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
