import { NextRequest } from 'next/server';
import { callApi, currentSession, isUuid, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** listStockBalances (US-061): one variant in one warehouse (cost and value only with reports.view, TM-014). */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  const variantId = req.nextUrl.searchParams.get('variantId');
  const warehouseId = req.nextUrl.searchParams.get('warehouseId');
  if (!isUuid(variantId ?? '') || (warehouseId && !isUuid(warehouseId))) return problem(400, 'validation', 'variantId (and warehouseId) must be UUIDs');
  return relay(await callApi(session, 'GET', `/api/v1/inventory/balances?variantId=${variantId}${warehouseId ? `&warehouseId=${warehouseId}` : ''}`));
}
