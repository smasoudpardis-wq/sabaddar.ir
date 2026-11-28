import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isUuid, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** updateVariant (US-051, CAT-008): switch a combination of a matrix off or on. */
export async function PATCH(req: NextRequest, { params }: { params: Promise<{ id: string; variantId: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const { id, variantId } = await params;
  if (!isUuid(id) || !isUuid(variantId)) return problem(404, 'not-found', 'Not found');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const { expectedVersion, status } = body; // allow-list
  return relay(await callApi(session, 'PATCH', `/api/v1/products/${id}/variants/${variantId}`, { expectedVersion, status }));
}
