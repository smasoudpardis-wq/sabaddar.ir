import { randomBytes } from 'node:crypto';
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

export async function saveSession(s: Session): Promise<void> {
  const k = storageKey('s', s.id);
  await store().set(k, seal(JSON.stringify(s), k), config.idleTimeoutSeconds);
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
