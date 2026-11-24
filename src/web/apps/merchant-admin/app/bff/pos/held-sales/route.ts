import { NextRequest } from 'next/server';
import { callApi, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** listHeldSales (US-083): the held carts of this device's branch. */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  return relay(await callApi(session, 'GET', '/api/v1/pos/held-sales'));
}
