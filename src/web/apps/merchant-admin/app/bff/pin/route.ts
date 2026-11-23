import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** setMyPin (US-023): the API requires a sign-in within the last 10 minutes. */
export async function PUT(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const body = (await req.json().catch(() => null)) as { pin?: unknown } | null;
  return relay(await callApi(session, 'PUT', '/api/v1/me/pin', { pin: body?.pin }));
}
