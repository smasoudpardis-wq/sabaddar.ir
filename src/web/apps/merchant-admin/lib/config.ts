// Server-only configuration (never imported by client components). Secrets come from OpenBao in real environments (D11 §3).
function required(name: string): string {
  const v = process.env[name];
  if (!v) throw new Error(`${name} is required`);
  return v;
}

export const config = {
  issuer: () => required('KC_ISSUER'),                 // e.g. https://id.example/realms/merchant
  clientId: () => required('KC_CLIENT_ID'),            // rc-bff-merchant
  clientSecret: () => required('KC_CLIENT_SECRET'),
  appUrl: () => required('APP_URL'),                   // public origin of this BFF, e.g. https://admin.example
  apiUrl: () => required('API_URL'),                   // internal RetailCloud.Api base URL
  // AUTH-07: __Host- prefix + Secure in production; plain names only for http://127.0.0.1 development.
  secureCookies: () => (process.env.COOKIE_SECURE ?? 'true') === 'true',
  idleTimeoutSeconds: 30 * 60,                         // IAM-005 (admin)
};

export const cookieNames = () =>
  config.secureCookies()
    ? { session: '__Host-rc_sid', csrf: '__Host-rc_csrf', pending: '__Host-rc_auth' }
    : { session: 'rc_sid', csrf: 'rc_csrf', pending: 'rc_auth' };
