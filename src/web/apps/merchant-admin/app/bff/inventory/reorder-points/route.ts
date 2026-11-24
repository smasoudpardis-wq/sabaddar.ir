import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** setReorderPoint (US-066): allow-listed body; reorderPoint null removes it. */
export async function PUT(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const { warehouseId, variantId, reorderPoint } = body;
  return relay(await callApi(session, 'PUT', '/api/v1/inventory/reorder-points', { warehouseId, variantId, reorderPoint }));
}
