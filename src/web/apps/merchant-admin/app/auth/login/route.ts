import { NextRequest, NextResponse } from 'next/server';
import * as client from 'openid-client';
import { config, cookieNames } from '@/lib/config';
import { oidc } from '@/lib/oidc';
import { randomId, savePending } from '@/lib/session';
import { isUuid, safeReturnTo } from '@/lib/http';

export const dynamic = 'force-dynamic';

/**
 * Starts Authorization Code + PKCE. `?acr=mfa` asks Keycloak for level 2 (TOTP or passkey; a user without one is sent
 * to enrolment) — used when the API answers mfa-required or step-up-required (US-022). `?tenant=` is selected right
 * after the callback; `?returnTo=` must be a path of this app.
 */
export async function GET(req: NextRequest) {
  const q = req.nextUrl.searchParams;
  const acr = q.get('acr') === 'mfa' ? 'mfa' : undefined;
  const tenantId = isUuid(q.get('tenant')) ? q.get('tenant')! : undefined;
  const codeVerifier = client.randomPKCECodeVerifier();
  const state = randomId(16);
  const nonce = randomId(16);
  const pendingId = await savePending({ state, nonce, codeVerifier, acr, tenantId, returnTo: safeReturnTo(q.get('returnTo')), createdAt: Date.now() });
  const params: Record<string, string> = {
    redirect_uri: new URL('/auth/callback', config.appUrl()).toString(),
    scope: 'openid',
    state,
    nonce,
    code_challenge: await client.calculatePKCECodeChallenge(codeVerifier),
    code_challenge_method: 'S256',
  };
  if (acr) params.acr_values = acr; // Keycloak level 2 has max-age 0: the second factor is asked every time
  const res = NextResponse.redirect(client.buildAuthorizationUrl(await oidc(), params));
  res.cookies.set(cookieNames().pending, pendingId, {
    httpOnly: true, secure: config.secureCookies(), sameSite: 'lax', path: '/', maxAge: 600,
  });
  return res;
}
