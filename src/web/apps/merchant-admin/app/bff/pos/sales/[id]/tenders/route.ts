import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isIdempotencyKey, isUuid, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** recordTender (US-101, PAY-001/002): idempotent; a repeated card reference is retried with a manager's approvalId. */
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
  const { method, amount, tendered, referenceNo, terminalNo, cardLast4, approvalId } = body; // allow-list
  return relay(await callApi(session, 'POST', `/api/v1/pos/sales/${id}/tenders`,
    { method, amount, tendered, referenceNo, terminalNo, cardLast4, approvalId }, { 'idempotency-key': key! }));
}
