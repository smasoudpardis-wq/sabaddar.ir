// US-191 / D10 §7.2: the POS load scenarios. SCENARIO = L1 | L2 | L3 | L5 | L6 | SMOKE (L4 soak is L1 for 8 h: DURATION=8h).
// Thresholds are the SLOs of D10 §7.2: Finalize p95 < 2 s and p99 < 4 s, addLine p95 < 250 ms, Lookup p95 < 300 ms, errors < 0.1 %.
import { token, sale, prepare, REGISTERS } from './lib.js';

const RATE = { L1: 13.3, L2: 40, L3: 66.5, L5: 100, L6: 40, SMOKE: 1 };
const S = __ENV.SCENARIO || 'SMOKE';
const DURATION = __ENV.DURATION || { L1: '30m', L2: '30m', L3: '10m', L5: '10m', L6: '15m', SMOKE: '1m' }[S];
const slo = S === 'L3' ? { finalize: ['p(95)<4000'] } : { finalize: ['p(95)<2000', 'p(99)<4000'] };

export const options = {
  scenarios: {
    pos: { executor: 'constant-arrival-rate', rate: Number(__ENV.RATE || RATE[S]), timeUnit: '1s', duration: DURATION,
           preAllocatedVUs: Number(__ENV.VUS || 100), maxVUs: Number(__ENV.MAX_VUS || 600) },
  },
  thresholds: {
    'http_req_duration{op:finalize}': slo.finalize,
    'http_req_duration{op:addLine}': ['p(95)<250'],
    'http_req_duration{op:lookup}': ['p(95)<300'],
    'checks{op:finalize}': ['rate>0.999'],
    'checks{op:addLine}': ['rate>0.999'],
  },
};

export function setup() { return prepare(); }

let tok;
let tokAt = 0;
export default function (data) {
  if (!tok || Date.now() - tokAt > 120000) { tok = token(); tokAt = Date.now(); }   // tokens live minutes: refresh per VU
  const register = 1 + ((__VU - 1) % Math.min(REGISTERS, data.registers.length));
  sale(tok, register, data.skus, data.registers);
}
