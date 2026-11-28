import { NextRequest } from 'next/server';
import { callApi, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

/** listUnreconciledPayments (US-105, AB-14): card slips no settlement file has matched, per cashier. */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  const q = new URLSearchParams();
  for (const name of ['from', 'to']) {
    const v = req.nextUrl.searchParams.get(name);
    if (v === null || v === '') continue;
    if (!DATE.test(v)) return problem(400, 'validation', `${name} must be yyyy-MM-dd`);
    q.set(name, v);
  }
  const qs = q.toString();
  return relay(await callApi(session, 'GET', `/api/v1/settlements/unreconciled-payments${qs ? `?${qs}` : ''}`));
}
