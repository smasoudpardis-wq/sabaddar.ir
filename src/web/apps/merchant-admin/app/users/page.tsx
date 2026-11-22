'use client';
import { useEffect, useState } from 'react';
import { codeOf, MESSAGES, postJson, ROLE_LABELS } from '@/lib/client';

type Assignment = { id: string; roleName: string; scopeType: string; scopeId: string | null };
type User = { id: string; displayName: string; status: string; roleAssignments: Assignment[] };
type Role = { id: string; name: string };
type Branch = { id: string; code: string; name: string };

/** US-025: users of the active store and invitations with role + scope (D09 users screen, minimal). */
export default function UsersPage() {
  const [users, setUsers] = useState<User[] | null>(null);
  const [roles, setRoles] = useState<Role[]>([]);
  const [branches, setBranches] = useState<Branch[]>([]);
  const [form, setForm] = useState({ displayName: '', roleId: '', branchId: '' });
  const [link, setLink] = useState('');
  const [error, setError] = useState('');

  async function load() {
    // all three together: user rows show branch names, so they must not render before the branches arrive
    const [r, ro, br] = await Promise.all([fetch('/bff/users', { cache: 'no-store' }), fetch('/bff/roles'), fetch('/bff/branches')]);
    if (!r.ok) return setError(await codeOf(r));
    if (ro.ok) setRoles(await ro.json());
    if (br.ok) setBranches(await br.json());
    setUsers(await r.json());
  }
  useEffect(() => { void load(); }, []);

  const branchName = (id: string | null) => branches.find((b) => b.id === id)?.name ?? (id ? '—' : 'کل فروشگاه');

  async function invite(e: React.FormEvent) {
    e.preventDefault();
    setError(''); setLink('');
    const r = await postJson('/bff/invitations', {
      displayName: form.displayName,
      roleAssignments: [{ roleId: form.roleId, scopeType: form.branchId ? 'Branch' : 'Tenant', scopeId: form.branchId || null }],
    });
    if (r.status !== 201) return setError(await codeOf(r));
    const inv = await r.json();
    // Delivery by SMS/e-mail arrives with Notifications (X-03); until then the link is shared by the inviter.
    setLink(`${window.location.origin}/invite/${inv.inviteToken}`);
    await load();
  }

  if (error && !users) return <p role="alert" data-testid="users-error">{MESSAGES[error] ?? error}</p>;
  if (!users) return <p>در حال بارگذاری…</p>;
  return (
    <section>
      <h1>کاربران فروشگاه</h1>
      <ul data-testid="users">
        {users.map((u) => (
          <li key={u.id}>
            {u.displayName}: {u.roleAssignments.map((a) => `${ROLE_LABELS[a.roleName] ?? a.roleName} (${branchName(a.scopeId)})`).join('، ')}
          </li>
        ))}
      </ul>
      <h2>دعوت همکار</h2>
      <form onSubmit={(e) => void invite(e)} data-testid="invite-form">
        <label>نام
          <input name="displayName" required maxLength={100} value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.target.value })} />
        </label>
        <label>نقش
          <select name="roleId" required value={form.roleId} onChange={(e) => setForm({ ...form, roleId: e.target.value })}>
            <option value="">انتخاب کنید</option>
            {roles.map((r) => <option key={r.id} value={r.id}>{ROLE_LABELS[r.name] ?? r.name}</option>)}
          </select>
        </label>
        <label>محدوده
          <select name="branchId" value={form.branchId} onChange={(e) => setForm({ ...form, branchId: e.target.value })}>
            <option value="">کل فروشگاه</option>
            {branches.map((b) => <option key={b.id} value={b.id}>شعبه {b.name}</option>)}
          </select>
        </label>
        <button type="submit" style={{ minHeight: 44 }} data-testid="invite-submit">ساخت لینک دعوت</button>
      </form>
      {link && (
        <p>لینک دعوت (یک‌بار مصرف، ۷ روز): <code data-testid="invite-link">{link}</code></p>
      )}
      {error && <p role="alert" data-testid="users-error">{MESSAGES[error] ?? error}</p>}
      <p><a href="/">بازگشت</a></p>
    </section>
  );
}
