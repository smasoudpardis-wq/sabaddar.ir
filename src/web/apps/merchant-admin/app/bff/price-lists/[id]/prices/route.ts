import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isIdempotencyKey, isUuid, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** listPriceVersions (US-052): one variant's price history, or the list's latest changes. */
export async function GET(req: NextRequest, { params }: { params: Promise<{ id: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  const { id } = await params;
  const variantId = req.nextUrl.searchParams.get('variantId');
  if (!isUuid(id) || (variantId && !isUuid(variantId))) return problem(404, 'not-found', 'Not found');
  return relay(await callApi(session, 'GET', `/api/v1/price-lists/${id}/prices${variantId ? `?variantId=${variantId}` : ''}`));
}

/** setPrices (US-052, BR-041): idempotent; after a manager approves, the browser retries with X-Approval-Id. */
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
  const { items, validFrom, reason } = body; // allow-list
  const headers: Record<string, string> = { 'idempotency-key': key! };
  if (approval) headers['x-approval-id'] = approval;
  return relay(await callApi(session, 'POST', `/api/v1/price-lists/${id}/prices`, { items, validFrom, reason }, headers));
}
