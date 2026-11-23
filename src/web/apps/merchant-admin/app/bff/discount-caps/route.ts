import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** getDiscountCaps (US-053, BR-021). */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  return relay(await callApi(session, 'GET', '/api/v1/discount-caps'));
}

/** setDiscountCaps: only the four role names are passed on. */
export async function PUT(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const body = (await req.json().catch(() => null)) as { caps?: Record<string, unknown> } | null;
  if (!body?.caps || typeof body.caps !== 'object') return problem(400, 'validation', 'caps required');
  const caps = Object.fromEntries(['Cashier', 'BranchManager', 'TenantAdmin', 'TenantOwner']
    .filter((r) => body.caps![r] !== undefined).map((r) => [r, body.caps![r]]));
  return relay(await callApi(session, 'PUT', '/api/v1/discount-caps', { caps }));
}
