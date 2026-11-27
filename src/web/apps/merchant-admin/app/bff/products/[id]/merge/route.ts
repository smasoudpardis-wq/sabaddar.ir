import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isIdempotencyKey, isUuid, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** mergeProduct (US-044, CAT-006): idempotent; the path product (the duplicate) is merged into targetProductId. */
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
  const { targetProductId, expectedVersion, reason } = body;
  return relay(await callApi(session, 'POST', `/api/v1/products/${id}/merge`, { targetProductId, expectedVersion, reason }, { 'idempotency-key': key! }));
}
