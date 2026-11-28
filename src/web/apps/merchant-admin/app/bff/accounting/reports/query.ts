import { NextRequest } from 'next/server';

const RULES: Record<string, RegExp> = {
  from: /^\d{4}-\d{2}-\d{2}T[\d:.]+(Z|[+-]\d{2}:\d{2})$/,
  to: /^\d{4}-\d{2}-\d{2}T[\d:.]+(Z|[+-]\d{2}:\d{2})$/,
  includeEmpty: /^(true|false)$/,
  detailId: /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i,
  cursor: /^\d{1,7}$/,
  limit: /^\d{1,3}$/,
};

/** Only the allowed report parameters, each well-formed, are passed on; null when one is malformed. */
export function reportQuery(req: NextRequest, allowed: string[]): string | null {
  const q = new URLSearchParams();
  for (const name of allowed) {
    const v = req.nextUrl.searchParams.get(name);
    if (v === null || v === '') continue;
    if (!RULES[name].test(v)) return null;
    q.set(name, v);
  }
  const s = q.toString();
  return s ? `?${s}` : '';
}
