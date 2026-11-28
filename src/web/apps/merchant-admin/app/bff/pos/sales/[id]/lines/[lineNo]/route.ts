import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isUuid, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

type Params = { params: Promise<{ id: string; lineNo: string }> };
const valid = (id: string, lineNo: string) => isUuid(id) && /^[1-9][0-9]{0,3}$/.test(lineNo);

/** updateSaleLine (US-082): quantity or unit, against the cart's version. */
export async function PATCH(req: NextRequest, { params }: Params) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const { id, lineNo } = await params;
  if (!valid(id, lineNo)) return problem(404, 'not-found', 'Not found');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const { expectedVersion, qty, unit } = body;
  return relay(await callApi(session, 'PATCH', `/api/v1/pos/sales/${id}/lines/${lineNo}`, { expectedVersion, qty, unit }));
}

/** removeSaleLine (US-082, D-44: audited). */
export async function DELETE(req: NextRequest, { params }: Params) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const { id, lineNo } = await params;
  const version = req.nextUrl.searchParams.get('expectedVersion') ?? '';
  if (!valid(id, lineNo) || !/^[0-9]{1,9}$/.test(version)) return problem(404, 'not-found', 'Not found');
  return relay(await callApi(session, 'DELETE', `/api/v1/pos/sales/${id}/lines/${lineNo}?expectedVersion=${version}`));
}
