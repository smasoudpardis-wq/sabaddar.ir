import { randomBytes } from 'node:crypto';
import { config } from './config';

// Server-side session store (D04 §6, AUTH-07). Tokens never leave the server; the browser holds only an opaque id.
// The in-memory store is for development/tests; production uses Redis/Valkey with the same interface (US-021 follow-up).
export interface Session {
  id: string;
  accessToken: string;
  refreshToken: string;
  accessExpiresAt: number; // epoch seconds
  idToken?: string;
  activeTenantId?: string;
  csrf: string;
  lastSeen: number;
}

export interface PendingAuth {
  state: string;
  nonce: string;
  codeVerifier: string;
  createdAt: number;
}

const g = globalThis as unknown as { __rcSessions?: Map<string, Session>; __rcPending?: Map<string, PendingAuth> };
const sessions = (g.__rcSessions ??= new Map());
const pending = (g.__rcPending ??= new Map());

export const randomId = (bytes = 32) => randomBytes(bytes).toString('base64url');

export function createSession(s: Omit<Session, 'id' | 'csrf' | 'lastSeen'>): Session {
  const session: Session = { ...s, id: randomId(), csrf: randomId(24), lastSeen: Math.floor(Date.now() / 1000) };
  sessions.set(session.id, session);
  return session;
}

export function getSession(id: string | undefined): Session | undefined {
  if (!id) return undefined;
  const s = sessions.get(id);
  if (!s) return undefined;
  const now = Math.floor(Date.now() / 1000);
  if (now - s.lastSeen > config.idleTimeoutSeconds) {
    sessions.delete(id);
    return undefined;
  }
  s.lastSeen = now;
  return s;
}

export const destroySession = (id: string) => sessions.delete(id);

export function savePending(p: PendingAuth): string {
  const id = randomId();
  pending.set(id, p);
  return id;
}

export function takePending(id: string | undefined): PendingAuth | undefined {
  if (!id) return undefined;
  const p = pending.get(id);
  pending.delete(id); // single use
  if (!p || Date.now() - p.createdAt > 10 * 60 * 1000) return undefined;
  return p;
}
