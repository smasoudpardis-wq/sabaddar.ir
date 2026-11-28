import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

const CODES = ['receipt', 'due-reminder'];

/** updateNotificationTemplate (US-152): only the signed name and the signature are passed on. */
export async function PUT(req: NextRequest, { params }: { params: Promise<{ code: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const { code } = await params;
  if (!CODES.includes(code)) return problem(404, 'not-found', 'Not found');
  const body = (await req.json().catch(() => null)) as { expectedVersion?: unknown; storeName?: unknown; signature?: unknown } | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  return relay(await callApi(session, 'PUT', `/api/v1/settings/notification-templates/${code}`, {
    expectedVersion: body.expectedVersion, storeName: body.storeName, signature: body.signature,
  }));
}
