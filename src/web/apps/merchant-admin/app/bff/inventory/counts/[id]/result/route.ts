import { NextRequest } from 'next/server';
import { callApi, currentSession, isUuid, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** getCountResult (US-065, AT-280). */
export async function GET(req: NextRequest, { params }: { params: Promise<{ id: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  const { id } = await params;
  if (!isUuid(id)) return problem(404, 'not-found', 'Not found');
  return relay(await callApi(session, 'GET', `/api/v1/inventory/counts/${id}/result`));
}
