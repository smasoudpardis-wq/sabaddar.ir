import { NextResponse } from 'next/server';
import * as client from 'openid-client';
import { config, cookieNames } from '@/lib/config';
import { oidc } from '@/lib/oidc';
import { randomId, savePending } from '@/lib/session';

export const dynamic = 'force-dynamic';

export async function GET() {
  const codeVerifier = client.randomPKCECodeVerifier();
  const state = randomId(16);
  const nonce = randomId(16);
  const pendingId = savePending({ state, nonce, codeVerifier, createdAt: Date.now() });
  const url = client.buildAuthorizationUrl(await oidc(), {
    redirect_uri: new URL('/auth/callback', config.appUrl()).toString(),
    scope: 'openid',
    state,
    nonce,
    code_challenge: await client.calculatePKCECodeChallenge(codeVerifier),
    code_challenge_method: 'S256',
  });
  const res = NextResponse.redirect(url);
  res.cookies.set(cookieNames().pending, pendingId, {
    httpOnly: true, secure: config.secureCookies(), sameSite: 'lax', path: '/', maxAge: 600,
  });
  return res;
}
