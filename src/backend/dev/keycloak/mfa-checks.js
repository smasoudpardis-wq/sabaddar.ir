// US-022 (D12): verifies the level-of-authentication flow of merchant-realm.json against a real Keycloak 26.
// The API (MfaPolicy) relies on exactly these behaviours: acr=pwd|mfa, enrolment for users without a second factor,
// auth_time moving on step-up, a fresh second factor on every acr=mfa request, acr surviving refresh, and passkeys.
// Usage: KC=http://127.0.0.1:8080 NODE_PATH=$(npm root -g) node mfa-checks.js
// WebAuthn needs a host name as relying-party id (an IP is not allowed), so the passkey checks use "localhost".
const { chromium } = require('playwright');
const crypto = require('crypto');
const http = require('http');

const KC = process.env.KC || 'http://127.0.0.1:8080';
const KC_NAMED = KC.replace('127.0.0.1', 'localhost');
const CLIENT = 'rc-bff-merchant', SECRET = 'dev-only-secret-change-me', PASS = 'Spike-Passw0rd-1';
const REDIRECT = 'http://127.0.0.1:3000/auth/callback';
let onCallback;
http.createServer((req, res) => { onCallback?.(new URL(req.url, 'http://x').searchParams); res.end('ok'); }).listen(3000, '127.0.0.1');
const jwt = (t) => JSON.parse(Buffer.from(t.split('.')[1], 'base64url').toString());
let failures = 0;
const check = (name, cond, detail = '') => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`); if (!cond) failures++; };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function totp(base32, t = Date.now()) {
  const A = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'; let bits = '';
  for (const ch of base32.replace(/[\s=]/g, '').toUpperCase()) bits += A.indexOf(ch).toString(2).padStart(5, '0');
  const key = Buffer.from(bits.match(/.{8}/g).map((b) => parseInt(b, 2)));
  const ctr = Buffer.alloc(8); ctr.writeBigUInt64BE(BigInt(Math.floor(t / 30000)));
  const h = crypto.createHmac('sha1', key).update(ctr).digest(); const o = h[h.length - 1] & 15;
  return String((h.readUInt32BE(o) & 0x7fffffff) % 1e6).padStart(6, '0');
}
/** Waits for the next 30 s TOTP window: Keycloak refuses a code that was already used. */
const nextWindow = () => sleep(30000 - (Date.now() % 30000) + 500);

async function token(base, form) {
  const r = await fetch(`${base}/realms/merchant/protocol/openid-connect/token`, { method: 'POST', body: new URLSearchParams(form) });
  return r.json();
}

/** One authorization request in an existing browser context; `act` drives whatever pages Keycloak shows. */
async function authorize(page, base, extra, act) {
  const verifier = crypto.randomBytes(32).toString('base64url');
  const q = new URLSearchParams({ client_id: CLIENT, redirect_uri: REDIRECT, response_type: 'code', scope: 'openid', state: 's',
    code_challenge: crypto.createHash('sha256').update(verifier).digest('base64url'), code_challenge_method: 'S256', ...extra });
  const got = new Promise((r) => { onCallback = r; });
  await page.goto(`${base}/realms/merchant/protocol/openid-connect/auth?${q}`);
  const seen = act ? await act(page) : {};
  const sp = await Promise.race([got, sleep(20000).then(() => null)]);
  if (!sp?.get('code')) return { ...seen, error: sp?.get('error') ?? 'no callback' };
  const t = await token(base, { grant_type: 'authorization_code', client_id: CLIENT, client_secret: SECRET, code: sp.get('code'), redirect_uri: REDIRECT, code_verifier: verifier });
  return { ...seen, tokens: t, at: jwt(t.access_token) };
}

const password = (user) => async (page) => { await page.fill('#username', user); await page.fill('#password', PASS); await page.click('#kc-login'); return {}; };

(async () => {
  const browser = await chromium.launch();
  try {
    // --- TOTP user (cashier@spike.test: no second factor configured in the realm file)
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    const plain = await authorize(page, KC, {}, password('cashier@spike.test'));
    check('M1 password login yields acr=pwd (level 1)', plain.at?.acr === 'pwd', plain.at?.acr);

    let secret;
    await sleep(1100); // auth_time has second resolution
    const enrol = await authorize(page, KC, { acr_values: 'mfa' }, async (p) => {
      await p.waitForLoadState();
      const execution = new URL(p.url()).searchParams.get('execution');
      await p.click('#mode-manual');
      secret = (await p.locator('#kc-totp-secret-key').textContent()).trim();
      await p.fill('#totp', totp(secret)); await p.fill('#userLabel', 'mfa-checks'); await p.click('#saveTOTPBtn');
      return { execution };
    });
    check('M2 acr_values=mfa for a user without a second factor starts TOTP enrolment', enrol.execution === 'CONFIGURE_TOTP', enrol.execution);
    check('M3 after enrolment the token has acr=mfa', enrol.at?.acr === 'mfa', enrol.at?.acr);
    check('M4 step-up moves auth_time forward (API freshness check, ABAC-05)', enrol.at?.auth_time > plain.at?.auth_time,
      `${plain.at?.auth_time} → ${enrol.at?.auth_time}`);

    const refreshed = await token(KC, { grant_type: 'refresh_token', client_id: CLIENT, client_secret: SECRET, refresh_token: enrol.tokens.refresh_token });
    const rat = jwt(refreshed.access_token);
    check('M5 refresh keeps acr=mfa and auth_time', rat.acr === 'mfa' && rat.auth_time === enrol.at.auth_time, `${rat.acr} ${rat.auth_time}`);

    const sso = await authorize(page, KC, {}, null);
    check('M6 a later request without acr_values gets acr=pwd (the BFF must ask for mfa when needed)', sso.at?.acr === 'pwd', sso.at?.acr);

    await nextWindow();
    const again = await authorize(page, KC, { acr_values: 'mfa' }, async (p) => {
      await p.waitForLoadState();
      const prompted = (await p.locator('#otp').count()) === 1;
      if (prompted) { await p.fill('#otp', totp(secret)); await p.click('#kc-login'); }
      return { prompted };
    });
    check('M7 every acr=mfa request asks for the second factor again (level 2 max-age 0)', again.prompted && again.at?.acr === 'mfa');

    const wrong = await authorize(page, KC, { acr_values: 'mfa' }, async (p) => {
      await p.waitForLoadState(); await p.fill('#otp', '000000'); await p.click('#kc-login'); await p.waitForLoadState();
      return { stillOnOtp: (await p.locator('#otp').count()) === 1 };
    });
    check('M8 a wrong TOTP code does not complete the login', wrong.stillOnOtp && !wrong.at, wrong.error);
    await ctx.close();

    // --- Passkey user (passkey@spike.test has the webauthn-register required action) with a virtual authenticator
    const pctx = await browser.newContext();
    const pp = await pctx.newPage();
    const cdp = await pctx.newCDPSession(pp);
    await cdp.send('WebAuthn.enable');
    const { authenticatorId } = await cdp.send('WebAuthn.addVirtualAuthenticator', { options: {
      protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });
    await authorize(pp, KC_NAMED, {}, async (p) => {
      await password('passkey@spike.test')(p);
      await p.locator('#registerWebAuthn').click();
      return {};
    });
    const creds = await cdp.send('WebAuthn.getCredentials', { authenticatorId });
    check('M9 passkey registration through the webauthn-register required action', creds.credentials.length === 1);
    const pk = await authorize(pp, KC_NAMED, { acr_values: 'mfa' }, async (p) => {
      await p.waitForLoadState();
      const otp = await p.locator('#otp').count();
      await p.locator('#authenticateWebAuthnButton').click();
      return { otp };
    });
    check('M10 a user with a passkey steps up with WebAuthn (no TOTP) and gets acr=mfa', pk.otp === 0 && pk.at?.acr === 'mfa', `${pk.at?.acr} ${pk.error ?? ''}`);
  } catch (e) {
    check('M99 unexpected error', false, String(e));
  } finally {
    await browser.close();
  }
  console.log(failures ? `\nUS-022 MFA: ${failures} CHECK(S) FAILED` : '\nUS-022 MFA: ALL CHECKS PASSED');
  process.exit(failures ? 1 : 0);
})();
