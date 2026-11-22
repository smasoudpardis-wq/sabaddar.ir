import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isUuid, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

export async function POST(req: NextRequest, { params }: { params: Promise<{ id: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const { id } = await params;
  if (!isUuid(id)) return problem(404, 'not-found', 'Not found');
  return relay(await callApi(session, 'POST', `/api/v1/branches/${id}/pairing-codes`));
}
