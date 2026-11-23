import { NextRequest } from 'next/server';
import { callApi, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

// US-032: only the filters listAudit defines are passed on (no free-form query smuggling to the API).
const ALLOWED = ['from', 'to', 'action', 'actorId', 'resourceType', 'resourceId', 'cursor', 'limit'] as const;

export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  const q = new URLSearchParams();
  for (const k of ALLOWED) {
    const v = req.nextUrl.searchParams.get(k);
    if (v) q.set(k, v);
  }
  return relay(await callApi(session, 'GET', `/api/v1/audit${q.size ? `?${q}` : ''}`));
}
