import { NextRequest } from 'next/server';
import { callApi, currentSession, problem, relay } from '@/lib/http';
import { reportQuery } from '../query';

export const dynamic = 'force-dynamic';

/** getTrialBalance (US-114, ACC-007). */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  const q = reportQuery(req, ['from', 'to', 'includeEmpty']);
  if (q === null) return problem(400, 'validation', 'Invalid report query');
  return relay(await callApi(session, 'GET', `/api/v1/accounting/reports/trial-balance${q}`));
}
