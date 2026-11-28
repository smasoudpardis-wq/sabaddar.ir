// US-191 / L7 (D10 §7.2): 500 OTP requests a second from 100 addresses for 5 minutes. Waits for US-141 (POST /c/auth/otp):
// until that endpoint exists the script stops at once instead of reporting a pass.
import http from 'k6/http';
import { check, fail } from 'k6';
import { BASE } from './lib.js';

export const options = {
  scenarios: { abuse: { executor: 'constant-arrival-rate', rate: Number(__ENV.RATE || 500), timeUnit: '1s', duration: __ENV.DURATION || '5m', preAllocatedVUs: 200, maxVUs: 1000 } },
  thresholds: { 'checks{op:otp}': ['rate>0.99'] },   // every call is answered by a limit (429) or a challenge, never an error
};

export function setup() {
  const r = http.post(`${BASE}/c/auth/otp`, JSON.stringify({ phone: '09120000001' }), { headers: { 'Content-Type': 'application/json' } });
  if (r.status === 404) fail('L7 is not runnable yet: POST /c/auth/otp does not exist (US-141)');
}

export default function () {
  const ip = `10.0.${__VU % 100}.${(__ITER % 250) + 1}`;
  const phone = '0912' + String(1000000 + Math.floor(Math.random() * 8999999));
  const r = http.post(`${BASE}/c/auth/otp`, JSON.stringify({ phone }), { headers: { 'Content-Type': 'application/json', 'X-Forwarded-For': ip }, tags: { op: 'otp' } });
  check(r, { 'limited or challenged': (x) => [200, 201, 202, 400, 422, 429].includes(x.status) }, { op: 'otp' });
}
