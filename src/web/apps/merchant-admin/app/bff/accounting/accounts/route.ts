import { NextRequest } from 'next/server';
import { callApi, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** listAccounts (US-111): the chart, for picking the lines of a manual entry (US-112). */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  return relay(await callApi(session, 'GET', '/api/v1/accounting/accounts'));
}
