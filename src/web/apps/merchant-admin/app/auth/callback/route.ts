import { NextRequest, NextResponse } from 'next/server';
import * as client from 'openid-client';
import { config, cookieNames } from '@/lib/config';
import { oidc } from '@/lib/oidc';
import { createSession, takePending } from '@/lib/session';
import { problem } from '@/lib/http';

export const dynamic = 'force-dynamic';

export async function GET(req: NextRequest) {
  const names = cookieNames();
  const pending = takePending(req.cookies.get(names.pending)?.value);
  if (!pending) return problem(400, 'login-expired', 'Login attempt expired or unknown');

  // Rebuild the callback URL on the configured public origin (never trust the Host header).
  const current = new URL(req.nextUrl.pathname + req.nextUrl.search, config.appUrl());
  let tokens: client.TokenEndpointResponse & client.TokenEndpointResponseHelpers;
  try {
    tokens = await client.authorizationCodeGrant(await oidc(), current, {
      pkceCodeVerifier: pending.codeVerifier,
      expectedState: pending.state,
      expectedNonce: pending.nonce,
      idTokenExpected: true,
    });
  } catch {
    return problem(400, 'login-failed', 'Login could not be completed');
  }

  const session = createSession({
    accessToken: tokens.access_token,
    refreshToken: tokens.refresh_token ?? '',
    accessExpiresAt: Math.floor(Date.now() / 1000) + (tokens.expires_in ?? 300),
    idToken: tokens.id_token,
  });
  const res = NextResponse.redirect(new URL('/', config.appUrl()));
  const base = { secure: config.secureCookies(), sameSite: 'lax' as const, path: '/' };
  res.cookies.set(names.session, session.id, { ...base, httpOnly: true });   // AUTH-07
  res.cookies.set(names.csrf, session.csrf, { ...base, httpOnly: false });   // double-submit token, readable by our JS
  res.cookies.delete(names.pending);
  return res;
}
