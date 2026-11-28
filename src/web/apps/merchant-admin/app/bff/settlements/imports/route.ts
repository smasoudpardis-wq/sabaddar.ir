import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isIdempotencyKey, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** listSettlementImports (US-105, PAY-002.2): the 50 most recent bank settlement files. */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  return relay(await callApi(session, 'GET', '/api/v1/settlements/imports'));
}

/** importSettlement (US-105): idempotent; the CSV travels as text with its file name, nothing else. */
export async function POST(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const key = req.headers.get('idempotency-key');
  if (!isIdempotencyKey(key)) return problem(400, 'idempotency-key-required', 'Idempotency-Key is required');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const { fileName, content } = body; // allow-list
  return relay(await callApi(session, 'POST', '/api/v1/settlements/imports', { fileName, content }, { 'idempotency-key': key! }));
}
