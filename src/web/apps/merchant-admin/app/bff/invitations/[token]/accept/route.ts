import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

export async function POST(req: NextRequest, { params }: { params: Promise<{ token: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const { token } = await params;
  if (!/^[A-Za-z0-9_-]{32,64}$/.test(token)) return problem(404, 'not-found', 'Not found');
  return relay(await callApi(session, 'POST', `/api/v1/invitations/${token}/accept`));
}
