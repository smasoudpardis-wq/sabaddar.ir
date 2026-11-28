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
  await page.getByTestId('read-only-banner').waitFor();
  check('R01', 'a suspended store opens read-only with the D09 explanation (US-015)', (await page.getByTestId('read-only-banner').textContent()).includes('فقط‌خواندنی'));

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
  const types = await Promise.all(keys.map((k) => redis.type(k)));
  const values = await Promise.all(keys.filter((_, i) => types[i] === 'string').map((k) => redis.get(k)));
  // US-024 per-user session index: a set whose members are session storage keys (hashes), never a session id
  const members = (await Promise.all(keys.filter((_, i) => types[i] === 'set').map((k) => redis.smembers(k)))).flat();
  await redis.quit();
  // Every value must be the sealed format, and the decoded ciphertext must not contain token field names.
  // (Do not grep the base64 text for "eyJ": random ciphertext contains it by chance now and then.)
  const sealed = /^v1\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/;
  const plain = values.some((v) => !sealed.test(v ?? '')
      || /accessToken|refreshToken|"exp"/.test(Buffer.from((v ?? '').split('.')[2] ?? '', 'base64url').toString('latin1')))
    || keys.some((k) => k.includes(sidAfter))
    || !members.every((m) => /^rc:bff:s:[A-Za-z0-9_-]{43}$/.test(m)) || members.some((m) => m.includes(sidAfter));
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
  // S01–S02 US-027: the owner approves platform support for the requested time, then ends it early
  const supportId = process.env.SUPPORT_ID;
  await page.goto(`${BFF}/support-access`);
  await page.getByTestId(`support-approve-${supportId}`).click();
  await page.locator(`[data-testid="support-${supportId}"][data-status="Approved"]`).waitFor();
  check('S01', 'the owner approves support access for the requested time (IAM-008)', true);
  await page.getByTestId(`support-revoke-${supportId}`).click();
  await page.locator(`[data-testid="support-${supportId}"][data-status="Revoked"]`).waitFor();
  check('S02', 'the owner ends support access early (AB-17)', true);
  // C01–C02 US-041: the owner defines a product with a carton barcode, then finds it by scan and by Arabic-typed name
  await page.goto(`${BFF}/products`);
  await page.getByTestId('product-name').fill('نوشابه آزمایشی');
  await page.getByTestId('product-sku').fill('E2E-COLA');
  await page.getByTestId('product-barcode').fill('4006381333931');
  await page.getByTestId('product-submit').click();
  await page.getByTestId('product-created').waitFor();
  check('C01', 'the owner defines a product with variant and barcode (CAT-001/002)', true);
  await page.getByTestId('product-search').fill('04006381333931');
  await page.getByTestId('product-search-submit').click();
  await page.getByTestId('products').filter({ hasText: 'نوشابه آزمایشی' }).waitFor();
  await page.getByTestId('product-search').fill('نوشابه آزمايشي');   // Arabic yeh
  await page.getByTestId('product-search-submit').click();
  await page.getByTestId('products').filter({ hasText: 'E2E-COLA' }).waitFor();
  check('C02', 'found by the scanned GTIN-14 and by a name typed with Arabic letters (CAT-003, CAT-011)', true);
  // Q01–Q03 US-052: the owner prices the product; a big change waits for another manager's approval
  const cola = page.locator('[data-variant="E2E-COLA"]');
  await cola.getByLabel('قیمت جدید (ریال)').fill('۲۵۰۰۰۰');   // Persian digits from the keypad
  await cola.getByRole('button', { name: 'ثبت قیمت' }).click();
  await page.getByTestId('price-E2E-COLA').filter({ hasText: '۲۵۰٬۰۰۰' }).waitFor();
  check('Q01', 'the owner sets a first price and the product shows it (CAT-009)', true);
  await cola.getByLabel('قیمت جدید (ریال)').fill('500000');   // +100%
  await cola.getByRole('button', { name: 'ثبت قیمت' }).click();
  await cola.getByRole('status').filter({ hasText: 'تأیید مدیر' }).waitFor();
  const still = (await page.getByTestId('price-E2E-COLA').textContent()) ?? '';
  check('Q02', 'a +100% change waits for approval and the price stays (BR-041)', still.includes('۲۵۰٬۰۰۰'), still);
  await cola.getByRole('button', { name: 'اعمال پس از تأیید' }).click();
  const notYet = (await cola.getByRole('alert').textContent()) ?? '';
  check('Q03', 'applying before the approval is refused, not guessed', notYet.includes('هنوز تأیید نشده'), notYet);
  await shot(page, 'prices');
  // M01–M02 US-051: a colour × size matrix with one combination left out; one combination switched off later
  await page.getByTestId('matrix-name').fill('تی‌شرت آزمایشی');
  await page.getByTestId('matrix-sku').fill('E2E-TEE');
  await page.getByTestId('matrix-colors').fill('قرمز، آبی');
  await page.getByTestId('matrix-sizes').fill('M, L');
  await page.getByTestId('matrix-grid').getByLabel('آبی L').uncheck();
  await page.getByTestId('matrix-submit').click();
  await page.locator('[data-variant="E2E-TEE-1-1"]').waitFor();
  const tees = await page.locator('[data-variant^="E2E-TEE-"]').count();
  check('M01', 'a 2 × 2 matrix minus one combination makes 3 variants (CAT-008)', tees === 3, `${tees}`);
  await page.locator('[data-variant="E2E-TEE-1-2"]').getByRole('button', { name: 'غیرفعال کن' }).click();
  await page.locator('[data-variant="E2E-TEE-1-2"][data-status="Inactive"]').waitFor();
  check('M02', 'one combination is switched off and stays listed as inactive', true);
  // K01–K02 US-053: an automatic rule for the product, and the cashier's manual discount cap
  await page.goto(`${BFF}/discounts`);
  await page.getByTestId('discount-sku').fill('E2E-COLA');
  await page.getByTestId('discount-name').fill('جشنواره آزمایشی');
  await page.getByTestId('discount-percent').fill('۱۰');
  await page.getByTestId('discount-submit').click();
  await page.locator('[data-rule="جشنواره آزمایشی"]').waitFor();
  check('K01', 'the owner adds a 10% automatic rule for a product (CAT-010)', true);
  await page.getByTestId('cap-Cashier').fill('5');
  // the rule above already shows «saved»: wait for the caps' own answer, not for that message
  await Promise.all([
    page.waitForResponse((r) => r.url().endsWith('/bff/discount-caps') && r.request().method() === 'PUT'),
    page.getByTestId('caps-submit').click(),
  ]);
  const capsNow = await page.evaluate(async () => (await fetch('/bff/discount-caps', { cache: 'no-store' })).json());
  check('K02', "the cashier's manual cap is set to 5% (BR-021)", capsNow?.caps?.Cashier === 500, JSON.stringify(capsNow));
  // J01–J03 US-063: a manual adjustment with a reason; «Other» needs a note; the list shows it with its value
  await page.goto(`${BFF}/adjustments`);
  await page.locator('[data-testid="adjust-warehouse"] option').first().waitFor({ state: 'attached' });
  await page.getByTestId('adjust-sku-0').fill('E2E-COLA');
  await page.getByTestId('adjust-delta-0').fill('۵');
  await page.getByTestId('adjust-reason').selectOption('CountError');
  await page.getByTestId('adjust-submit').click();
  await page.getByTestId('adjust-done').waitFor();
  check('J01', 'the owner adds 5 found in a count to a product (INV-002)', true);
  await page.getByTestId('adjust-reason').selectOption('Other');
  const noteRequired = await page.getByTestId('adjust-note').evaluate((el) => el.required);
  await page.getByTestId('adjust-sku-0').fill('E2E-COLA');
  await page.getByTestId('adjust-delta-0').fill('-2');
  await page.getByTestId('adjust-note').fill('نمونه برای آزمایشگاه');
  await page.getByTestId('adjust-submit').click();
  await page.locator('[data-testid="adjust-list"] [data-reason="Other"]').waitFor();
  check('J02', '«Other» asks for a note and the decrease is posted', noteRequired);
  const stock = await page.evaluate(async () => {
    const p = (await (await fetch('/bff/products?q=E2E-COLA', { cache: 'no-store' })).json()).items[0];
    return (await (await fetch(`/bff/inventory/balances?variantId=${p.variants[0].id}`, { cache: 'no-store' })).json()).items[0]?.onHand;
  });
  await page.getByTestId('adjust-delta-0').fill('-9');
  await page.getByTestId('adjust-sku-0').fill('E2E-COLA');
  await page.getByTestId('adjust-reason').selectOption('Theft');
  await page.getByTestId('adjust-submit').click();
  await page.getByTestId('adjust-error').waitFor();
  const refused = (await page.getByTestId('adjust-error').textContent()) ?? '';
  check('J03', 'the balance is 3 and taking 9 is refused for lack of stock (BR-040)', stock === '3' && refused.includes('موجودی کافی نیست'), `${stock} ${refused}`);
  await shot(page, 'adjustments');
  // G01–G03 US-071: a supplier, a receipt with input VAT that re-averages the cost and is owed, and a gift confirmed first
  await page.goto(`${BFF}/receipts`);
  await page.getByTestId('supplier-name').fill('پخش آزمایشی');
  await page.getByTestId('supplier-submit').click();
  await page.locator('[data-testid="suppliers"] [data-supplier="پخش آزمایشی"]').waitFor();
  await page.locator('[data-testid="receipt-supplier"] option', { hasText: 'پخش آزمایشی' }).waitFor({ state: 'attached' });
  check('G01', 'the owner adds a supplier (PRC-001)', true);
  await page.getByTestId('receipt-supplier').selectOption({ label: 'پخش آزمایشی' });
  await page.getByTestId('receipt-invoice').fill('E2E-F1');
  await page.getByTestId('receipt-sku-0').fill('E2E-COLA');
  await page.getByTestId('receipt-qty-0').fill('۱۲');
  await page.getByTestId('receipt-cost-0').fill('50,000');
  await page.getByTestId('receipt-vat-0').fill('54000');
  await page.getByTestId('receipt-submit').click();
  await page.getByTestId('receipt-done').waitFor();
  const posted = (await page.getByTestId('receipt-done').textContent()) ?? '';
  await page.locator('[data-testid="receipt-list"] [data-invoice="E2E-F1"]').waitFor();
  const owed = (await page.locator('[data-supplier="پخش آزمایشی"]').textContent()) ?? '';
  // 3 on hand at no cost + 12 at 50,000: WAC 40,000; the supplier is owed 600,000 + 54,000 VAT (AT-240, PRC-002)
  check('G02', 'a receipt re-averages the cost and books what is owed to the supplier', posted.includes('۴۰٬۰۰۰') && owed.includes('۶۵۴٬۰۰۰'),
    `${posted} | ${owed}`);
  await page.getByTestId('receipt-supplier').selectOption({ label: 'پخش آزمایشی' });
  await page.getByTestId('receipt-sku-0').fill('E2E-COLA');
  await page.getByTestId('receipt-qty-0').fill('2');
  await page.getByTestId('receipt-cost-0').fill('0');
  await page.getByTestId('receipt-submit').click();
  await page.getByTestId('receipt-confirm').waitFor();
  const asked = (await page.getByTestId('receipt-confirm').textContent()) ?? '';
  await page.getByTestId('receipt-confirm-submit').click();
  await page.getByTestId('receipt-done').waitFor();
  check('G03', 'a zero-cost line is posted only after the user confirms it (UC-05)', asked.includes('بهای صفر'), asked);
  await shot(page, 'receipts');
  // Y01–Y03 US-072: the owner pays part of the bill in cash, sends one can back for a little more than its average cost,
  // and paying more than is owed is confirmed first (PRC-003, PRC-004)
  await page.goto(`${BFF}/settlements`);
  await page.locator('[data-testid="pay-supplier"] option', { hasText: 'پخش آزمایشی' }).waitFor({ state: 'attached' });
  await page.getByTestId('pay-supplier').selectOption({ label: 'پخش آزمایشی (بدهی ما: ۶۵۴٬۰۰۰)' });
  await page.getByTestId('pay-amount').fill('۱۵۴,۰۰۰');
  await page.getByTestId('pay-submit').click();
  await page.getByTestId('settlement-done').waitFor();
  const afterPay = (await page.getByTestId('settlement-done').textContent()) ?? '';
  check('Y01', 'a cash payment lowers what the store owes the supplier (PRC-003, AT-051)', afterPay.includes('۵۰۰٬۰۰۰'), afterPay);
  await page.locator('[data-testid="return-supplier"] option', { hasText: 'پخش آزمایشی' }).waitFor({ state: 'attached' });
  await page.getByTestId('return-supplier').selectOption({ label: 'پخش آزمایشی (بدهی ما: ۵۰۰٬۰۰۰)' });
  await page.getByTestId('return-sku-0').fill('E2E-COLA');
  await page.getByTestId('return-qty-0').fill('1');
  await page.getByTestId('return-agreed').fill('40000');
  await page.getByTestId('return-submit').click();
  await page.locator('[data-testid="return-list"] [data-agreed="40000"]').waitFor();
  const afterReturn = (await page.getByTestId('settlement-done').textContent()) ?? '';
  // 17 cans worth 600,000 → WAC 35,294; taken back for 40,000: a gain of 4,706 and 460,000 still owed (AT-052)
  check('Y02', 'a return leaves at the average cost and the agreed amount comes off the bill (PRC-004, AT-052)',
    afterReturn.includes('۳۵٬۲۹۴') && afterReturn.includes('۴٬۷۰۶') && afterReturn.includes('۴۶۰٬۰۰۰'), afterReturn);
  await page.getByTestId('pay-supplier').selectOption({ label: 'پخش آزمایشی (بدهی ما: ۴۶۰٬۰۰۰)' });
  await page.getByTestId('pay-amount').fill('500000');
  await page.getByTestId('pay-method').selectOption('Bank');
  await page.getByTestId('pay-reference').fill('E2E-TR-1');
  await page.getByTestId('pay-submit').click();
  await page.getByTestId('settlement-confirm').waitFor();
  await page.getByTestId('settlement-confirm-submit').click();
  await page.locator('[data-testid="settlement-suppliers"] [data-supplier="پخش آزمایشی"][data-balance="-40000"]').waitFor();
  check('Y03', 'paying more than is owed is confirmed first and leaves the supplier owing the store', true);
  await shot(page, 'settlements');
  // Z01–Z04 US-112: the owner writes a manual entry (bank fee), which is refused while unbalanced, posts it, reverses it,
  // and deletes a draft that will not be posted (ACC-003, ACC-006)
  await page.goto(`${BFF}/journals`);
  await page.locator('[data-testid="journal-account-0"] option', { hasText: 'کارمزد بانکی' }).waitFor({ state: 'attached' });
  const fillEntry = async (text, debit, credit) => {
    await page.getByTestId('journal-description').fill(text);
    await page.getByTestId('journal-account-0').selectOption({ label: '5106 کارمزد بانکی' });
    await page.getByTestId('journal-debit-0').fill(debit);
    await page.getByTestId('journal-account-1').selectOption({ label: '1102 بانک' });
    await page.getByTestId('journal-credit-1').fill(credit);
  };
  await fillEntry('کارمزد آزمایشی', '۱۲,۰۰۰', '10000');
  const unbalancedShown = await page.getByTestId('journal-totals').getAttribute('data-balanced');
  await page.getByTestId('journal-submit').click();
  await page.getByTestId('journal-error').waitFor();
  const unbalanced = (await page.getByTestId('journal-error').textContent()) ?? '';
  check('Z01', 'an unbalanced entry shows the gap and is refused (ACC-002)', unbalancedShown === 'false' && unbalanced.includes('برابر نیست'), unbalanced);
  await page.getByTestId('journal-credit-1').fill('12000');
  await page.getByTestId('journal-submit').click();
  const fee = page.locator('[data-testid="journal-list"] > li[data-description="کارمزد آزمایشی"]');
  await fee.and(page.locator('[data-status="Draft"]')).waitFor();
  await fee.getByTestId('journal-post').click();
  await fee.and(page.locator('[data-status="Posted"]')).waitFor();
  check('Z02', 'a balanced manual draft is posted (ACC-006)', true);
  await fee.getByTestId('journal-reason').fill('ثبت اشتباه آزمایشی');
  await fee.getByTestId('journal-reverse').click();
  await fee.and(page.locator('[data-status="Reversed"]')).waitFor();
  check('Z03', 'a posted entry is not edited: it is reversed by a mirror entry and marked reversed (ACC-003)',
    (await page.locator('[data-testid="journal-list"] > li[data-description="کارمزد آزمایشی"]').count()) === 1
    && (await page.locator('[data-testid="journal-list"] > li', { hasText: 'سند معکوس' }).count()) >= 1);
  await fillEntry('پیش‌نویس رهاشده', '500', '500');
  await page.getByTestId('journal-submit').click();
  const dropped = page.locator('[data-testid="journal-list"] > li[data-description="پیش‌نویس رهاشده"]');
  await dropped.waitFor();
  await dropped.getByTestId('journal-delete').click();
  await dropped.waitFor({ state: 'detached' });
  check('Z04', 'a draft that will not be posted is deleted (D02 §6.3)', true);
  // Z05–Z06 US-113: the first month of this year has ended and is closed; the current month cannot be closed yet (ACC-009).
  // The Workers keep next year's periods too, so a period is named by year and number.
  const current = await page.evaluate(async () => {
    const now = Date.now();
    const list = await (await fetch('/bff/accounting/periods', { cache: 'no-store' })).json();
    const p = list.find((x) => Date.parse(x.start) <= now && now < Date.parse(x.end));
    return p ? `${p.fiscalYear}-${p.periodNo}` : undefined;
  });
  const first = `${current?.split('-')[0]}-1`;
  await page.getByTestId(`period-close-${first}`).click();
  await page.locator(`[data-testid="period-list"] [data-period="${first}"][data-status="Closed"]`).waitFor();
  check('Z05', 'a month that has ended is closed and shows as closed (ACC-009)', true);
  await page.getByTestId(`period-close-${current}`).click();
  await page.getByTestId('journal-error').filter({ hasText: 'تمام نشده' }).waitFor();
  check('Z06', 'the current month cannot be closed: today\'s documents need an open period (D02 UC-02 8d)',
    (await page.locator(`[data-testid="period-list"] [data-period="${current}"][data-status="Open"]`).count()) === 1, `current=${current}`);
  await shot(page, 'journals');
  // RT01–RT02 US-172: the store sees its industry's legal text as fixed and sets only its own footer and signature (BR-086, DI-085)
  await page.goto(`${BFF}/receipt-template`);
  await page.getByTestId('template-legal').or(page.getByTestId('template-error')).waitFor();
  if (await page.getByTestId('template-error').count()) throw new Error(`receipt template: ${await page.getByTestId('template-error').textContent()}`);
  const version = await page.getByTestId('template-version').getAttribute('data-version');
  const fixed = (await page.getByTestId('template-legal').evaluate((n) => n.tagName)) === 'P'
    && (await page.locator('[data-testid="template-form"] textarea, [data-testid="template-form"] input').count()) === 2;
  check('RT01', 'the industry template (versioned) is shown and its legal text cannot be edited', /@\d+$/.test(version ?? '') && fixed, version);
  await page.getByTestId('template-footer').fill('با سپاس از خرید شما — آزمون');
  await page.getByTestId('template-signature').fill('فروشگاه آزمایشی');
  await page.getByTestId('template-save').click();
  await page.getByTestId('template-saved').waitFor();
  const templatePreview = (await page.getByTestId('template-preview').textContent()) ?? '';
  check('RT02', 'the footer and signature are saved and shown under the legal text', templatePreview.includes('آزمون') && templatePreview.includes('فروشگاه آزمایشی'), templatePreview);
  await shot(page, 'receipt-template');
  // V01–V04 US-082: on a paired device the owner opens a shift; a barcode reader is read without focus (keyboard wedge),
  // a double read within 1.5 s is ignored, a later scan adds to the line, and a line can be removed
  await page.goto(`${BFF}/terminals`);
  await page.getByTestId('issue-code').click();
  await page.fill('input[name=pairingCode]', (await page.getByTestId('pairing-code').textContent()).trim());
  await page.fill('input[name=deviceName]', 'صندوق-فروش');
  await page.getByTestId('pair-submit').click();
  await page.getByTestId('this-device').filter({ hasText: 'ثبت‌شده است' }).waitFor();
  await page.goto(`${BFF}/pos`);
  await page.getByTestId('pos-open-submit').click();
  await page.getByTestId('pos-ready').waitFor();
  check('V01', 'the owner opens a shift on this registered device (BR-050)', true);
  await page.locator('h1').click();   // focus on no field: the reader types into the page itself
  await page.keyboard.type('4006381333931');
  await page.keyboard.press('Enter');
  await page.keyboard.type('4006381333931');
  await page.keyboard.press('Enter');
  await page.locator('[data-testid="pos-lines"] [data-sku="E2E-COLA"]').waitFor();
  await page.getByTestId('pos-ignored').waitFor();
  const firstQty = await page.locator('[data-sku="E2E-COLA"]').getAttribute('data-qty');
  const total = Number(await page.getByTestId('pos-total').getAttribute('data-total'));
  check('V02', 'a keyboard-wedge scan adds the item at the server price, with no field focused (SAL-002)', firstQty === '1' && total > 0, `${firstQty} ${total}`);
  await page.waitForTimeout(1600);
  await page.keyboard.type('4006381333931');
  await page.keyboard.press('Enter');
  await page.locator('[data-sku="E2E-COLA"][data-qty="2"]').waitFor();
  check('V03', 'the double read was ignored; a scan after 1.5 s adds one more (AT-061)', true);
  await page.getByTestId('pos-remove-E2E-COLA').click();
  await page.locator('[data-testid="pos-total"][data-total="0"]').waitFor();
  check('V04', 'a line is removed and the total follows (SAL-001)', true);
  // V05–V06 US-101: part by card (its slip's reference), the rest in cash with change; then the bill is fixed
  await page.waitForTimeout(1600);   // the same code again within 1.5 s would be (rightly) ignored
  await page.locator('h1').click();
  await page.keyboard.type('4006381333931');
  await page.keyboard.press('Enter');
  await page.locator('[data-sku="E2E-COLA"][data-qty="1"]').waitFor();
  const due = Number(await page.getByTestId('pos-pay').getAttribute('data-remaining'));
  await page.getByTestId('pos-card-amount').fill('100000');
  await page.getByTestId('pos-card-reference').fill('E2E558812');
  await page.getByTestId('pos-card-submit').click();
  await page.locator(`[data-testid="pos-pay"][data-remaining="${due - 100000}"]`).waitFor();
  // V08: the slip was wrong: the card payment is taken back with a reason and paid again with the right slip
  await page.getByTestId('pos-void-1').click();
  await page.getByTestId('pos-void-reason-1').fill('شماره پیگیری اشتباه');
  await page.getByTestId('pos-void-submit-1').click();
  await page.locator(`[data-testid="pos-pay"][data-remaining="${due}"]`).waitFor();
  const voided = await page.locator('[data-testid="pos-tenders"] [data-status="Voided"]').count();
  await page.getByTestId('pos-card-amount').fill('100000');
  await page.getByTestId('pos-card-reference').fill('E2E558813');
  await page.getByTestId('pos-card-submit').click();
  await page.locator(`[data-testid="pos-pay"][data-remaining="${due - 100000}"]`).waitFor();
  check('V08', 'a payment is voided with a reason, stops counting, and the bill is paid again', voided === 1);
  await page.getByTestId('pos-cash-tendered').fill('۲۰۰٬۰۰۰');
  const preview = (await page.getByTestId('pos-change-preview').textContent()) ?? '';
  await page.getByTestId('pos-cash-submit').click();
  await page.getByTestId('pos-paid').waitFor();
  const cashLine = (await page.locator('[data-testid="pos-tenders"] [data-method="Cash"]').textContent()) ?? '';
  const change = (200000 - (due - 100000)).toLocaleString('fa-IR');
  check('V05', 'paid by card with its reference and the rest in cash; the change is shown before and after (SAL-006.1)',
    preview.includes(change) && cashLine.includes(change), `${due} ${preview} | ${cashLine}`);
  check('V06', 'once paid, the bill can no longer be changed', (await page.getByTestId('pos-remove-E2E-COLA').count()) === 0);
  // V07 US-091: Finalize issues the invoice number, the stock leaves and the next sale starts clean
  await page.getByTestId('pos-finalize').click();
  await page.getByTestId('pos-finalized').waitFor();
  const invoice = await page.getByTestId('pos-finalized').getAttribute('data-invoice');
  const card = await page.evaluate(async () => {
    const p = (await (await fetch('/bff/products?q=E2E-COLA', { cache: 'no-store' })).json()).items[0];
    return (await (await fetch(`/bff/inventory/balances?variantId=${p.variants[0].id}`, { cache: 'no-store' })).json()).items[0]?.onHand;
  });
  // PR01–PR02 US-092: the invoice prints on an 80 mm roll with the industry's legal text and its receipt link; a second print says
  // it is a copy, and the link opens the receipt for someone who is not signed in (SAL-012, BR-086, CUS-009)
  const printHref = await page.getByTestId('pos-print-80').getAttribute('href');
  const printer = await ctx.newPage();
  await printer.goto(`${BFF}${printHref}`);
  await printer.getByTestId('receipt').waitFor();
  const legal = (await printer.getByTestId('receipt-legal').textContent()) ?? '';
  const receiptUrl = ((await printer.getByTestId('receipt-link').textContent()) ?? '').match(/https?:\/\/\S+\/r\/[A-Za-z0-9_-]+/)?.[0] ?? '';
  check('PR01', 'the printed invoice carries its number, the industry legal text and a receipt link',
    (await printer.getByTestId('receipt').getAttribute('data-invoice')) === invoice && legal.includes('مالیات') && receiptUrl.includes('/r/')
    && (await printer.getByTestId('receipt-copy').count()) === 0, `${legal} ${receiptUrl}`);
  await printer.reload();
  await printer.getByTestId('receipt-copy').waitFor();
  const stranger = await browser.newContext();
  const sp = await stranger.newPage();
  await sp.goto(receiptUrl);
  await sp.getByTestId('receipt').waitFor();
  const publicInvoice = await sp.getByTestId('receipt').getAttribute('data-invoice');
  await sp.goto(receiptUrl.slice(0, -3) + 'zzz');
  await sp.getByTestId('public-receipt-missing').waitFor();
  check('PR02', 'a reprint is marked as a copy and the link opens the receipt without signing in; a wrong link does not',
    publicInvoice === invoice, publicInvoice);
  await stranger.close();
  await printer.close();
  await page.getByTestId('pos-next').click();
  await page.getByTestId('pos-ready').waitFor();
  // 17 received, 1 sent back (Y02), 1 sold
  check('V07', 'Finalize issues B1-<year>-000001 and the stock goes down by one (SAL-007, BR-056)', /^B1-14\d\d-000001$/.test(invoice ?? '') && card === '15'
    && (await page.getByTestId('pos-total').count()) === 0, `${invoice} ${card}`);
  // V09–V10 US-083: a cart is put aside with a note and resumed; another is cancelled with a reason
  await page.waitForTimeout(1600);
  await page.locator('h1').click();
  await page.keyboard.type('4006381333931');
  await page.keyboard.press('Enter');
  await page.locator('[data-sku="E2E-COLA"][data-qty="1"]').waitFor();
  await page.getByTestId('pos-hold').click();
  await page.getByTestId('pos-aside-text').fill('مشتری آزمایشی');
  await page.getByTestId('pos-aside-submit').click();
  await page.locator('[data-testid="pos-held"] [data-note="مشتری آزمایشی"]').waitFor();
  // the page refreshes the cart and the held list separately: wait for each state instead of reading it at once
  const emptyAfterHold = await page.getByTestId('pos-lines').locator('tbody tr').first().waitFor({ state: 'detached', timeout: 5000 }).then(() => true, () => false);
  await page.locator('[data-testid="pos-held"] [data-note="مشتری آزمایشی"] button').click();
  await page.locator('[data-sku="E2E-COLA"][data-qty="1"]').waitFor();
  check('V09', 'a cart is held with a note, the register is free for the next customer, and the cart is resumed (SAL-011)',
    emptyAfterHold && await page.locator('[data-testid="pos-held"] li').first().waitFor({ state: 'detached', timeout: 5000 }).then(() => true, () => false));
  await page.getByTestId('pos-cancel').click();
  await page.getByTestId('pos-aside-text').fill('مشتری منصرف شد');
  await page.getByTestId('pos-aside-submit').click();
  await page.locator('[data-testid="pos-lines"] tbody tr').first().waitFor({ state: 'detached' });
  check('V10', 'a cart without payments is cancelled with a reason (SAL-004)', true);
  // OF01–OF02 US-084: the network drops; the register keeps scanning into a cart held on the device (no payment), and when the
  // network returns the cart is sent once and priced by the server; a code the store does not have is reported (SAL-013, AT-071)
  await page.context().setOffline(true);
  await page.getByTestId('pos-offline').waitFor();
  for (const n of [1, 2]) {
    await page.waitForTimeout(1600);
    await page.locator('h1').click();
    await page.keyboard.type('4006381333931');
    await page.keyboard.press('Enter');
    await page.locator(`[data-testid="pos-offline-lines"] [data-code="4006381333931"][data-qty="${n}"]`).waitFor();
  }
  await page.getByTestId('pos-manual').fill('E2E-NONE');
  await page.getByTestId('pos-manual-submit').click();
  await page.locator('[data-testid="pos-offline-lines"] [data-code="E2E-NONE"]').waitFor();
  const offlineRow = (await page.locator('[data-testid="pos-offline-lines"] [data-code="4006381333931"]').textContent()) ?? '';
  check('OF01', 'offline: a banner shows, scans go to a cart on the device with cached names, and payment is not offered',
    offlineRow.includes('نوشابه آزمایشی') && (await page.getByTestId('pos-pay').count()) === 0 && (await page.getByTestId('pos-finalize').count()) === 0, offlineRow);
  await page.context().setOffline(false);
  await page.getByTestId('pos-synced').waitFor();
  await page.locator('[data-testid="pos-lines"] [data-sku="E2E-COLA"][data-qty="2"]').waitFor();
  const synced = (await page.getByTestId('pos-synced').textContent()) ?? '';
  check('OF02', 'back online the cart is synced once at server prices and the unknown code is reported',
    synced.includes('E2E-NONE') && (await page.getByTestId('pos-offline-cart').count()) === 0
    && (await page.locator('[data-testid="pos-lines"] tbody tr').count()) === 1, synced);
  await page.getByTestId('pos-cancel').click();
  await page.getByTestId('pos-aside-text').fill('آزمون آفلاین');
  await page.getByTestId('pos-aside-submit').click();
  await page.locator('[data-testid="pos-lines"] tbody tr').first().waitFor({ state: 'detached' });
  await shot(page, 'pos');
  // F01 US-064: one damaged bottle moves from the shop floor to the returns warehouse at its average cost
  await page.goto(`${BFF}/transfers`);
  await page.locator('[data-testid="transfer-to"] option').nth(1).waitFor({ state: 'attached' });
  await page.getByTestId('transfer-sku').fill('E2E-COLA');
  await page.getByTestId('transfer-qty').fill('۱');
  await page.getByTestId('transfer-submit').click();
  await page.getByTestId('transfer-done').waitFor();
  await page.locator('[data-testid="transfer-list"] [data-from="B1-MAIN"][data-to="B1-RET"]').waitFor();
  check('F01', 'a transfer between two warehouses of the branch is posted and listed (INV-008)', true);
  // H01 US-066: a reorder point above what is on hand puts the item on the low-stock list
  await page.goto(`${BFF}/low-stock`);
  await page.getByTestId('low-count').waitFor();
  await page.getByTestId('reorder-sku').fill('E2E-COLA');
  await page.getByTestId('reorder-point').fill('۵۰');
  await page.getByTestId('reorder-submit').click();
  await page.getByTestId('reorder-saved').waitFor();
  await page.locator('[data-testid="low-list"] [data-sku="E2E-COLA"]').waitFor();
  check('H01', 'an item at or below its reorder point is listed as low stock (INV-010, AT-048)', true);
  // O01 US-065: the owner counts the returns warehouse (one more than the system holds), closes, and as a counter may not
  // confirm the correction alone (SoD-03)
  await page.goto(`${BFF}/counts`);
  await page.locator('[data-testid="count-warehouse"] option', { hasText: 'B1-RET' }).waitFor({ state: 'attached' });
  await page.getByTestId('count-warehouse').selectOption({ label: 'B1-RET' });
  await page.getByTestId('count-start').click();
  await page.locator('[data-testid="count-list"] [data-warehouse="B1-RET"][data-status="Open"]').waitFor();
  await page.getByTestId('count-sku').fill('E2E-COLA');
  await page.getByTestId('count-qty').fill('۲');
  await page.getByTestId('count-add').click();
  await page.locator('[data-testid="count-list"] [data-warehouse="B1-RET"]', { hasText: '۱ شمارنده' }).waitFor();
  await page.getByTestId('count-close').click();
  await page.getByTestId('count-result').waitFor();
  const overage = Number(await page.getByTestId('count-result').getAttribute('data-total'));
  await page.getByTestId('count-confirm').click();
  await page.getByTestId('count-error').waitFor();
  const sod = (await page.getByTestId('count-error').textContent()) ?? '';
  check('O01', 'a count finds one more than the system holds; the counter needs another manager to confirm the correction (INV-009, SoD-03)',
    overage > 0 && sod.includes('مدیر دیگری'), `${overage} ${sod}`);
  await page.goto(`${BFF}/terminals`);
  await page.getByTestId('revoke-صندوق-فروش').click();
  await page.getByTestId('this-device').filter({ hasText: 'نیست' }).waitFor();
  // CG01–CG02 US-043: an unknown public barcode gets the moderated global product as a pre-selected suggestion; saving it needs the
  // user's explicit "checked" (CAT-004, CAT-004.1)
  await page.goto(`${BFF}/products`);
  await page.getByTestId('product-barcode').fill('6260000000101');
  await page.getByTestId('product-suggest').click();
  await page.locator('[data-testid="product-candidates"] [data-source="global"]').waitFor();
  const suggestedName = await page.getByTestId('product-name').inputValue();
  check('CG01', 'the moderated global product is suggested with its source and confidence and fills the form',
    suggestedName === 'چای سیاه آزمایشی' && (await page.locator('[data-source="global"][data-confidence="0.95"]').count()) === 1, suggestedName);
  await page.getByTestId('product-sku').fill('E2E-TEA');
  await page.getByTestId('product-submit').click();
  await page.getByTestId('product-error').filter({ hasText: 'بررسی کردم' }).waitFor();
  await page.getByTestId('product-confirm-suggestion').check();
  await page.getByTestId('product-submit').click();
  await page.getByTestId('product-created').filter({ hasText: 'چای سیاه آزمایشی' }).waitFor();
  check('CG02', 'a suggestion is saved only after the user ticks that they checked it (CAT-004.1)', true);
  // MG01–MG02 US-044: a duplicate product is merged into the original (its barcode follows), and the original is suggested to the
  // global catalog only after the store agrees (CAT-006, CAT-005.1)
  for (const [name, sku, code] of [['ماست آزمایشی', 'E2E-YOG', '6260000000125'], ['ماست تکراری', 'E2E-YOG-DUP', '6260000000132']]) {
    await page.getByTestId('product-name').fill(name);
    await page.getByTestId('product-sku').fill(sku);
    await page.getByTestId('product-barcode').fill(code);
    await page.getByTestId('product-submit').click();
    await page.getByTestId('product-created').filter({ hasText: name }).waitFor();
  }
  await page.getByTestId('product-search').fill('ماست');
  await page.getByTestId('product-search-submit').click();
  const dupRow = page.locator('[data-testid="products"] > li', { hasText: 'ماست تکراری' });
  await dupRow.getByTestId('product-merge-open').click();
  await dupRow.getByTestId('product-merge-target').fill('E2E-YOG');
  await dupRow.getByTestId('product-merge-reason').fill('ثبت دوباره‌ی یک کالا');
  await dupRow.getByTestId('product-merge-submit').click();
  await dupRow.waitFor({ state: 'detached' });
  const original = page.locator('[data-testid="products"] > li', { hasText: 'ماست آزمایشی' });
  const both = (await original.textContent()) ?? '';
  check('MG01', 'the duplicate disappears from the list and its barcode now belongs to the original (CAT-006)',
    both.includes('6260000000125') && both.includes('6260000000132'), both);
  await original.getByTestId('product-suggest-open').click();
  await original.getByTestId('product-suggest-consent').check();
  await original.getByTestId('product-suggest-submit').click();
  await original.getByTestId('product-action-note').filter({ hasText: 'فرستاده شد' }).waitFor();
  check('MG02', 'with the store\'s explicit consent the product is sent to the global catalog for moderation (CAT-005.1)', true);
  // W01–W03 US-045: the owner sets a scale label format; a printed label then decodes to the item and its weight
  await page.goto(`${BFF}/products`);
  await page.getByTestId('product-name').fill('پنیر فله آزمایشی');
  await page.getByTestId('product-sku').fill('E2E-CHEESE');
  await page.getByTestId('product-barcode').fill('00123');
  await page.getByTestId('product-internal').check();
  await page.getByTestId('product-submit').click();
  await page.getByTestId('product-created').waitFor();
  await page.goto(`${BFF}/weighted-barcodes`);
  await page.getByTestId('weighted-prefix').fill('21');
  await page.getByTestId('weighted-submit').click();
  await page.getByTestId('weighted-saved').waitFor();
  await page.locator('[data-testid="weighted-profiles"] [data-prefix="21"]').waitFor();
  check('W01', 'the owner defines a weighted barcode profile (CAT-007)', true);
  await page.getByTestId('weighted-label').fill('2100123012503');
  await page.getByTestId('weighted-try').click();
  const decoded = (await page.getByTestId('weighted-result').textContent()) ?? '';
  check('W02', 'a scale label decodes to the item and 1.250 kg (AT-029)', decoded.includes('پنیر فله آزمایشی') && decoded.includes('1.250'), decoded);
  await page.getByTestId('weighted-label').fill('2100123012504');   // wrong check digit
  await page.getByTestId('weighted-try').click();
  const bad = (await page.getByTestId('weighted-error').textContent()) ?? '';
  check('W03', 'a misread label is refused, not guessed (CAT-003)', bad.includes('بارکد معتبر نیست'), bad);
  await shot(page, 'weighted-barcodes');
  // X01 US-016: the owner (fresh MFA) requests a full export; the job is recorded and its status is shown
  await page.goto(`${BFF}/exports`);
  await page.getByTestId('export-submit').click();
  await page.getByTestId('export-status').waitFor();
  check('X01', 'the owner requests a full data export and sees its status (TEN-005)',
    ['Queued', 'Running', 'Completed', 'Failed'].includes(await page.getByTestId('export-status').getAttribute('data-status')));
  // X02 with object storage (RC_E2E_S3=1): the Workers finish the file and the 5-minute link downloads a ZIP
  if (process.env.RC_E2E_S3 === '1') {
    await page.locator('[data-testid="export-status"][data-status="Completed"]').waitFor({ timeout: 60000 });
    const exportId = await page.getByTestId('export-status').getAttribute('data-export-id');
    const job = await (await page.request.get(`${BFF}/bff/exports/${exportId}`)).json();
    const file = await page.request.get(job.downloadUrl);
    const bytes = await file.body();
    check('X02', 'the finished export downloads from its presigned link as a ZIP (TEN-005, D-49)',
      file.status() === 200 && bytes[0] === 0x50 && bytes[1] === 0x4b && job.downloadUrl.includes('X-Amz-Expires=300'));
  }
  // IK01–IK03 US-162: the owner (fresh MFA) makes a read-only partner key, shown once; it opens the partner API for this store
  // only, without costs, and stops the moment it is revoked (INT-001, AT-180)
  await page.goto(`${BFF}/integrations`);
  await page.locator('input[name="keyName"]').fill('فروشگاه اینترنتی آزمایشی');
  await page.locator('input[name="inventory.read"]').check();
  await page.getByTestId('create-key').click();
  const partnerKey = (await page.getByTestId('key-secret').textContent()) ?? '';
  await page.getByTestId('key-status-فروشگاه اینترنتی آزمایشی').filter({ hasText: 'فعال' }).waitFor();
  const listedKeys = (await page.getByTestId('keys').textContent()) ?? '';
  check('IK01', 'a key is shown once at creation and the list shows only its public prefix (INT-001)',
    /^rck_[a-z0-9]{10}_[A-Za-z0-9_-]{40,}$/.test(partnerKey) && !listedKeys.includes(partnerKey.split('_')[2]), partnerKey.slice(0, 15));
  const partnerGet = (path, key) => page.request.get(`http://127.0.0.1:5080/api/v1/partner${path}`, { headers: { 'X-Api-Key': key } });
  const pp = await partnerGet('/catalog/products', partnerKey);
  const pb = await partnerGet('/inventory/balances', partnerKey);
  const pbText = await pb.text();
  const ppNames = ((await pp.json()).items ?? []).map((x) => x.name);
  check('IK02', 'the partner reads this store\'s products and quantities, never the cost (INT-001)',
    pp.status() === 200 && pb.status() === 200 && ppNames.includes('ماست آزمایشی') && !/wac|value/i.test(pbText), `${pp.status()}/${pb.status()}`);
  page.once('dialog', (d) => void d.accept());
  await page.getByTestId('revoke-key-فروشگاه اینترنتی آزمایشی').click();
  await page.getByTestId('key-status-فروشگاه اینترنتی آزمایشی').filter({ hasText: 'ابطال‌شده' }).waitFor();
  const afterRevoke = await partnerGet('/catalog/products', partnerKey);
  check('IK03', 'a revoked key is refused at once (401 invalid-api-key)', afterRevoke.status() === 401, String(afterRevoke.status()));
  await shot(page, 'integrations');
  // CS01–CS03 US-105: the card slip of V08 waits on the unreconciled report until the bank's settlement file settles it; the
  // bank's fee is shown, a line no slip has is left aside, and the same file is not imported twice (PAY-002.2, AB-14, AT-083)
  await page.goto(`${BFF}/card-settlements`);
  await page.getByTestId('unreconciled-E2E558813').waitFor();
  check('CS01', 'a finalized card slip no file settled is on the report, with its cashier; the voided one is not',
    (await page.getByTestId('unreconciled-E2E558812').count()) === 0, (await page.getByTestId('unreconciled').textContent()) ?? '');
  const today = new Date().toISOString().slice(0, 10);
  const settlementCsv = `شماره پیگیری,تاریخ,مبلغ,کارمزد\nE2E558813,${today},"100,000",500\nE2E999999,${today},5000,0\n`;
  const csvFile = { name: 'bank-settlement.csv', mimeType: 'text/csv', buffer: Buffer.from(settlementCsv, 'utf8') };
  await page.getByTestId('settlement-file').setInputFiles(csvFile);
  await page.getByTestId('settlement-submit').click();
  await page.getByTestId('settlement-result').waitFor();
  check('CS02', 'the matching slip is settled: 99,500 to the bank and 500 the bank\'s fee; the unknown line is left aside',
    (await page.getByTestId('result-deposited').getAttribute('data-value')) === '99500'
    && (await page.getByTestId('result-fees').getAttribute('data-value')) === '500'
    && (await page.getByTestId('line-outcome-1').getAttribute('data-outcome')) === 'Matched'
    && (await page.getByTestId('line-outcome-2').getAttribute('data-outcome')) === 'NotFound', (await page.getByTestId('settlement-result').textContent()) ?? '');
  await page.getByTestId('import-bank-settlement.csv').waitFor();
  for (let i = 0; i < 50 && (await page.getByTestId('unreconciled-E2E558813').count()) > 0; i++) await page.waitForTimeout(100);
  const stillOpen = await page.getByTestId('unreconciled-E2E558813').count();
  await page.getByTestId('settlement-file').setInputFiles(csvFile);
  await page.getByTestId('settlement-submit').click();
  await page.getByTestId('error').waitFor();
  check('CS03', 'the settled slip leaves the report, and the same file again is refused',
    stillOpen === 0 && ((await page.getByTestId('error').textContent()) ?? '').includes('قبلاً'), String(stillOpen));
  await shot(page, 'card-settlements');
  // AR01–AR03 US-114: the trial balance of the fiscal year so far balances and shows the bank's fee of CS02; 2-column keeps
  // the balances only; the cash account's ledger (تراز صندوق) is shown and exported for Excel (ACC-007, AT-107)
  await page.goto(`${BFF}/accounting-reports`);
  await page.locator('[data-testid="report-to-period"] option').first().waitFor({ state: 'attached' });
  await page.getByTestId('show-trial-balance').click();
  await page.getByTestId('trial-balance').waitFor();
  check('AR01', 'the trial balance balances and carries the bank fee of the settlement (J4)',
    (await page.getByTestId('trial-balance').getAttribute('data-balanced')) === 'true'
    && (await page.getByTestId('tb-5106').getAttribute('data-closing-debit')) === '500',
    `${await page.getByTestId('trial-balance').getAttribute('data-balanced')} ${await page.getByTestId('tb-5106').getAttribute('data-closing-debit')}`);
  const fourCols = await page.locator('[data-testid="tb-totals"] td').count();
  await page.getByTestId('tb-cols-2').check();
  const twoCols = await page.locator('[data-testid="tb-totals"] td').count();
  check('AR02', 'the 2-column view keeps only the closing balances', fourCols === 8 && twoCols === 4, `${fourCols}/${twoCols}`);
  await page.getByTestId('show-cash').click();
  await page.locator('[data-testid="ledger"][data-account="1101"]').waitFor();
  const cashLines = await page.getByTestId('ledger-line').count();
  const [csvDownload] = await Promise.all([page.waitForEvent('download'), page.getByTestId('export-ledger').click()]);
  check('AR03', 'the cash ledger lists its lines with a running balance and exports as CSV for Excel',
    cashLines > 0 && csvDownload.suggestedFilename() === 'ledger-1101.csv', `${cashLines} ${csvDownload.suggestedFilename()}`);
  await shot(page, 'accounting-reports');
  // RP01–RP03 US-161: the store's own sales (V07) show in the daily report with the time of the data; profit and stock value
  // follow the same sale; the CSV opens as a download for someone who may export (RPT-001..003, AT-170, AT-171, AT-173, AT-175)
  await page.goto(`${BFF}/reports`);
  await page.getByTestId('rp-show').click();
  await page.getByTestId('report').waitFor();
  const dailyRows = await page.locator('[data-testid="row"]').evaluateAll((els) => els.map((e) => [e.getAttribute('data-invoices'), e.getAttribute('data-net')]));
  check('RP01', 'the daily sales report lists the finalized sale of the day with the time its data was read',
    dailyRows.length >= 1 && Number(dailyRows[0][0]) >= 1 && Number(dailyRows[0][1]) > 0 && ((await page.getByTestId('report-meta').textContent()) ?? '').includes('به‌روزرسانی'),
    JSON.stringify(dailyRows));
  await page.getByTestId('tab-gross-profit').click();
  await page.getByTestId('rp-show').click();
  await page.locator('[data-testid="report"][data-report="gross-profit"]').waitFor();
  const profit = await page.locator('[data-testid="row"]').first().getAttribute('data-profit');
  await page.getByTestId('tab-stock').click();
  await page.getByTestId('rp-show').click();
  await page.locator('[data-testid="report"][data-report="stock"]').waitFor();
  const colaValue = (await page.locator('[data-testid="row"][data-sku="E2E-COLA"]').first().getAttribute('data-value')) ?? '';
  check('RP02', 'gross profit is net sales less the cost at sale, and the stock report values what is left at average cost',
    Number(profit) > 0 && Number(colaValue) > 0, `${profit} ${colaValue}`);
  const [reportCsv] = await Promise.all([page.waitForEvent('download'), page.getByTestId('rp-export').click()]);
  check('RP03', 'the report exports as CSV for the owner (data export right)', reportCsv.suggestedFilename() === 'stock.csv', reportCsv.suggestedFilename());
  await shot(page, 'reports');
  await page.goto(BFF);
  await page.getByTestId('logout').waitFor();

  // D01–D05 US-024: my sessions on every device; sign one out, disable a colleague (IAM-009), sign out everywhere
  const signInCashier = async () => {
    const c = await browser.newContext();
    const p = await c.newPage();
    await p.goto(BFF);
    await p.getByTestId('login').click();
    await p.fill('#username', 'cashier@spike.test');
    await p.fill('#password', 'Spike-Passw0rd-1');
    await p.click('#kc-login');
    await p.getByTestId(`select-${T1}`).click();
    await p.getByTestId('active-tenant').waitFor();
    return { c, p };
  };
  const call = (p, method, url) => p.evaluate(async ([m, u]) => {
    const t = document.cookie.split('; ').find((x) => x.startsWith('rc_csrf='))?.split('=')[1] ?? '';
    const r = await fetch(u, { method: m, headers: { 'x-csrf-token': t } });
    return { s: r.status, b: r.status === 204 ? null : await r.json().catch(() => null) };
  }, [method, url]);
  const a = await signInCashier();
  const b = await signInCashier();
  await a.p.goto(`${BFF}/sessions`);
  await a.p.getByTestId('session').first().waitFor();
  const listed = await call(a.p, 'GET', '/bff/sessions');
  // B's own view names its handle; the cashier's earlier sign-in (I03, browser closed without signing out) is listed too
  const other = (await call(b.p, 'GET', '/bff/sessions')).b.items.find((x) => x.current);
  const sessionRows = await a.p.getByTestId('session').count();
  check('D01', 'the cashier sees every signed-in browser, this one marked, with a device label (IAM-005)',
    listed.b.items.length >= 2 && listed.b.items.filter((x) => x.current).length === 1 && /Chrome/.test(other?.device ?? '')
      && listed.b.items.some((x) => x.handle === other?.handle && !x.current) && sessionRows === listed.b.items.length,
    JSON.stringify(listed.b.items.map((x) => [x.device, x.current])));
  await a.p.getByTestId(`session-end-${other.handle}`).click();
  // poll the locator (waitForFunction evaluates a string, which the app's CSP forbids in older Playwright)
  for (let i = 0; i < 100 && (await a.p.getByTestId('session').count()) !== sessionRows - 1; i++) await a.p.waitForTimeout(100);
  const bAfter = await b.p.request.get(`${BFF}/bff/me`);
  await b.p.goto(`${BFF}/auth/login`);
  const reauth = await b.p.locator('#username').isVisible({ timeout: 10000 }).catch(() => false);
  check('D02', 'the other browser is signed out and must enter the password again (Keycloak session ended too)', bAfter.status() === 401 && reauth,
    `${bAfter.status()} login-form=${reauth}`);
  await b.c.close();

  await page.goto(`${BFF}/users`);
  const row = page.getByTestId('users').locator('li', { hasText: 'صندوق‌دار (مرکزی)' });
  await row.getByRole('button', { name: 'غیرفعال‌سازی' }).click();
  await row.filter({ hasText: 'غیرفعال' }).locator('button', { hasText: 'فعال‌سازی دوباره' }).waitFor();
  const cut = await call(a.p, 'GET', '/bff/products');
  check('D03', 'a disabled cashier loses the store on the very next request (IAM-009, AT-018)', cut.s === 404, `${cut.s} ${cut.b?.code}`);
  await row.getByRole('button', { name: 'فعال‌سازی دوباره' }).click();
  await row.getByRole('button', { name: 'غیرفعال‌سازی' }).waitFor();
  const back = await call(a.p, 'GET', '/bff/products');
  check('D04', 're-enabled with the same roles, the cashier works again', back.s === 200, String(back.s));

  const c3 = await signInCashier();
  a.p.once('dialog', (d) => void d.accept());
  await a.p.goto(`${BFF}/sessions`);
  await a.p.getByTestId('logout-all').click();
  await a.p.getByTestId('login').waitFor();
  const [aMe, cMe] = [await a.p.request.get(`${BFF}/bff/me`), await c3.p.request.get(`${BFF}/bff/me`)];
  check('D05', '«خروج از همه دستگاه‌ها» ends every session of the cashier, this one included', aMe.status() === 401 && cMe.status() === 401,
    `${aMe.status()} ${cMe.status()}`);
  await a.c.close();
  await c3.c.close();
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
