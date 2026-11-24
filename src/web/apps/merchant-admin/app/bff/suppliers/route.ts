import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** listSuppliers (US-071, PRC-001) with what the store owes each. */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  const q = req.nextUrl.searchParams.get('q');
  return relay(await callApi(session, 'GET', `/api/v1/suppliers${q ? `?q=${encodeURIComponent(q)}` : ''}`));
}

/** createSupplier (US-071): allow-listed body. */
export async function POST(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const { name, nationalId, economicCode, phone } = body;
  return relay(await callApi(session, 'POST', '/api/v1/suppliers', { name, nationalId, economicCode, phone }));
}
