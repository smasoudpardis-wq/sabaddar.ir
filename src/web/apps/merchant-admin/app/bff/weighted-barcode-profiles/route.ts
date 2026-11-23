import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

const FIELDS = ['id', 'prefix', 'itemCodeLength', 'valueType', 'valueLength', 'decimals', 'hasInnerCheckDigit'] as const;

/** getWeightedBarcodeProfiles (US-045, CAT-007). */
export async function GET(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  return relay(await callApi(session, 'GET', '/api/v1/weighted-barcode-profiles'));
}

/** setWeightedBarcodeProfiles: the whole set, each profile allow-listed field by field (no mass assignment). */
export async function PUT(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const body = (await req.json().catch(() => null)) as { expectedVersion?: unknown; profiles?: unknown } | null;
  if (!body || !Array.isArray(body.profiles)) return problem(400, 'validation', 'JSON body with profiles required');
  const profiles = body.profiles.map((p: Record<string, unknown> | null) =>
    Object.fromEntries(FIELDS.filter((f) => p && p[f] !== undefined).map((f) => [f, p![f]])));
  return relay(await callApi(session, 'PUT', '/api/v1/weighted-barcode-profiles', { expectedVersion: body.expectedVersion, profiles }));
}
