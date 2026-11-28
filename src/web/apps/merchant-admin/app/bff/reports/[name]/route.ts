import { NextRequest, NextResponse } from 'next/server';
import { callApi, currentSession, problem, relay } from '@/lib/http';

export const dynamic = 'force-dynamic';

const REPORTS = new Set(['sales-daily', 'stock', 'gross-profit']);
const RULES: Record<string, RegExp> = {
  branchId: /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i,
  from: /^\d{4}-\d{2}-\d{2}T[\d:.]+(Z|[+-]\d{2}:\d{2})$/,
  to: /^\d{4}-\d{2}-\d{2}T[\d:.]+(Z|[+-]\d{2}:\d{2})$/,
  format: /^(json|csv)$/,
  groupBy: /^[a-z,]{1,40}$/,
};

/** reportSalesDaily, reportStock, reportGrossProfit (US-161, RPT-001..003): allow-listed report and query; a CSV is passed on byte for byte (its BOM is for Excel). */
export async function GET(req: NextRequest, { params }: { params: Promise<{ name: string }> }) {
  const session = await currentSession(req);
  if (!session) return problem(401, 'invalid-token', 'Not signed in');
  const { name } = await params;
  if (!REPORTS.has(name)) return problem(404, 'not-found', 'Not found');
  const q = new URLSearchParams();
  for (const [key, rule] of Object.entries(RULES)) {
    const v = req.nextUrl.searchParams.get(key);
    if (v === null || v === '') continue;
    if (!rule.test(v)) return problem(400, 'validation', `Invalid ${key}`);
    q.set(key, v);
  }
  const r = await callApi(session, 'GET', `/api/v1/reports/${name}?${q.toString()}`);
  if (r.ok && (r.headers.get('content-type') ?? '').startsWith('text/csv')) {
    return new NextResponse(await r.arrayBuffer(), {
      status: 200,
      headers: { 'content-type': 'text/csv; charset=utf-8', 'content-disposition': `attachment; filename="${name}.csv"`, 'cache-control': 'no-store' },
    });
  }
  return relay(r);
}
