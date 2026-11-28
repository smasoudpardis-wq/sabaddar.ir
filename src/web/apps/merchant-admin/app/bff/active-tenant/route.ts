import { NextRequest, NextResponse } from 'next/server';
import { callApi, csrfOk, currentSession, isUuid, problem, relay } from '@/lib/http';
import { saveSession } from '@/lib/session';

export const dynamic = 'force-dynamic';

/** selectActiveTenant: the API confirms membership; only then is the tenant stored in the server-side session (BR-001). */
export async function POST(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const body = (await req.json().catch(() => null)) as { tenantId?: unknown } | null;
  if (!isUuid(body?.tenantId)) return problem(400, 'validation', 'tenantId must be a UUID');
  const r = await callApi(session, 'POST', '/api/v1/me/active-tenant', { tenantId: body.tenantId });
  if (r.status === 204) {
    session.activeTenantId = body.tenantId;
    await saveSession(session);
    return new NextResponse(null, { status: 204 });
  }
  return relay(r);
}
