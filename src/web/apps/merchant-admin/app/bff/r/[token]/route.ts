import { NextRequest } from 'next/server';
import { callPublicApi, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

/** getPublicReceipt (US-092, CUS-009): anyone holding the link sees the receipt; no session is needed or used. */
export async function GET(_req: NextRequest, { params }: { params: Promise<{ token: string }> }) {
  const { token } = await params;
  if (!/^[A-Za-z0-9_-]{20,30}$/.test(token)) return problem(404, 'not-found', 'Not found');
  return relay(await callPublicApi(`/api/v1/r/${token}`));
}
