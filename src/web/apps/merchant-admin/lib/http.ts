import { NextRequest, NextResponse } from 'next/server';
import * as client from 'openid-client';
import { config, cookieNames } from './config';
import { getSession, Session } from './session';
import { oidc } from './oidc';

export function problem(status: number, code: string, title: string) {
  return NextResponse.json({ type: `https://errors.retailcloud.example/${code}`, title, status, code },
    { status, headers: { 'content-type': 'application/problem+json', 'cache-control': 'no-store' } });
}

export function currentSession(req: NextRequest): Session | undefined {
  return getSession(req.cookies.get(cookieNames().session)?.value);
}

/** CSRF for every state-changing BFF call (SEC-T-003): same-origin Origin header + double-submit token. */
export function csrfOk(req: NextRequest, session: Session): boolean {
  const origin = req.headers.get('origin');
  const expected = new URL(config.appUrl()).origin;
  return origin === expected && req.headers.get('x-csrf-token') === session.csrf;
}

/** Refreshes the access token when it expires within 60 s; Keycloak rotates the refresh token (SP-01 C10/C11). */
async function freshAccessToken(session: Session): Promise<string> {
  const now = Math.floor(Date.now() / 1000);
  if (session.accessExpiresAt - now > 60) return session.accessToken;
  const tokens = await client.refreshTokenGrant(await oidc(), session.refreshToken);
  session.accessToken = tokens.access_token;
  session.refreshToken = tokens.refresh_token ?? session.refreshToken;
  session.accessExpiresAt = now + (tokens.expires_in ?? 300);
  return session.accessToken;
}

/** Calls RetailCloud.Api with the session's token and the server-held active tenant (BR-001). */
export async function callApi(session: Session, method: string, path: string, body?: unknown): Promise<Response> {
  const headers: Record<string, string> = { authorization: `Bearer ${await freshAccessToken(session)}` };
  if (session.activeTenantId) headers['x-rc-tenant'] = session.activeTenantId;
  if (body !== undefined) headers['content-type'] = 'application/json';
  try {
    return await fetch(new URL(path, config.apiUrl()), { method, headers, body: body === undefined ? undefined : JSON.stringify(body), cache: 'no-store' });
  } catch {
    // API unreachable: answer with problem+json instead of an opaque 500 (D09 §10.1 'upstream-unavailable').
    return new Response(JSON.stringify({ type: 'https://errors.retailcloud.example/upstream-unavailable', title: 'Service temporarily unavailable', status: 502, code: 'upstream-unavailable' }),
      { status: 502, headers: { 'content-type': 'application/problem+json' } });
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
