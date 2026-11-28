import { NextRequest, NextResponse } from 'next/server';
import { callApi, currentSession, lockStateOf, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (await lockStateOf(session)) {
    return NextResponse.json({ locked: true }, { headers: { 'cache-control': 'no-store' } }); // the lock screen shows nothing else
  }
  const r = await callApi(session, 'GET', '/api/v1/me');
  if (!r.ok) return relay(r);
  const me = await r.json();
  return NextResponse.json({ ...me, activeTenantId: session.activeTenantId ?? null }, { headers: { 'cache-control': 'no-store' } });
}
