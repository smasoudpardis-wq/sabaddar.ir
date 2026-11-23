import { NextRequest, NextResponse } from 'next/server';
import { csrfOk, currentSession, problem } from '@/lib/http';
import { saveSession } from '@/lib/session';

export const dynamic = 'force-dynamic';

/** Lock the register now (US-023). */
export async function POST(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  session.lockedAt = Math.floor(Date.now() / 1000);
  await saveSession(session);
  return new NextResponse(null, { status: 204 });
}
