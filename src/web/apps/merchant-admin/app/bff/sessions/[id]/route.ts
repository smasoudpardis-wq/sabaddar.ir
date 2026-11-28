import { NextRequest, NextResponse } from 'next/server';
import { csrfOk, currentSession, lockStateOf, problem } from '@/lib/http';
import { revokeAtIdp } from '@/lib/oidc';
import { endSession, handleOf, sessionsOf } from '@/lib/session';
import { storageKey } from '@/lib/store';

export const dynamic = 'force-dynamic';

/** US-024: sign one of my other devices out. Only my own sessions can be found; this device signs out with /auth/logout. */
export async function DELETE(req: NextRequest, { params }: { params: Promise<{ id: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  if (await lockStateOf(session)) return problem(423, 'session-locked', 'The register is locked; unlock with your PIN');
  const { id } = await params;
  if (!/^[A-Za-z0-9_-]{22}$/.test(id) || !session.subject) return problem(404, 'not-found', 'Not found');
  if (id === handleOf(storageKey('s', session.id))) return problem(409, 'current-session', 'Use sign-out for this device');
  const target = (await sessionsOf(session.subject)).find((x) => x.summary.handle === id);
  if (!target) return problem(404, 'not-found', 'Not found');
  await endSession(target.session, revokeAtIdp);
  return new NextResponse(null, { status: 204 });
}
