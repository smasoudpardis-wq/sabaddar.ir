import { NextRequest } from 'next/server';
import { callApi, currentSession, isUuid, problem, relay } from '@/lib/http';
import { reportQuery } from '../../query';

export const dynamic = 'force-dynamic';

/** getLedger (US-114, ACC-007): an account, a group of accounts, or one detail on an account. */
export async function GET(req: NextRequest, { params }: { params: Promise<{ id: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  const { id } = await params;
  if (!isUuid(id)) return problem(404, 'not-found', 'Not found');
  const q = reportQuery(req, ['from', 'to', 'detailId', 'cursor', 'limit']);
  if (q === null) return problem(400, 'validation', 'Invalid report query');
  return relay(await callApi(session, 'GET', `/api/v1/accounting/reports/ledger/${id}${q}`));
}
