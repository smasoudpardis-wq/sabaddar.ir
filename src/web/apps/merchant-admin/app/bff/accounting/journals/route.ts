import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isIdempotencyKey, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** listJournals (US-111): newest first, keyset pages. */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  const q = new URLSearchParams();
  for (const k of ['cursor', 'limit', 'sourceType']) {
    const v = req.nextUrl.searchParams.get(k);
    if (v) q.set(k, v);
  }
  return relay(await callApi(session, 'GET', `/api/v1/accounting/journals${q.size ? `?${q}` : ''}`));
}

/** createJournalDraft (US-112, ACC-006): idempotent; allow-listed body. */
export async function POST(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const key = req.headers.get('idempotency-key');
  if (!isIdempotencyKey(key)) return problem(400, 'idempotency-key-required', 'Idempotency-Key is required');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const { effectiveAt, description, lines } = body;
  return relay(await callApi(session, 'POST', '/api/v1/accounting/journals', { effectiveAt, description, lines }, { 'idempotency-key': key! }));
}
