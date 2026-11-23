import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isIdempotencyKey, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

const QUERY = ['q', 'barcode', 'cursor', 'limit'] as const;

/** listProducts (US-041): only the contract's filters are passed on. */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  const q = new URLSearchParams();
  for (const k of QUERY) {
    const v = req.nextUrl.searchParams.get(k);
    if (v) q.set(k, v);
  }
  return relay(await callApi(session, 'GET', `/api/v1/products${q.size ? `?${q}` : ''}`));
}

/** createProduct (US-041): idempotent; the browser keeps one key per form attempt. Unknown fields are refused by the API. */
export async function POST(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const key = req.headers.get('idempotency-key');
  if (!isIdempotencyKey(key)) return problem(400, 'idempotency-key-required', 'Idempotency-Key is required');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const { name, baseUnit, brand, taxCode, taxRate, variants, confirmation } = body; // allow-list
  return relay(await callApi(session, 'POST', '/api/v1/products', { name, baseUnit, brand, taxCode, taxRate, variants, confirmation },
    { 'idempotency-key': key }));
}
