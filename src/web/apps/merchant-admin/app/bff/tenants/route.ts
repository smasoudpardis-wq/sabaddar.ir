import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, isIdempotencyKey, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/**
 * registerTenant (UC-10). The browser generates the Idempotency-Key once per wizard attempt and keeps it across the
 * MFA step-up redirect, so a retry or double click lands on the same tenant (the API derives the id from the key).
 */
export async function POST(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const key = req.headers.get('idempotency-key');
  if (!isIdempotencyKey(key)) return problem(400, 'idempotency-key-required', 'Idempotency-Key is required');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  const { legalName, industryCode, firstBranchName, timeZone, pricingMode } = body; // allow-list (no mass assignment)
  return relay(await callApi(session, 'POST', '/api/v1/tenants', { legalName, industryCode, firstBranchName, timeZone, pricingMode },
    { 'idempotency-key': key }));
}
