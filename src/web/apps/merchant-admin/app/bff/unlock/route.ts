import { NextRequest, NextResponse } from 'next/server';
import * as client from 'openid-client';
import { cookieNames } from '@/lib/config';
import { callApi, csrfOk, currentSession, problem, relay } from '@/lib/http';
import { oidc } from '@/lib/oidc';
import { destroySession, saveSession } from '@/lib/session';

export const dynamic = 'force-dynamic';

/**
 * Unlock with the PIN (IAM-004): the API checks it (registered terminal only, 5 failures lock it). When the PIN gets
 * locked the BFF session ends too, so a full sign-in is required (AUTH-08).
 */
export async function POST(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const body = (await req.json().catch(() => null)) as { pin?: unknown } | null;
  const r = await callApi(session, 'POST', '/api/v1/me/pin/verify', { pin: body?.pin }, undefined, { allowLocked: true });
  if (r.status === 204) {
    session.lockedAt = undefined;
    session.lastActive = Math.floor(Date.now() / 1000);
    await saveSession(session);
    return new NextResponse(null, { status: 204 });
  }
  const data = await r.clone().json().catch(() => ({}));
  if (data.code === 'pin-locked') {
    await client.tokenRevocation(await oidc(), session.refreshToken).catch(() => undefined);
    await destroySession(session.id);
    const res = problem(401, 'pin-locked', 'PIN locked; sign in again');
    res.cookies.delete(cookieNames().session);
    res.cookies.delete(cookieNames().csrf);
    return res;
  }
  return relay(r);
}
