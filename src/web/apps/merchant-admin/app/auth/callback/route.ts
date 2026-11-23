import { NextRequest, NextResponse } from 'next/server';
import * as client from 'openid-client';
import { config, cookieNames } from '@/lib/config';
import { oidc } from '@/lib/oidc';
import { createSession, destroySession, deviceLabel, saveSession, takePending } from '@/lib/session';
import { callApi, currentSession, problem } from '@/lib/http';

export const dynamic = 'force-dynamic';

export async function GET(req: NextRequest) {
  const names = cookieNames();
  const pending = await takePending(req.cookies.get(names.pending)?.value);
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
  const claims = tokens.claims();
  const acr = claims?.acr as string | undefined;
  if (pending.acr === 'mfa' && acr !== 'mfa') return problem(400, 'mfa-not-completed', 'Second factor was not completed');

  // TM-006: a new session id after every authentication (login and step-up); the old session is destroyed.
  const previous = await currentSession(req);
  if (previous) await destroySession(previous.id);
  const session = await createSession({
    accessToken: tokens.access_token,
    refreshToken: tokens.refresh_token ?? '',
    accessExpiresAt: Math.floor(Date.now() / 1000) + (tokens.expires_in ?? 300),
    idToken: tokens.id_token,
    acr,
    activeTenantId: previous?.activeTenantId,
    subject: claims ? `${claims.iss}\n${claims.sub}` : undefined,     // US-024 session index
    device: (req.cookies.get(names.device)?.value ? 'صندوق · ' : '') + deviceLabel(req.headers.get('user-agent')),
  });

  // Step-up started from a tenant selection: select it now; the API re-checks membership and MFA (BR-001, IAM-003).
  if (pending.tenantId) {
    const probe = { ...session, activeTenantId: undefined };
    const r = await callApi(probe, 'POST', '/api/v1/me/active-tenant', { tenantId: pending.tenantId });
    if (r.status === 204) {
      session.activeTenantId = pending.tenantId;
      await saveSession(session);
    }
  }

  const res = NextResponse.redirect(new URL(pending.returnTo, config.appUrl()));
  const base = { secure: config.secureCookies(), sameSite: 'lax' as const, path: '/' };
  res.cookies.set(names.session, session.id, { ...base, httpOnly: true });   // AUTH-07
  res.cookies.set(names.csrf, session.csrf, { ...base, httpOnly: false });   // double-submit token, readable by our JS
  res.cookies.delete(names.pending);
  return res;
}
