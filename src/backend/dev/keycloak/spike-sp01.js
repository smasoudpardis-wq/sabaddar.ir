// SP-01 (D12): verifies ADR-003 / D06 §7 identity controls against a real Keycloak 26 with the realm-as-code files.
// Usage: KC=http://localhost:8080 NODE_PATH=$(npm root -g) node spike-sp01.js
const { chromium } = require('playwright');
const crypto = require('crypto');
const http = require('http');
// Real callback endpoints (Playwright does not route the redirect leg of a navigation).
const lastCallback = {};
for (const port of [3000, 3001]) {
  http.createServer((req, res) => { lastCallback[port] = new URL(req.url, `http://localhost:${port}`); res.end('ok'); }).listen(port, '127.0.0.1');
}
const KC = process.env.KC || 'http://localhost:8080';
const SECRET = 'dev-only-secret-change-me';
const PASS = 'Spike-Passw0rd-1';
const b64u = (b) => Buffer.from(b).toString('base64url');
const jwt = (t) => JSON.parse(Buffer.from(t.split('.')[1], 'base64url').toString());
let failures = 0;
const check = (name, cond, detail = '') => {
  console.log(`${cond ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`);
  if (!cond) failures++;
};

async function browserLogin(browser, realm, clientId, redirect, username, password, { pkce = true } = {}) {
  const verifier = b64u(crypto.randomBytes(48));
  const challenge = b64u(crypto.createHash('sha256').update(verifier).digest());
  const state = b64u(crypto.randomBytes(16));
  const q = new URLSearchParams({ client_id: clientId, response_type: 'code', scope: 'openid', redirect_uri: redirect, state });
  if (pkce) { q.set('code_challenge', challenge); q.set('code_challenge_method', 'S256'); }
  const port = Number(new URL(redirect).port);
  delete lastCallback[port];
  const page = await browser.newPage();
  const cb = () => (lastCallback[port] && lastCallback[port].searchParams.get('state') === state ? lastCallback[port] : null);
  await page.goto(`${KC}/realms/${realm}/protocol/openid-connect/auth?${q}`);
  let callback = cb();
  if (!callback && (await page.locator('#username').count())) {
    await page.fill('#username', username);
    await page.fill('#password', password);
    await Promise.all([page.waitForLoadState('networkidle').catch(() => {}), page.click('#kc-login')]);
  }
  for (let i = 0; i < 20 && !(callback = cb()); i++) await page.waitForTimeout(250);
  const html = callback ? '' : await page.content();
  await page.close();
  return { verifier, state, callback, html };
}

async function token(realm, form) {
  const r = await fetch(`${KC}/realms/${realm}/protocol/openid-connect/token`, {
    method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams(form),
  });
  return { status: r.status, body: await r.json().catch(() => ({})) };
}

(async () => {
  const browser = await chromium.launch();
  const M = { realm: 'merchant', client: 'rc-bff-merchant', redirect: 'http://127.0.0.1:3000/auth/callback' };

  // C1 — PKCE is mandatory for the BFF client
  const noPkce = await browserLogin(browser, M.realm, M.client, M.redirect, 'owner@spike.test', PASS, { pkce: false });
  const c1err = noPkce.callback?.searchParams.get('error') || (/code_challenge/i.test(noPkce.html) ? 'error page: code_challenge' : null);
  check('C1 authorization request without PKCE is rejected', !!c1err && !noPkce.callback?.searchParams.get('code'), c1err || 'got a code');

  // C2/C3 — code exchange requires the verifier; tokens carry the right audience, issuer, lifetime
  const ok = await browserLogin(browser, M.realm, M.client, M.redirect, 'owner@spike.test', PASS);
  const code = ok.callback?.searchParams.get('code');
  check('C2 state is echoed back', ok.callback?.searchParams.get('state') === ok.state);
  const noVerifier = await token(M.realm, { grant_type: 'authorization_code', client_id: M.client, client_secret: SECRET, code, redirect_uri: M.redirect });
  check('C3 code exchange without code_verifier fails', noVerifier.status === 400, `${noVerifier.status} ${noVerifier.body.error}`);
  // a failed exchange invalidates the code in Keycloak; log in again for the positive path
  const ok2 = await browserLogin(browser, M.realm, M.client, M.redirect, 'owner@spike.test', PASS);
  const code2 = ok2.callback?.searchParams.get('code');
  const t = await token(M.realm, { grant_type: 'authorization_code', client_id: M.client, client_secret: SECRET, code: code2, redirect_uri: M.redirect, code_verifier: ok2.verifier });
  check('C4 code + verifier yields tokens', t.status === 200 && !!t.body.access_token, `${t.status} ${t.body.error || ''}`);
  const at = jwt(t.body.access_token);
  const aud = [].concat(at.aud || []);
  check('C5 access token aud contains merchant-api', aud.includes('merchant-api'), JSON.stringify(at.aud));
  check('C6 issuer is the merchant realm', at.iss === `${KC}/realms/merchant`, at.iss);
  // Keycloak computes iat and exp from separate clock reads, so the difference can come out one second long
  check('C7 access token lifetime is 10 minutes (AUTH-05)', Math.abs(at.exp - at.iat - 600) <= 1, `${at.exp - at.iat}s`);
  check('C8 authorized party is the BFF client', at.azp === M.client, at.azp);
  // C8b — the API keys users on (iss, sub); realm-as-code must declare the 'basic' scope (found by the US-021 E2E)
  check('C8b access token carries sub and auth_time, no email', typeof at.sub === 'string' && at.sub.length > 0 && typeof at.auth_time === 'number' && !('email' in at),
    `sub=${at.sub} auth_time=${at.auth_time}`);
  // C10/C11 — refresh token rotation with reuse detection (AUTH-06)
  const r1 = await token(M.realm, { grant_type: 'refresh_token', client_id: M.client, client_secret: SECRET, refresh_token: t.body.refresh_token });
  check('C10 refresh returns a new refresh token', r1.status === 200 && r1.body.refresh_token && r1.body.refresh_token !== t.body.refresh_token,
    `${r1.status} ${r1.body.error || ''} ${r1.body.error_description || ''}`);
  const reuse = await token(M.realm, { grant_type: 'refresh_token', client_id: M.client, client_secret: SECRET, refresh_token: t.body.refresh_token });
  check('C11 reusing a rotated refresh token is rejected', reuse.status === 400, `${reuse.status} ${reuse.body.error}`);

  // C9 — replaying an authorization code fails AND revokes what that code issued (RFC 6749 §4.1.2)
  const reuseCode = await token(M.realm, { grant_type: 'authorization_code', client_id: M.client, client_secret: SECRET, code: code2, redirect_uri: M.redirect, code_verifier: ok2.verifier });
  check('C9 authorization code cannot be used twice', reuseCode.status === 400, `${reuseCode.status} ${reuseCode.body.error}`);
  const afterReplay = await token(M.realm, { grant_type: 'refresh_token', client_id: M.client, client_secret: SECRET, refresh_token: r1.body.refresh_token });
  check('C9b code replay revokes the session issued from that code', afterReplay.status === 400, `${afterReplay.status} ${afterReplay.body.error}`);

  // C12 — password grant is disabled for the BFF (no credentials ever pass through our code)
  const pw = await token(M.realm, { grant_type: 'password', client_id: M.client, client_secret: SECRET, username: 'owner@spike.test', password: PASS });
  check('C12 resource-owner password grant is disabled', pw.status >= 400, `${pw.status} ${pw.body.error}`);

  // C13 — realm separation: a platform token is not a merchant-api token (BR-004)
  const P = { realm: 'platform', client: 'rc-bff-platform', redirect: 'http://127.0.0.1:3001/auth/callback' };
  const pl = await browserLogin(browser, P.realm, P.client, P.redirect, 'operator@spike.test', PASS);
  const pt = await token(P.realm, { grant_type: 'authorization_code', client_id: P.client, client_secret: SECRET, code: pl.callback?.searchParams.get('code'), redirect_uri: P.redirect, code_verifier: pl.verifier });
  const pat = jwt(pt.body.access_token);
  const paud = [].concat(pat.aud || []);
  check('C13 platform token has platform-api and not merchant-api, different issuer',
    paud.includes('platform-api') && !paud.includes('merchant-api') && pat.iss !== at.iss, JSON.stringify(pat.aud));
  check('C14 merchant user cannot log in to the platform realm',
    !(await browserLogin(browser, P.realm, P.client, P.redirect, 'owner@spike.test', PASS)).callback?.searchParams.get('code'));

  // C15 — brute-force protection (AUTH-04): 10 failures → temporary lock, verified via admin API
  for (let i = 0; i < 10; i++) await browserLogin(browser, M.realm, M.client, M.redirect, 'bruteforce@spike.test', 'wrong-password-' + i);
  const admin = await token('master', { grant_type: 'password', client_id: 'admin-cli', username: 'admin', password: 'admin' });
  const H = { authorization: `Bearer ${admin.body.access_token}` };
  const [u] = await (await fetch(`${KC}/admin/realms/merchant/users?username=bruteforce@spike.test&exact=true`, { headers: H })).json();
  const bf = await (await fetch(`${KC}/admin/realms/merchant/attack-detection/brute-force/users/${u.id}`, { headers: H })).json();
  check('C15 ten failed logins temporarily lock the account', bf.disabled === true && bf.numFailures >= 10, JSON.stringify(bf));
  const afterLock = await browserLogin(browser, M.realm, M.client, M.redirect, 'bruteforce@spike.test', PASS);
  check('C16 correct password is refused while locked', !afterLock.callback?.searchParams.get('code'));

  await browser.close();
  console.log(failures ? `\n${failures} check(s) FAILED` : '\nSP-01 ALL CHECKS PASSED');
  process.exit(failures ? 1 : 0); // also stops the callback servers
})().catch((e) => { console.error(e); process.exit(2); });
