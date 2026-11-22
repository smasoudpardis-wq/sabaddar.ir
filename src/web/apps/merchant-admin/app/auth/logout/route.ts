import { NextRequest, NextResponse } from 'next/server';
import * as client from 'openid-client';
import { cookieNames } from '@/lib/config';
import { oidc } from '@/lib/oidc';
import { destroySession } from '@/lib/session';
import { csrfOk, currentSession, problem } from '@/lib/http';

export const dynamic = 'force-dynamic';

export async function POST(req: NextRequest) {
  const session = currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  await client.tokenRevocation(await oidc(), session.refreshToken).catch(() => undefined);
  destroySession(session.id);
  const res = new NextResponse(null, { status: 204 });
  res.cookies.delete(cookieNames().session);
  res.cookies.delete(cookieNames().csrf);
  return res;
}
