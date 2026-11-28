// US-191 / L6: 20 concurrent report readers beside the POS load (run both: `k6 run pos.js` with SCENARIO=L6 and this one together).
import http from 'k6/http';
import { check } from 'k6';
import { token, BASE, TENANT } from './lib.js';

export const options = {
  scenarios: { readers: { executor: 'constant-vus', vus: 20, duration: __ENV.DURATION || '15m' } },
  thresholds: { 'http_req_duration{op:report}': ['p(95)<5000'], 'checks{op:report}': ['rate>0.99'] },
};

export default function () {
  const h = { Authorization: `Bearer ${token()}`, 'X-RC-Tenant': TENANT };
  for (const path of ['reports/sales-daily', 'reports/stock', 'reports/gross-profit?groupBy=product']) {
    const r = http.get(`${BASE}/${path}`, { headers: h, tags: { op: 'report' } });
    check(r, { 'report ok': (x) => x.status === 200 }, { op: 'report' });
  }
}
