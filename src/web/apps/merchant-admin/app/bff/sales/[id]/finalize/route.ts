import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isIdempotencyKey, isUuid, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** finalizeSale (US-091, SAL-007): idempotent; after a lost answer the browser retries with the same key (UC-02 6a). */
export async function POST(req: NextRequest, { params }: { params: Promise<{ id: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const { id } = await params;
  if (!isUuid(id)) return problem(404, 'not-found', 'Not found');
  const key = req.headers.get('idempotency-key');
  if (!isIdempotencyKey(key)) return problem(400, 'idempotency-key-required', 'Idempotency-Key is required');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const { expectedVersion, expectedTotal } = body;
  return relay(await callApi(session, 'POST', `/api/v1/sales/${id}/finalize`, { expectedVersion, expectedTotal }, { 'idempotency-key': key! }));
}
