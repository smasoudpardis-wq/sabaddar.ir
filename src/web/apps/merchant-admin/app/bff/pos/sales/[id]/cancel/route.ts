import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isUuid, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** cancelSale (US-083, SAL-011): allow-listed body. */
export async function POST(req: NextRequest, { params }: { params: Promise<{ id: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const { id } = await params;
  if (!isUuid(id)) return problem(404, 'not-found', 'Not found');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const { expectedVersion, reason } = body;
  return relay(await callApi(session, 'POST', `/api/v1/pos/sales/${id}/cancel`, { expectedVersion, reason }));
}
