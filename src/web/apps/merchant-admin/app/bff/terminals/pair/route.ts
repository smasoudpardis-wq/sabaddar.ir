import { NextRequest, NextResponse } from 'next/server';
import { config, cookieNames } from '@/lib/config';
import { callApi, csrfOk, currentSession, problem } from '@/lib/http';

export const dynamic = 'force-dynamic';

/**
 * pairTerminal (US-013): the device secret goes into an HttpOnly cookie (1 year) and is never returned to page
 * scripts; the BFF forwards it as X-RC-Device so the API recognises this browser as the registered terminal.
 */
export async function POST(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const body = (await req.json().catch(() => null)) as { pairingCode?: unknown; deviceName?: unknown } | null;
  const r = await callApi(session, 'POST', '/api/v1/terminals/pair', { pairingCode: body?.pairingCode, deviceName: body?.deviceName });
  const data = await r.json().catch(() => ({}));
  if (r.status !== 201) {
    return NextResponse.json(data, { status: r.status, headers: { 'content-type': 'application/problem+json', 'cache-control': 'no-store' } });
  }
  const { deviceSecret, ...terminal } = data as { deviceSecret: string } & Record<string, unknown>;
  const res = NextResponse.json(terminal, { status: 201, headers: { 'cache-control': 'no-store' } });
  res.cookies.set(cookieNames().device, deviceSecret, {
    httpOnly: true, secure: config.secureCookies(), sameSite: 'lax', path: '/', maxAge: 365 * 24 * 3600,
  });
  return res;
}
