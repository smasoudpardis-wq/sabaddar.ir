import * as client from 'openid-client';
import { config } from './config';

let cached: Promise<client.Configuration> | undefined;

/** OIDC discovery against Keycloak (confidential client, PKCE S256 — ADR-003, verified by SP-01). */
export function oidc(): Promise<client.Configuration> {
  cached ??= (async () => {
    const issuer = new URL(config.issuer());
    const options = issuer.protocol === 'http:' ? { execute: [client.allowInsecureRequests] } : undefined;
    return client.discovery(issuer, config.clientId(), config.clientSecret(), undefined, options);
  })().catch((e) => {
    cached = undefined;
    throw e;
  });
  return cached;
}

/** Revokes a refresh token. Keycloak 26 then ends the whole user session (checked through the admin API, US-024). */
export const revokeAtIdp = async (refreshToken: string) => client.tokenRevocation(await oidc(), refreshToken);
