import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isIdempotencyKey, isUuid, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** confirmCount (US-065, SoD-03, BR-033): idempotent; after a manager approves, retried with X-Approval-Id. */
export async function POST(req: NextRequest, { params }: { params: Promise<{ id: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const { id } = await params;
  if (!isUuid(id)) return problem(404, 'not-found', 'Not found');
  const key = req.headers.get('idempotency-key');
  if (!isIdempotencyKey(key)) return problem(400, 'idempotency-key-required', 'Idempotency-Key is required');
  const approval = req.headers.get('x-approval-id');
  if (approval && !isUuid(approval)) return problem(400, 'validation', 'X-Approval-Id must be a UUID');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const headers: Record<string, string> = { 'idempotency-key': key! };
  if (approval) headers['x-approval-id'] = approval;
  return relay(await callApi(session, 'POST', `/api/v1/inventory/counts/${id}/confirm`, { expectedVersion: body.expectedVersion }, headers));
}
