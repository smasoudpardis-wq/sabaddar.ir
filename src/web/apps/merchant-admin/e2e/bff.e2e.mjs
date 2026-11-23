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
// Optional evidence for reviewers: SCREENSHOTS=<dir> saves key screens (phone width, like most store owners use).
const shot = async (p, name) => { if (process.env.SCREENSHOTS) await p.screenshot({ path: `${process.env.SCREENSHOTS}/${name}.png`, fullPage: true }); };
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
  await page.getByTestId('plan').waitFor();
  check('L01', 'the home page shows the plan, the trial end and the caps (US-014)', /آزمایشی.*پایان دوره آزمایشی.*۲ شعبه/.test(await page.getByTestId('plan').textContent()));

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
  // Every value must be the sealed format, and the decoded ciphertext must not contain token field names.
  // (Do not grep the base64 text for "eyJ": random ciphertext contains it by chance now and then.)
  const sealed = /^v1\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/;
  const plain = values.some((v) => !sealed.test(v ?? '')
      || /accessToken|refreshToken|"exp"/.test(Buffer.from((v ?? '').split('.')[2] ?? '', 'base64url').toString('latin1')))
    || keys.some((k) => k.includes(sidAfter));
  check('E21', 'Valkey holds only encrypted values under hashed keys', keys.length > 0 && !plain, `${keys.length} keys`);

  // I01–I06 US-025: the owner invites a cashier for one branch; the cashier joins through the link (IAM-001, IAM-006)
  await page.goto(`${BFF}/users`);
  await page.getByTestId('users').waitFor();
  check('I01', 'owner sees the users page', (await page.getByTestId('users').textContent()).includes('مالک آزمایشی'));
  await page.fill('input[name=displayName]', 'صندوق‌دار آزمایشی');
  await page.locator('select[name=roleId]').selectOption({ label: 'صندوق‌دار' });
  await page.locator('select[name=branchId]').selectOption({ label: 'شعبه مرکزی' });
  await page.getByTestId('invite-submit').click();
  const link = (await page.getByTestId('invite-link').textContent()).trim();
  check('I02', 'invitation link created for Cashier @ branch', /\/invite\/[A-Za-z0-9_-]{43}$/.test(link), link.replace(/[A-Za-z0-9_-]{43}$/, '…'));
  await shot(page, '5-invite');

  const cctx = await browser.newContext();
  const cp = await cctx.newPage();
  await cp.goto(link);
  await cp.getByTestId('invite-login').click();
  await cp.fill('#username', 'cashier@spike.test');
  await cp.fill('#password', 'Spike-Passw0rd-1');
  await cp.click('#kc-login');
  await cp.getByTestId('invite-accept').click();
  await cp.waitForURL(`${BFF}/`);
  await cp.getByTestId('active-tenant').filter({ hasText: 'فروشگاه الف' }).waitFor();
  check('I03', 'invitee signs in, accepts and lands in the store (Cashier: password only)',
    (await cp.getByTestId('mfa-state').textContent()).includes('رمز عبور'));
  const cashierUsers = await cp.evaluate(async () => { const r = await fetch('/bff/users'); return { s: r.status, b: await r.json() }; });
  check('I04', 'the cashier cannot open user administration (403 forbidden, SEC-T-012)', cashierUsers.s === 403 && cashierUsers.b.code === 'forbidden', `${cashierUsers.s} ${cashierUsers.b.code}`);
  await cp.goto(link);
  await cp.getByTestId('invite-accept').click();
  await cp.getByTestId('invite-error').waitFor();
  check('I05', 'the link is single use', (await cp.getByTestId('invite-error').textContent()).includes('معتبر نیست'));
  await cctx.close();
  await page.goto(`${BFF}/users`);
  await page.getByTestId('users').filter({ hasText: 'صندوق‌دار' }).waitFor();
  check('I06', 'the owner sees the new cashier with role and branch', (await page.getByTestId('users').textContent()).includes('صندوق‌دار (مرکزی)'));
  await page.goto(BFF);
  await page.getByTestId('logout').waitFor();

  // B01–B02 US-012: the owner adds a branch from the panel; duplicates are refused
  await page.goto(`${BFF}/branches`);
  await page.getByTestId('branches').waitFor();
  await page.fill('input[name=code]', 'B2');
  await page.fill('input[name=name]', 'شعبه شمال');
  await page.getByTestId('branch-submit').click();
  await page.getByTestId('branches').filter({ hasText: 'شعبه شمال' }).waitFor();
  check('B01', 'owner creates a branch from the panel', true);
  await page.fill('input[name=code]', 'B2');
  await page.fill('input[name=name]', 'تکراری');
  await page.getByTestId('branch-submit').click();
  await page.getByTestId('branch-error').waitFor();
  check('B02', 'duplicate branch code is refused with a clear message', (await page.getByTestId('branch-error').textContent()).includes('قبلاً'));
  // B03 US-014: store T1 is on the trial plan (2 branches): a third is refused with the D09 upgrade message
  await page.fill('input[name=code]', 'B3');
  await page.fill('input[name=name]', 'شعبه سوم');
  await page.getByTestId('branch-submit').click();
  await page.getByTestId('branch-error').filter({ hasText: 'سقف پلن' }).waitFor();
  check('B03', 'a branch beyond the plan is refused with the upgrade message (TEN-003)', (await page.getByTestId('branch-error').textContent()).includes('(شعبه)'));
  await page.goto(BFF);
  await page.getByTestId('logout').waitFor();

  // T01–T04 US-013: issue a pairing code, pair this browser as a terminal, the API recognises it, revoke it
  await page.goto(`${BFF}/terminals`);
  await page.getByTestId('this-device').waitFor();
  check('T01', 'a fresh browser is not a terminal', (await page.getByTestId('this-device').textContent()).includes('نیست'));
  await page.getByTestId('issue-code').click();
  const pairingCode = (await page.getByTestId('pairing-code').textContent()).trim();
  await page.fill('input[name=pairingCode]', pairingCode);
  await page.fill('input[name=deviceName]', 'صندوق-آزمایشی');
  await page.getByTestId('pair-submit').click();
  await page.getByTestId('this-device').filter({ hasText: 'ثبت‌شده است' }).waitFor();
  const dev = (await ctx.cookies(BFF)).find((c) => c.name === 'rc_dev');
  const visible = await page.evaluate(() => document.cookie.includes('rc_dev'));
  check('T02', 'paired: the API recognises this browser; the device secret is an HttpOnly cookie', !!dev && dev.httpOnly && !visible);
  await page.fill('input[name=pairingCode]', pairingCode);
  await page.fill('input[name=deviceName]', 'دوباره');
  await page.getByTestId('pair-submit').click();
  await page.getByTestId('terminal-error').waitFor();
  check('T03', 'a pairing code works only once', true);

  // P01–P04 US-023: PIN on this registered terminal; lock; wrong PIN counts down; right PIN unlocks (AT-013)
  await page.fill('input[name=newPin]', '4821');
  await page.getByTestId('pin-submit').click();
  await page.getByTestId('pin-saved').waitFor();
  check('P01', 'PIN set right after a full sign-in', true);
  await page.goto(BFF);
  await page.getByTestId('lock').click();
  await page.getByTestId('lock-screen').waitFor();
  const whileLocked = await page.evaluate(async () => { const r = await fetch('/bff/tenant'); return { s: r.status, b: await r.json() }; });
  check('P02', 'locked: the lock screen shows and the API is closed (423)', whileLocked.s === 423 && whileLocked.b.code === 'session-locked', `${whileLocked.s}`);
  await page.fill('input[name=pin]', '0000');
  await page.getByTestId('unlock').click();
  await page.getByTestId('lock-error').waitFor();
  check('P03', 'a wrong PIN shows the attempts left', (await page.getByTestId('lock-error').textContent()).includes('4'));
  await page.fill('input[name=pin]', '4821');
  await page.getByTestId('unlock').click();
  await page.getByTestId('welcome').waitFor();
  check('P04', 'the right PIN unlocks the register', true);
  await page.goto(`${BFF}/terminals`);
  await page.getByTestId('this-device').filter({ hasText: 'ثبت‌شده است' }).waitFor();
  await page.getByTestId('revoke-صندوق-آزمایشی').click();
  await page.getByTestId('this-device').filter({ hasText: 'نیست' }).waitFor();
  check('T04', 'after revocation the device is no longer a terminal (AT-003)', true);
  await page.goto(BFF);
  await page.getByTestId('logout').waitFor();

  // A01–A02 US-026: a cashier's discount request (seeded) waits in the owner's inbox; the owner approves remotely
  await page.goto(`${BFF}/approvals`);
  const pendingId = process.env.APPROVAL_ID;
  await page.getByTestId(`approve-${pendingId}`).waitFor();
  check('A01', 'the pending request is in the approver inbox', (await page.getByTestId('approvals').textContent()).includes('تخفیف دستی'));
  await page.getByTestId(`approve-${pendingId}`).click();
  await page.getByTestId(`decided-${pendingId}`).waitFor();
  check('A02', 'the owner approves it (fresh MFA; approver ≠ requester)', (await page.getByTestId(`decided-${pendingId}`).textContent()).includes('تأیید'));
  // U01–U02 US-032 the owner reads the audit trail: the approval just made is on top, with who and from where
  await page.goto(`${BFF}/audit`);
  await page.getByTestId('audit-rows').waitFor();
  const top = page.locator('[data-testid="audit-rows"] tbody tr').first();
  check('U01', 'the decision just made is the newest audit record, with role and client address',
    (await top.getAttribute('data-action')) === 'iam.approval.approved' && /TenantOwner/.test(await top.textContent()) && /127\.0\.0\.1/.test(await top.textContent()));
  await page.getByTestId('audit-group').selectOption('platform.');
  await Promise.all([page.waitForResponse((r) => r.url().includes('/bff/audit') && r.url().includes('action=platform.')),
    page.getByTestId('audit-search').click()]);
  await page.locator('[data-testid="audit-rows"] tbody tr[data-action^="iam."]').first().waitFor({ state: 'detached' });
  const shown = await page.locator('[data-testid="audit-rows"] tbody tr').evaluateAll((trs) => trs.map((t) => t.getAttribute('data-action')));
  check('U02', 'filtering by group shows only that group', shown.length > 0 && shown.every((a) => a.startsWith('platform.')), shown.join(','));
  await page.goto(BFF);
  await page.getByTestId('logout').waitFor();

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
  // N01–N05 UC-10 self-service sign-up of a brand-new identity (US-011): wizard → MFA enrolment → Workers provision
  const nctx = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2 });
  const np = await nctx.newPage();
  await np.goto(BFF);
  await np.getByTestId('login').click();
  await np.fill('#username', 'newowner@spike.test');
  await np.fill('#password', 'Spike-Passw0rd-1');
  await np.click('#kc-login');
  await np.waitForURL(`${BFF}/`);
  await np.getByTestId('onboarding-form').waitFor();
  check('N01', 'first login of an unknown identity shows the sign-up wizard', true);
  await np.fill('input[name=legalName]', 'سوپرمارکت آزمایشی');
  await shot(np, '1-signup-form');
  await np.getByTestId('onboarding-submit').click();
  await np.locator('#mode-manual').waitFor();
  check('N02', 'creating a business requires MFA: the owner is sent to TOTP enrolment', new URL(np.url()).searchParams.get('execution') === 'CONFIGURE_TOTP');
  await shot(np, '2-mfa-enrolment');
  await np.click('#mode-manual');
  const nsecret = (await np.locator('#kc-totp-secret-key').textContent()).trim();
  await np.fill('#totp', totp(nsecret));
  await np.fill('#userLabel', 'e2e');
  await np.click('#saveTOTPBtn');
  await np.getByTestId('onboarding-ready').waitFor({ timeout: 45000 });
  await shot(np, '3-store-ready');
  const done = await np.locator('li').allTextContents();
  check('N03', 'after MFA the same draft is resubmitted and the Workers provision every step',
    ['شعبه و صندوق اول', 'انبار اول', 'شجره حساب‌ها', 'دوره‌های مالی سال جاری'].every((x) => done.some((d) => d.includes('✓') && d.includes(x))), done.join(' | '));
  await np.getByTestId('go-home').click();
  await np.getByTestId('active-tenant').filter({ hasText: 'سوپرمارکت آزمایشی' }).waitFor();
  await shot(np, '4-admin-home');
  check('N04', 'the new owner lands in the new store as TenantOwner with MFA', (await np.getByTestId('mfa-state').textContent()).includes('تأییدشده'));
  const draftLeft = await np.evaluate(() => sessionStorage.getItem('rc.onboarding.draft'));
  check('N05', 'the wizard draft is cleared once the store is ready', draftLeft === null);
  await nctx.close();
} catch (e) {
  check('E99', 'unexpected error', false, String(e));
} finally {
  await browser.close();
}
const failed = results.filter((r) => !r.ok).length;
console.log(`${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
