import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isIdempotencyKey, isUuid, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** voidTender: takes a payment back before Finalize, with a reason (audited). */
export async function POST(req: NextRequest, { params }: { params: Promise<{ id: string; seq: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const { id, seq } = await params;
  if (!isUuid(id) || !/^[1-9][0-9]{0,3}$/.test(seq)) return problem(404, 'not-found', 'Not found');
  const key = req.headers.get('idempotency-key');
  if (!isIdempotencyKey(key)) return problem(400, 'idempotency-key-required', 'Idempotency-Key is required');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const { expectedVersion, reason } = body;
  return relay(await callApi(session, 'POST', `/api/v1/pos/sales/${id}/tenders/${seq}/void`, { expectedVersion, reason }, { 'idempotency-key': key! }));
}
