'use client';
import { useEffect, useState } from 'react';
import { codeOf, MESSAGES, postJson } from '@/lib/client';

type Branch = { id: string; code: string; name: string; status: string };

/** US-012 / TEN-002: branches of the store; a new branch gets register R1 and its warehouse automatically. */
export default function BranchesPage() {
  const [branches, setBranches] = useState<Branch[] | null>(null);
  const [form, setForm] = useState({ code: '', name: '' });
  const [error, setError] = useState('');

  async function load() {
    const r = await fetch('/bff/branches', { cache: 'no-store' });
    if (!r.ok) return setError(await codeOf(r));
    setBranches(await r.json());
  }
  useEffect(() => { void load(); }, []);

  async function create(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    const r = await postJson('/bff/branches', { code: form.code.toUpperCase(), name: form.name });
    if (r.status !== 201) return setError(await codeOf(r));
    setForm({ code: '', name: '' });
    await load();
  }

  if (!branches) return error ? <p role="alert">{MESSAGES[error] ?? error}</p> : <p>در حال بارگذاری…</p>;
  return (
    <section>
      <h1>شعب</h1>
      <ul data-testid="branches">{branches.map((b) => <li key={b.id}>{b.code}: {b.name}</li>)}</ul>
      <h2>شعبه جدید</h2>
      <form onSubmit={(e) => void create(e)} data-testid="branch-form">
        <label>کد (مثل B2)
          <input name="code" required maxLength={10} pattern="[A-Za-z0-9][A-Za-z0-9-]{0,9}" value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} />
        </label>
        <label>نام
          <input name="name" required maxLength={100} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
        </label>
        <button type="submit" style={{ minHeight: 44 }} data-testid="branch-submit">ایجاد شعبه</button>
      </form>
      <p>هر شعبه جدید خودکار یک صندوق (R1) و یک انبار دارد.</p>
      {error && <p role="alert" data-testid="branch-error">{MESSAGES[error] ?? error}</p>}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
