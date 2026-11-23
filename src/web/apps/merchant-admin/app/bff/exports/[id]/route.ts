import { NextRequest } from 'next/server';
import { callApi, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** getExport (US-016): status, and a fresh 5-minute download link once the file is ready. */
export async function GET(req: NextRequest, { params }: { params: Promise<{ id: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  const { id } = await params;
  if (!UUID.test(id)) return problem(404, 'not-found', 'Not found');
  return relay(await callApi(session, 'GET', `/api/v1/exports/${id}`));
}
