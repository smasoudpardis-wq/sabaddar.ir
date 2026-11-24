import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** createSale (US-082, US-084): one cart per clientDraftId (DI-010); an offline cart arrives with its lines (SAL-013). */
export async function POST(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const { registerId, clientDraftId, lines } = body;
  return relay(await callApi(session, 'POST', '/api/v1/pos/sales', { registerId, clientDraftId, lines }));
}
