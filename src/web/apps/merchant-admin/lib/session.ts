import { createHash, randomBytes } from 'node:crypto';
import { config } from './config';
import { open, seal, storageKey, store } from './store';

// Server-side session (D04 §6, AUTH-07). Tokens never leave the server; the browser holds only an opaque id.
export interface Session {
  id: string;
  accessToken: string;
  refreshToken: string;
  accessExpiresAt: number; // epoch seconds
  idToken?: string;
  acr?: string;            // pwd | mfa (US-022)
  activeTenantId?: string;
  csrf: string;
  createdAt: number;       // epoch seconds; absolute lifetime cap
  lastSeen: number;
  lastActive?: number;      // last unlocked API use (auto-lock on terminals, IAM-005)
  lockedAt?: number;        // register locked: only the PIN unlocks it (IAM-004, US-023)
  subject?: string;         // issuer + '\n' + sub: the person this session belongs to (session index, US-024)
  device?: string;          // coarse label shown in the session list, e.g. "Chrome · Windows" (no IP, no full UA)
}

export interface PendingAuth {
  state: string;
  nonce: string;
  codeVerifier: string;
  acr?: 'mfa';
  tenantId?: string;       // select this tenant right after a step-up (US-022)
  returnTo: string;
  createdAt: number;
}

const ABSOLUTE_LIFETIME = 12 * 3600; // = Keycloak ssoSessionMaxLifespan
const now = () => Math.floor(Date.now() / 1000);

export const randomId = (bytes = 32) => randomBytes(bytes).toString('base64url');

const userIndex = (subject: string) => storageKey('u', subject);

export async function saveSession(s: Session): Promise<void> {
  const k = storageKey('s', s.id);
  await store().set(k, seal(JSON.stringify(s), k), config.idleTimeoutSeconds);
  // IAM-005 session list: the index holds storage keys (hashes), never session ids, so a dump of it opens nothing
  if (s.subject) await store().addMember(userIndex(s.subject), k, ABSOLUTE_LIFETIME);
}

export async function createSession(s: Omit<Session, 'id' | 'csrf' | 'lastSeen' | 'createdAt'>): Promise<Session> {
  const session: Session = { ...s, id: randomId(), csrf: randomId(24), createdAt: now(), lastSeen: now() };
  await saveSession(session);
  return session;
}

/** Loads a session; idle timeout is the store TTL (sliding), the absolute lifetime is checked here. */
export async function getSession(id: string | undefined): Promise<Session | undefined> {
  if (!id) return undefined;
  const k = storageKey('s', id);
  const raw = await store().get(k);
  const json = raw ? open(raw, k) : null;
  if (!json) return undefined;
  const s = JSON.parse(json) as Session;
  if (s.id !== id || now() - s.createdAt > ABSOLUTE_LIFETIME) {
    await store().del(k);
    return undefined;
  }
  if (now() - s.lastSeen > 60) {
    s.lastSeen = now();
    await saveSession(s); // slide the idle window at most once a minute
  }
  return s;
}

export const destroySession = (id: string) => store().del(storageKey('s', id));

/** What the browser may know about one of its user's sessions: a handle derived from the storage key, never the id. */
export interface SessionSummary {
  handle: string;
  device: string;
  createdAt: number;
  lastSeen: number;
  mfa: boolean;
}

export const handleOf = (storageKeyOfSession: string) =>
  createHash('sha256').update('rc-session-handle\n' + storageKeyOfSession).digest('base64url').slice(0, 22);

/** The live sessions of a person (US-024). Expired or destroyed entries are pruned from the index on the way. */
export async function sessionsOf(subject: string): Promise<{ key: string; session: Session; summary: SessionSummary }[]> {
  const index = userIndex(subject);
  const out: { key: string; session: Session; summary: SessionSummary }[] = [];
  for (const k of await store().members(index)) {
    const raw = await store().get(k);
    const json = raw ? open(raw, k) : null;
    const s = json ? (JSON.parse(json) as Session) : undefined;
    if (!s || s.subject !== subject || now() - s.createdAt > ABSOLUTE_LIFETIME) {
      await store().removeMember(index, k);
      continue;
    }
    out.push({ key: k, session: s, summary: { handle: handleOf(k), device: s.device ?? '', createdAt: s.createdAt, lastSeen: s.lastSeen, mfa: s.acr === 'mfa' } });
  }
  return out.sort((a, b) => b.session.lastSeen - a.session.lastSeen);
}

/**
 * Ends one session everywhere: at the identity provider first, then in the BFF. Revoking the refresh token ends the whole
 * Keycloak user session (Keycloak 26, checked through the admin API), so that device's SSO cookie no longer signs it in.
 */
export async function endSession(s: Session, endAtIdp: (refreshToken: string) => Promise<unknown>): Promise<void> {
  if (s.refreshToken) await endAtIdp(s.refreshToken).catch(() => undefined);
  const k = storageKey('s', s.id);
  await store().del(k);
  if (s.subject) await store().removeMember(userIndex(s.subject), k);
}

/** A short, non-identifying device label from the User-Agent (browser and OS families only). */
export function deviceLabel(userAgent: string | null): string {
  const ua = userAgent ?? '';
  const browser = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'مرورگر';
  const os = /Android/.test(ua) ? 'Android' : /iPhone|iPad/.test(ua) ? 'iOS' : /Windows/.test(ua) ? 'Windows' : /Mac OS X/.test(ua) ? 'macOS' : /Linux/.test(ua) ? 'Linux' : '';
  return os ? `${browser} · ${os}` : browser;
}

export async function savePending(p: PendingAuth): Promise<string> {
  const id = randomId();
  const k = storageKey('p', id);
  await store().set(k, seal(JSON.stringify(p), k), 600);
  return id;
}

/** Single use: the pending login is deleted when read. */
export async function takePending(id: string | undefined): Promise<PendingAuth | undefined> {
  if (!id) return undefined;
  const k = storageKey('p', id);
  const raw = await store().take(k);
  const json = raw ? open(raw, k) : null;
  return json ? (JSON.parse(json) as PendingAuth) : undefined;
}

/**
 * Runs fn while holding the refresh lock of a session. Keycloak rotates refresh tokens and revokes the session on reuse
 * (SP-01 C11), so two BFF instances must never refresh the same session concurrently.
 */
export async function withRefreshLock<T>(sessionId: string, fn: () => Promise<T>, onBusy: () => Promise<T>): Promise<T> {
  const k = storageKey('l', sessionId);
  if (!(await store().lock(k, 10_000))) return onBusy();
  try {
    return await fn();
  } finally {
    await store().del(k);
  }
}
