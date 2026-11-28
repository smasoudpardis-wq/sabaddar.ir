import { NextRequest } from 'next/server';
import { callApi, csrfOk, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

const CATEGORIES = ['FinancialDocuments', 'TaxInvoices', 'InventoryLedger', 'Audit', 'CustomerCrm', 'CustomerIdentity', 'Notifications', 'TechnicalLogs'];

/** setRetention (US-173, BR-087): keep a category longer than the platform default; shorter than the floor is refused by the API. */
export async function PUT(req: NextRequest, { params }: { params: Promise<{ category: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const { category } = await params;
  if (!CATEGORIES.includes(category)) return problem(404, 'not-found', 'Not found');
  const body = (await req.json().catch(() => null)) as { days?: unknown } | null;
  if (!body) return problem(400, 'validation', 'JSON body required');
  return relay(await callApi(session, 'PUT', `/api/v1/settings/retention/${category}`, { days: body.days }));
}

/** clearRetention (US-173): back to the platform default. */
export async function DELETE(req: NextRequest, { params }: { params: Promise<{ category: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  if (!csrfOk(req, session)) return problem(403, 'csrf', 'CSRF check failed');
  const { category } = await params;
  if (!CATEGORIES.includes(category)) return problem(404, 'not-found', 'Not found');
  return relay(await callApi(session, 'DELETE', `/api/v1/settings/retention/${category}`));
}
