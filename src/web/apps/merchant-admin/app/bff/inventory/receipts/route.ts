import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isIdempotencyKey, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** listGoodsReceipts (US-071). */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  return relay(await callApi(session, 'GET', '/api/v1/inventory/receipts'));
}

/** postGoodsReceipt (US-071, PRC-002, UC-05): idempotent; warnings are confirmed by sending the same receipt with confirmedWarnings. */
export async function POST(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const key = req.headers.get('idempotency-key');
  if (!isIdempotencyKey(key)) return problem(400, 'idempotency-key-required', 'Idempotency-Key is required');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const { supplierId, warehouseId, lines, supplierInvoiceNo, confirmedWarnings } = body; // allow-list
  return relay(await callApi(session, 'POST', '/api/v1/inventory/receipts', { supplierId, warehouseId, lines, supplierInvoiceNo, confirmedWarnings },
    { 'idempotency-key': key! }));
}
