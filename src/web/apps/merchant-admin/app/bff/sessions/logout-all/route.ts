import { NextRequest, NextResponse } from 'next/server';
import { cookieNames } from '@/lib/config';
import { csrfOk, currentSession, problem } from '@/lib/http';
import { revokeAtIdp } from '@/lib/oidc';
import { endSession, sessionsOf } from '@/lib/session';

export const dynamic = 'force-dynamic';

/** IAM-005 «خروج از همه دستگاه‌ها»: every session of mine ends, this one included, at the BFF and at Keycloak. */
export async function POST(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const all = session.subject ? await sessionsOf(session.subject) : [];
  for (const { session: s } of all) await endSession(s, revokeAtIdp);
  if (!all.some((x) => x.session.id === session.id)) await endSession(session, revokeAtIdp);
  const res = new NextResponse(JSON.stringify({ ended: Math.max(all.length, 1) }), { status: 200, headers: { 'content-type': 'application/json' } });
  res.cookies.delete(cookieNames().session);
  res.cookies.delete(cookieNames().csrf);
  return res;
}
