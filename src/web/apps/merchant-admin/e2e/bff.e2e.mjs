// US-021 / US-022 browser proof (AUTH-02, AUTH-07, SEC-T-003, TM-006, AT-012). Exit code 1 on any failed check.
import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
import { createHmac } from 'node:crypto';
const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const Redis = createRequire(`${process.env.APP_DIR}/package.json`)('ioredis');

/** RFC 6238 TOTP (SHA-1, 6 digits, 30 s) — Keycloak's default OTP policy. */
function totp(base32) {
  const A = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'; let bits = '';
  for (const ch of base32.replace(/[\s=]/g, '').toUpperCase()) bits += A.indexOf(ch).toString(2).padStart(5, '0');
  const key = Buffer.from(bits.match(/.{8}/g).map((b) => parseInt(b, 2)));
  const ctr = Buffer.alloc(8); ctr.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const h = createHmac('sha1', key).update(ctr).digest(); const o = h[h.length - 1] & 15;
  return String((h.readUInt32BE(o) & 0x7fffffff) % 1e6).padStart(6, '0');
}

const BFF = 'http://127.0.0.1:3000';
const { T1, T2, TF } = process.env;
const results = [];
const check = (id, name, ok, detail = '') => { results.push({ id, ok }); console.log(`${ok ? 'PASS' : 'FAIL'} ${id} ${name}${detail ? ' — ' + detail : ''}`); };

const browser = await chromium.launch();
const ctx = await browser.newContext();
const page = await ctx.newPage();
const post = (path, body, headers = {}) => page.evaluate(async ([p, b, h]) => {
  const r = await fetch(p, { method: 'POST', headers: { 'content-type': 'application/json', ...h }, body: JSON.stringify(b) });
  return { status: r.status, body: r.status === 204 ? null : await r.json().catch(() => null) };
}, [path, body, headers]);
const csrf = () => page.evaluate(() => document.cookie.split('; ').find((c) => c.startsWith('rc_csrf='))?.split('=')[1] ?? '');

try {
  // E01 anonymous
  const anon = await page.request.get(`${BFF}/bff/me`);
  check('E01', 'anonymous /bff/me → 401', anon.status() === 401);

  // E02 security headers on the page
  const resp = await page.goto(BFF);
  const h = resp.headers();
  check('E02', 'CSP nonce + frame-ancestors none + XFO DENY + nosniff + no-referrer',
    /script-src 'self' 'nonce-/.test(h['content-security-policy'] ?? '') && /frame-ancestors 'none'/.test(h['content-security-policy'])
    && h['x-frame-options'] === 'DENY' && h['x-content-type-options'] === 'nosniff' && h['referrer-policy'] === 'no-referrer'
    && !h['x-powered-by']);

  // E03 login through Keycloak (Authorization Code + PKCE, confidential client)
  await page.getByTestId('login').click();
  await page.waitForURL(/\/realms\/merchant\/protocol\/openid-connect\/auth/);
  const authUrl = new URL(page.url());
  check('E03', 'authorize request uses PKCE S256 + state + nonce',
    authUrl.searchParams.get('code_challenge_method') === 'S256' && !!authUrl.searchParams.get('state') && !!authUrl.searchParams.get('nonce'));
  await page.fill('#username', 'owner@spike.test');
  await page.fill('#password', 'Spike-Passw0rd-1');
  await page.click('#kc-login');
  await page.waitForURL(`${BFF}/`);
  await page.getByTestId('welcome').waitFor();
  check('E04', 'login completes and API identifies the user', (await page.getByTestId('welcome').textContent()).includes('مالک آزمایشی'));

  // E05 cookies
  const cookies = await ctx.cookies(BFF);
  const sid = cookies.find((c) => c.name === 'rc_sid');
  check('E05', 'session cookie is HttpOnly + SameSite=Lax + path /', !!sid && sid.httpOnly && sid.sameSite === 'Lax' && sid.path === '/');
  check('E06', 'pending-auth cookie removed after callback', !cookies.some((c) => c.name === 'rc_auth'));

  // E07 no token reachable from JavaScript
  const leaked = await page.evaluate(() => {
    const all = document.cookie + JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }) + document.documentElement.outerHTML;
    return /eyJ[A-Za-z0-9_-]{10,}\./.test(all);
  });
  check('E07', 'no JWT in document.cookie / storage / DOM', !leaked);

  // E08 memberships
  const ids = await page.locator('[data-testid^="select-"]').evaluateAll((els) => els.map((e) => e.dataset.testid.slice(7)));
  check('E08', 'only own stores listed', ids.length === 2 && ids.includes(T1) && ids.includes(T2) && !ids.includes(TF), ids.join(','));

  // E09 tenant-scoped call before selection
  const early = await page.evaluate(async () => (await fetch('/bff/tenant')).json());
  check('E09', 'tenant call before selection → 403 tenant-not-selected', early.code === 'tenant-not-selected', early.code);

  // E10–E13 CSRF and membership
  const token = await csrf();
  const noCsrf = await post('/bff/active-tenant', { tenantId: T1 });
  check('E10', 'state change without CSRF token → 403', noCsrf.status === 403 && noCsrf.body?.code === 'csrf');
  const wrongCsrf = await post('/bff/active-tenant', { tenantId: T1 }, { 'x-csrf-token': 'x' + token });
  check('E11', 'state change with wrong CSRF token → 403', wrongCsrf.status === 403);
  const crossOrigin = await page.request.post(`${BFF}/bff/active-tenant`, {
    headers: { origin: 'https://evil.example', 'x-csrf-token': token, 'content-type': 'application/json' }, data: { tenantId: T1 } });
  check('E12', 'foreign Origin with valid token → 403', crossOrigin.status() === 403);
  const foreign = await post('/bff/active-tenant', { tenantId: TF }, { 'x-csrf-token': token });
  check('E13', 'selecting a store the user is not a member of → 404 not-found', foreign.status === 404 && foreign.body?.code === 'not-found', `${foreign.status} ${foreign.body?.code}`);

  // E14 Cashier role: no second factor needed (IAM-003 is per role)
  await page.getByTestId(`select-${T2}`).click();
  await page.getByTestId('active-tenant').waitFor();
  check('E14', 'select store as Cashier with password only → works', (await page.getByTestId('active-tenant').textContent()).includes('فروشگاه ب'));

  // E15–E16 TenantOwner role without MFA → the API refuses (AT-290)
  const t1Direct = await post('/bff/active-tenant', { tenantId: T1 }, { 'x-csrf-token': token });
  check('E15', 'select store as TenantOwner with password only → 403 mfa-required', t1Direct.status === 403 && t1Direct.body?.code === 'mfa-required', `${t1Direct.status} ${t1Direct.body?.code}`);
  const sidBefore = (await ctx.cookies(BFF)).find((c) => c.name === 'rc_sid').value;

  // E17–E19 the UI steps up through Keycloak: no TOTP yet → enrolment page → code → back with store A active
  await page.getByTestId(`select-${T1}`).click();
  await page.waitForURL(/login-actions\/required-action|openid-connect\/auth/);
  const authReq = new URL(page.url());
  const enrol = page.locator('#mode-manual');
  await enrol.waitFor();
  check('E16', 'user without a second factor is sent to TOTP enrolment', new URL(page.url()).searchParams.get('execution') === 'CONFIGURE_TOTP' || /required-action/.test(authReq.pathname));
  await enrol.click();
  const secret = (await page.locator('#kc-totp-secret-key').textContent()).trim();
  await page.fill('#totp', totp(secret));
  await page.fill('#userLabel', 'e2e');
  await page.click('#saveTOTPBtn');
  await page.waitForURL(`${BFF}/`);
  // locator waits, not waitForFunction: the strict CSP (no 'unsafe-eval') correctly blocks Playwright's eval-based polling
  await page.getByTestId('active-tenant').filter({ hasText: 'فروشگاه الف' }).waitFor();
  check('E17', 'after the second factor the TenantOwner store is active', true);
  check('E18', 'UI and API report the MFA state', (await page.getByTestId('mfa-state').textContent()).includes('تأییدشده'));
  const sidAfter = (await ctx.cookies(BFF)).find((c) => c.name === 'rc_sid').value;
  const stale = await fetch(`${BFF}/bff/me`, { headers: { cookie: `rc_sid=${sidBefore}` } });
  check('E19', 'session id rotated on step-up; the pre-MFA session is dead (TM-006)', sidAfter !== sidBefore && stale.status === 401);

  // E20–E21 Valkey: sessions survive a BFF restart; stored data is neither readable tokens nor raw ids
  execFileSync(process.env.RESTART_BFF, { stdio: 'inherit' });
  const afterRestart = await page.evaluate(async () => { const r = await fetch('/bff/tenant'); return { s: r.status, b: await r.json() }; });
  check('E20', 'session survives a BFF restart (Valkey store)', afterRestart.s === 200 && afterRestart.b.legalName === 'فروشگاه الف', String(afterRestart.s));
  const redis = new Redis(process.env.VALKEY_URL ?? 'redis://127.0.0.1:6379');
  const keys = await redis.keys('rc:bff:*');
  const values = await Promise.all(keys.map((k) => redis.get(k)));
  await redis.quit();
  const plain = values.some((v) => /eyJ|refresh|accessToken/.test(v ?? '')) || keys.some((k) => k.includes(sidAfter));
  check('E21', 'Valkey holds only encrypted values under hashed keys', keys.length > 0 && !plain, `${keys.length} keys`);

  // E22–E23 logout
  const oldSid = (await ctx.cookies(BFF)).find((c) => c.name === 'rc_sid').value;
  await page.getByTestId('logout').click();
  await page.getByTestId('login').waitFor();
  const after = await page.request.get(`${BFF}/bff/me`);
  check('E22', 'after logout /bff/me → 401', after.status() === 401);
  const replay = await fetch(`${BFF}/bff/me`, { headers: { cookie: `rc_sid=${oldSid}` } });
  check('E23', 'old session id is dead server-side', replay.status === 401);

  // E24 open redirect after login is refused
  const evil = await fetch(`${BFF}/auth/login?returnTo=//evil.example/x`, { redirect: 'manual' });
  const pendingCookie = evil.headers.get('set-cookie') ?? '';
  check('E24', 'returnTo outside the app is ignored (no open redirect)', evil.status === 307 && !/evil/.test(evil.headers.get('location') ?? '') && pendingCookie.includes('rc_auth='));
} catch (e) {
  check('E99', 'unexpected error', false, String(e));
} finally {
  await browser.close();
}
const failed = results.filter((r) => !r.ok).length;
console.log(`${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
