// US-191 / D10 §7: shared helpers of the k6 load scenarios. Nothing here is a secret: the user is a dev-realm test account and the
// device secrets are the ones tests/perf/seed.sql registers. Run through tests/perf/run-perf.sh (it seeds, starts the API, runs k6).
import http from 'k6/http';
import { check, fail } from 'k6';
import { uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';

export const BASE = __ENV.BASE || 'http://127.0.0.1:5080/api/v1';
export const KC = __ENV.KC || 'http://127.0.0.1:8080';
export const TENANT = __ENV.TENANT || '0194f000-0000-7000-8000-0000000000f1';
export const REGISTERS = Number(__ENV.REGISTERS || 20);
export const LINES = Number(__ENV.LINES || 15);          // D10 §7.1: 15 items per sale
export const BRANCH = '0194f000-0000-7000-8000-0000000000f3';   // seed.sql
export const HOT = (__ENV.HOT || '') === '1';            // L5: every cart is the same SKU

/** A token of the perf user (Keycloak dev realm, client rc-perf with direct grant, created by run-perf.sh). */
export function token() {
  const r = http.post(`${KC}/realms/merchant/protocol/openid-connect/token`,
    { grant_type: 'password', client_id: 'rc-perf', username: __ENV.PERF_USER || 'cashier@spike.test', password: __ENV.PERF_PASSWORD || 'Spike-Passw0rd-1' });
  if (r.status !== 200) fail(`token: ${r.status} ${r.body}`);
  return r.json('access_token');
}

/** Headers for register number i (1-based): the register's device secret and the store. */
export function headers(tok, i, extra = {}) {
  return { Authorization: `Bearer ${tok}`, 'Content-Type': 'application/json', 'X-RC-Tenant': TENANT, 'X-RC-Device': `perf-device-${i}`, ...extra };
}

export const json = (o) => JSON.stringify(o);
export const idem = () => ({ 'Idempotency-Key': uuidv4().replace(/-/g, '') });

/** One sale as a cashier does it: cart, items (scan), cash tender, Finalize. Tags every call by operation for the thresholds. */
export function sale(tok, register, skus, registers) {
  const h = (extra) => headers(tok, register, extra);
  let r = http.post(`${BASE}/pos/sales`, json({ registerId: registers[register - 1], clientDraftId: 'd-' + uuidv4().replace(/-/g, '') }), { headers: h(), tags: { op: 'createSale' } });
  if (!check(r, { 'cart created': (x) => x.status === 201 || x.status === 200 }, { op: 'createSale' })) return null;
  const id = r.json('id');
  let cart = r.json();
  for (let n = 0; n < LINES; n++) {
    const sku = HOT ? skus[0] : skus[Math.floor(Math.random() * skus.length)];
    const l = http.post(`${BASE}/catalog/barcodes/resolve`, json({ barcode: sku, branchId: BRANCH }), { headers: h(), tags: { op: 'lookup' } });   // the scan (D10: 1.2 per line)
    check(l, { 'resolved': (x) => x.status === 200 }, { op: 'lookup' });
    r = http.post(`${BASE}/pos/sales/${id}/lines`, json({ barcode: sku, qty: '1' }), { headers: h(), tags: { op: 'addLine' } });
    if (!check(r, { 'line added': (x) => x.status === 200 }, { op: 'addLine' })) return null;
    cart = r.json();
  }
  const total = cart.totals.total;
  const handed = Math.ceil(Number(total) / 50000) * 50000;
  r = http.post(`${BASE}/pos/sales/${id}/tenders`, json({ method: 'Cash', amount: total, tendered: String(handed) }), { headers: h(idem()), tags: { op: 'tender' } });
  if (!check(r, { 'paid': (x) => x.status === 200 }, { op: 'tender' })) return null;
  cart = r.json();
  r = http.post(`${BASE}/sales/${id}/finalize`, json({ expectedVersion: cart.version, expectedTotal: cart.totals.total }), { headers: h(idem()), tags: { op: 'finalize' } });
  // 409s a business rule gives (e.g. a version race the client retries) are not errors of the system under test
  check(r, { 'finalized': (x) => x.status === 200 && x.json('status') === 'Finalized' }, { op: 'finalize' });
  return r;
}

/** setup(): products with big stock, one open shift per register. Returns what the VUs need. */
export function prepare() {
  const tok = token();
  const owner = { Authorization: `Bearer ${tok}`, 'Content-Type': 'application/json', 'X-RC-Tenant': TENANT };
  const wh = http.get(`${BASE}/warehouses`, { headers: owner });
  const warehouseId = wh.json('items')[0].id;
  const skus = [];
  for (let k = 0; k < 40; k++) {
    const sku = `PERF-${String(k).padStart(3, '0')}`;
    const r = http.post(`${BASE}/products`, json({
      name: 'کالای بار ' + sku, baseUnit: 'EA', taxRate: 1000, confirmation: { confirmed: true },
      openingStock: { warehouseId, qty: '1000000', unitCost: '50000' },
      variants: [{ sku, unitPrice: String(100000 + k * 1000), barcodes: [{ code: sku, isInternal: true }] }],
    }), { headers: { ...owner, ...idem() } });
    if (r.status === 201 || r.status === 409) skus.push(sku);   // 409: left over from an earlier run
  }
  // the registers seed.sql made: ids are 0194f000-0000-7000-8000-<hex 4096+n>
  const registers = [];
  for (let n = 1; n <= REGISTERS; n++) registers.push('0194f000-0000-7000-8000-' + (4096 + n).toString(16).padStart(12, '0'));
  for (let i = 1; i <= Math.min(REGISTERS, registers.length); i++) {
    http.post(`${BASE}/shifts`, json({ registerId: registers[i - 1], openingCash: '0' }), { headers: headers(tok, i, idem()) });   // 409 = already open
  }
  if (skus.length === 0 || registers.length === 0) fail('perf fixture is empty: run tests/perf/seed.sql first');
  return { skus, registers };
}
