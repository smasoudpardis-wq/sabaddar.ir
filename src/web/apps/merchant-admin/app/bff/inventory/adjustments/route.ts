import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isIdempotencyKey, isUuid, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** listAdjustments (US-063, AB-05): the adjustments and their value. */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  return relay(await callApi(session, 'GET', '/api/v1/inventory/adjustments'));
}

/** postAdjustment (US-063, INV-002, BR-033): idempotent; above the threshold the browser retries with X-Approval-Id. */
export async function POST(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const key = req.headers.get('idempotency-key');
  if (!isIdempotencyKey(key)) return problem(400, 'idempotency-key-required', 'Idempotency-Key is required');
  const approval = req.headers.get('x-approval-id');
  if (approval && !isUuid(approval)) return problem(400, 'validation', 'X-Approval-Id must be a UUID');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const { warehouseId, reason, lines, note } = body; // allow-list
  const headers: Record<string, string> = { 'idempotency-key': key! };
  if (approval) headers['x-approval-id'] = approval;
  return relay(await callApi(session, 'POST', '/api/v1/inventory/adjustments', { warehouseId, reason, lines, note }, headers));
}
