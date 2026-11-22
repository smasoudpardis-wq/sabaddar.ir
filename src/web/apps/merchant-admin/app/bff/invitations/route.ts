import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** inviteUser (US-025). Allow-listed fields only; the API checks permission, scope and anti-escalation. */
export async function POST(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const { displayName, email, mobile, roleAssignments } = body;
  return relay(await callApi(session, 'POST', '/api/v1/users/invitations', { displayName, email, mobile, roleAssignments }));
}
