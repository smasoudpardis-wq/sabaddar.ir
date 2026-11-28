import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** listIntegrationKeys (US-162, INT-001): never carries a secret. */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  return relay(await callApi(session, 'GET', '/api/v1/integrations/keys'));
}

/** createIntegrationKey (US-162): allow-listed body; the key comes back once and is shown once, never stored by the BFF. */
export async function POST(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const { name, scopes, expiresAt } = body;
  return relay(await callApi(session, 'POST', '/api/v1/integrations/keys', { name, scopes, expiresAt }));
}
