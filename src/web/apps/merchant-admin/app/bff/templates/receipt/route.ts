import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** getReceiptTemplate (US-172, BR-086): the industry's version plus the store's own parts. */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  return relay(await callApi(session, 'GET', '/api/v1/templates/receipt'));
}

/** updateReceiptTemplate (US-172, DI-085): only the footer and signature are passed on; nothing else can be overridden. */
export async function PUT(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const body = (await req.json().catch(() => null)) as { expectedVersion?: unknown; overrides?: { footerText?: unknown; signature?: unknown } } | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  return relay(await callApi(session, 'PUT', '/api/v1/templates/receipt', {
    expectedVersion: body.expectedVersion,
    overrides: { footerText: body.overrides?.footerText, signature: body.overrides?.signature },
  }));
}
