// US-021 / AUTH-07 / SEC-T-003 browser proof. Exit code 1 on any failed check.
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require('playwright');

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

  // E14 select through the UI
  await page.getByTestId(`select-${T2}`).click();
  await page.getByTestId('active-tenant').waitFor();
  check('E14', 'select store → tenant-scoped API answers with that store', (await page.getByTestId('active-tenant').textContent()).includes('فروشگاه ب'));

  // E15 switch
  await page.getByTestId(`select-${T1}`).click();
  // locator wait, not waitForFunction: the strict CSP (no 'unsafe-eval') correctly blocks Playwright's eval-based polling
  await page.getByTestId('active-tenant').filter({ hasText: 'فروشگاه الف' }).waitFor();
  check('E15', 'switching store changes the tenant context', true);

  // E16 logout
  const oldSid = (await ctx.cookies(BFF)).find((c) => c.name === 'rc_sid').value;
  await page.getByTestId('logout').click();
  await page.getByTestId('login').waitFor();
  const after = await page.request.get(`${BFF}/bff/me`);
  check('E16', 'after logout /bff/me → 401', after.status() === 401);
  const replay = await fetch(`${BFF}/bff/me`, { headers: { cookie: `rc_sid=${oldSid}` } });
  check('E17', 'old session id is dead server-side', replay.status === 401);
} catch (e) {
  check('E99', 'unexpected error', false, String(e));
} finally {
  await browser.close();
}
const failed = results.filter((r) => !r.ok).length;
console.log(`${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
