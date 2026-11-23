import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isUuid, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** decideApproval (US-026): allow-listed body; the API enforces approver ≠ requester, scope and fresh MFA. */
export async function POST(req: NextRequest, { params }: { params: Promise<{ id: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const { id } = await params;
  if (!isUuid(id)) return problem(404, 'not-found', 'Not found');
  const body = (await req.json().catch(() => null)) as { decision?: unknown; comment?: unknown } | null;
  return relay(await callApi(session, 'POST', `/api/v1/approvals/${id}/decision`, { decision: body?.decision, comment: body?.comment }));
}
