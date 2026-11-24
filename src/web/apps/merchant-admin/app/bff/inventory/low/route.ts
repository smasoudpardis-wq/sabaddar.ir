import { NextRequest } from 'next/server';
import { callApi, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** listStockBalances?belowReorderPoint=true (US-066, INV-010): what is low, named. */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  return relay(await callApi(session, 'GET', '/api/v1/inventory/balances?belowReorderPoint=true'));
}
