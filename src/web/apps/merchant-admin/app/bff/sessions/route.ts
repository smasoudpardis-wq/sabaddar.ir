import { NextRequest, NextResponse } from 'next/server';
import { currentSession, lockStateOf, problem } from '@/lib/http';
import { handleOf, sessionsOf } from '@/lib/session';
import { storageKey } from '@/lib/store';

export const dynamic = 'force-dynamic';

/** IAM-005 / US-024: the signed-in person's sessions on this application (every device), newest use first. */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (await lockStateOf(session)) return problem(423, 'session-locked', 'The register is locked; unlock with your PIN');
  if (!session.subject) return NextResponse.json({ items: [] }, { headers: { 'cache-control': 'no-store' } });
  const mine = handleOf(storageKey('s', session.id));
  const items = (await sessionsOf(session.subject)).map(({ summary }) => ({ ...summary, current: summary.handle === mine }));
  return NextResponse.json({ items }, { headers: { 'cache-control': 'no-store' } });
}
