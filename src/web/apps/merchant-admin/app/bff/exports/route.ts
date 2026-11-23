import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** createExport (US-016): allow-listed body; the API checks data.export and a recent second factor. */
export async function POST(req: NextRequest) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const body = (await req.json().catch(() => null)) as Record<string, unknown> | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  return relay(await callApi(session, 'POST', '/api/v1/exports', { categories: body.categories, from: body.from, to: body.to }));
}
