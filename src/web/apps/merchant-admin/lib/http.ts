import { cookies, headers as requestHeaders } from 'next/headers';
import { NextRequest, NextResponse } from 'next/server';
import * as client from 'openid-client';
import { config, cookieNames } from './config';
import { destroySession, getSession, saveSession, Session, withRefreshLock } from './session';
import { oidc } from './oidc';

export function problem(status: number, code: string, title: string) {
  return NextResponse.json({ type: `https://errors.retailcloud.example/${code}`, title, status, code },
    { status, headers: { 'content-type': 'application/problem+json', 'cache-control': 'no-store' } });
}

const problemResponse = (status: number, code: string, title: string) =>
  new Response(JSON.stringify({ type: `https://errors.retailcloud.example/${code}`, title, status, code }),
    { status, headers: { 'content-type': 'application/problem+json' } });

export function currentSession(req: NextRequest): Promise<Session | undefined> {
  return getSession(req.cookies.get(cookieNames().session)?.value);
}

/** CSRF for every state-changing BFF call (SEC-T-003): same-origin Origin header + double-submit token. */
export function csrfOk(req: NextRequest, session: Session): boolean {
  const origin = req.headers.get('origin');
  const expected = new URL(config.appUrl()).origin;
  return origin === expected && req.headers.get('x-csrf-token') === session.csrf;
}

class SessionExpired extends Error {}

const needsRefresh = (s: Session) => s.accessExpiresAt - Math.floor(Date.now() / 1000) <= 60;

/** Refreshes the access token when it expires within 60 s, under a per-session lock (refresh tokens rotate). */
async function freshAccessToken(session: Session): Promise<string> {
  if (!needsRefresh(session)) return session.accessToken;
  return withRefreshLock(session.id, async () => {
    const latest = await getSession(session.id); // another instance may have refreshed just before us
    if (!latest) throw new SessionExpired();
    if (!needsRefresh(latest)) return Object.assign(session, latest).accessToken;
    let tokens: client.TokenEndpointResponse;
    try {
      tokens = await client.refreshTokenGrant(await oidc(), latest.refreshToken);
    } catch {
      await destroySession(session.id); // revoked or expired at the IdP: the BFF session ends too
      throw new SessionExpired();
    }
    Object.assign(session, latest, {
      accessToken: tokens.access_token,
      refreshToken: tokens.refresh_token ?? latest.refreshToken,
      accessExpiresAt: Math.floor(Date.now() / 1000) + (tokens.expires_in ?? 300),
    });
    await saveSession(session);
    return session.accessToken;
  }, async () => {
    for (let i = 0; i < 50; i++) {
      await new Promise((r) => setTimeout(r, 100));
      const s = await getSession(session.id);
      if (!s) throw new SessionExpired();
      if (!needsRefresh(s)) return Object.assign(session, s).accessToken;
    }
    throw new SessionExpired();
  });
}

/** Calls RetailCloud.Api with the session's token and the server-held active tenant (BR-001). */
/** IAM-005: a registered terminal locks after 5 idle minutes; then only the PIN (or a new sign-in) opens it. */
export const TERMINAL_IDLE_LOCK_SECONDS = 5 * 60;

export async function isOnTerminal(): Promise<boolean> {
  return !!(await cookies()).get(cookieNames().device)?.value;
}

/** Applies the idle auto-lock; returns true when the session is (now) locked. */
export async function lockStateOf(session: Session): Promise<boolean> {
  if (session.lockedAt) return true;
  const nowS = Math.floor(Date.now() / 1000);
  if ((await isOnTerminal()) && session.lastActive && nowS - session.lastActive > TERMINAL_IDLE_LOCK_SECONDS) {
    session.lockedAt = nowS;
    await saveSession(session);
    return true;
  }
  return false;
}

export async function callApi(session: Session, method: string, path: string, body?: unknown, extraHeaders?: Record<string, string>,
  options: { allowLocked?: boolean } = {}): Promise<Response> {
  if (!options.allowLocked && (await lockStateOf(session))) {
    return problemResponse(423, 'session-locked', 'The register is locked; unlock with your PIN');
  }
  const nowS = Math.floor(Date.now() / 1000);
  if (!options.allowLocked && (!session.lastActive || nowS - session.lastActive > 30)) {
    session.lastActive = nowS;
    await saveSession(session);
  }
  let token: string;
  try {
    token = await freshAccessToken(session);
  } catch (e) {
    if (e instanceof SessionExpired) return problemResponse(401, 'invalid-token', 'Session expired');
    throw e;
  }
  const headers: Record<string, string> = { ...extraHeaders, authorization: `Bearer ${token}` };
  if (session.activeTenantId) headers['x-rc-tenant'] = session.activeTenantId;
  // ABAC-04: a paired POS device carries its secret in an HttpOnly cookie that outlives user sessions (US-013).
  const device = (await cookies()).get(cookieNames().device)?.value;
  if (device) headers['x-rc-device'] = device;
  if (body !== undefined) headers['content-type'] = 'application/json';
  // AUD-001: the API records the browser's address. The BFF is the API's trusted proxy (Network:TrustedProxies), so it passes on
  // the address its own edge reported, or the peer it saw; a client-sent header is only as trustworthy as that edge.
  const incoming = await requestHeaders();
  const clientIp = incoming.get('x-forwarded-for')?.split(',')[0]?.trim() || incoming.get('x-real-ip') || undefined;
  if (clientIp) headers['x-forwarded-for'] = clientIp;
  try {
    return await fetch(new URL(path, config.apiUrl()), { method, headers, body: body === undefined ? undefined : JSON.stringify(body), cache: 'no-store' });
  } catch {
    // API unreachable: answer with problem+json instead of an opaque 500 (D09 §10.1 'upstream-unavailable').
    return problemResponse(502, 'upstream-unavailable', 'Service temporarily unavailable');
  }
}

/**
 * CUS-009: a call without a user (the public receipt link). No token, no tenant, no device: only the client address, so the
 * API can rate-limit and audit the view.
 */
export async function callPublicApi(path: string): Promise<Response> {
  const incoming = await requestHeaders();
  const clientIp = incoming.get('x-forwarded-for')?.split(',')[0]?.trim() || incoming.get('x-real-ip') || undefined;
  try {
    return await fetch(new URL(path, config.apiUrl()), { headers: clientIp ? { 'x-forwarded-for': clientIp } : {}, cache: 'no-store' });
  } catch {
    return problemResponse(502, 'upstream-unavailable', 'Service temporarily unavailable');
  }
}

/** Relays an API response to the browser unchanged in status and body (problem+json included). */
export async function relay(r: Response): Promise<NextResponse> {
  const text = r.status === 204 ? null : await r.text();
  return new NextResponse(text, {
    status: r.status,
    headers: { 'content-type': r.headers.get('content-type') ?? 'application/json', 'cache-control': 'no-store' },
  });
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/** Idempotency-Key format of D07 (16–64 of A–Z a–z 0–9 _ -). */
export const isIdempotencyKey = (v: unknown): v is string => typeof v === 'string' && /^[A-Za-z0-9_-]{16,64}$/.test(v);

export const isUuid = (v: unknown): v is string => typeof v === 'string' && UUID.test(v);

/** Only same-app absolute paths (no scheme, no '//' host) — prevents open redirects after login. */
export const safeReturnTo = (v: string | null) => (v && v.startsWith('/') && !v.startsWith('//') && !v.includes('\\') ? v : '/');
