# merchant-admin — BFF و پنل مدیریت فروشگاه (US-021)

Next.js 15 در نقش BFF (D04 §6، ADR-003). Token فقط سمت سرور می‌ماند و مرورگر فقط یک Session id مات در Cookie `HttpOnly` دارد.

| مسیر | کار |
|---|---|
| `GET /auth/login` | شروع Authorization Code + PKCE S256 با `state` و `nonce` |
| `GET /auth/callback` | تبادل code، ساخت Session سمت سرور، Cookie `rc_sid` (Production: `__Host-rc_sid`، `Secure`) |
| `POST /auth/logout` | ابطال Refresh Token در Keycloak و حذف Session (CSRF لازم) |
| `GET /bff/me` | کاربر و عضویت‌ها از `GET /api/v1/me` |
| `POST /bff/active-tenant` | انتخاب فروشگاه؛ فقط اگر API عضویت را تأیید کند در Session ذخیره می‌شود (CSRF لازم) |
| `GET /bff/tenant` | پروفایل فروشگاه فعال (`X-RC-Tenant` را BFF می‌گذارد، نه مرورگر) |

CSRF: هر درخواست تغییردهنده باید `Origin` همین برنامه و Header `x-csrf-token` برابر Cookie `rc_csrf` داشته باشد.

## متغیرها

`KC_ISSUER`، `KC_CLIENT_ID`، `KC_CLIENT_SECRET` (در محیط واقعی از OpenBao)، `APP_URL`، `API_URL`، `COOKIE_SECURE` (پیش‌فرض `true`؛ فقط در Dev روی `http://127.0.0.1` مقدار `false`).

## اجرا و تست

```bash
npm ci && npm run build
```

```bash
NODE_PATH=/path/to/node_modules/with/playwright e2e/run-e2e.sh
```

پیش‌نیازهای E2E در سر فایل `e2e/run-e2e.sh` آمده است (Keycloak از `src/backend/dev/keycloak`، پایگاه داده‌ی تست از `src/backend/scripts/setup-test-db.sh`). ۱۷ بررسی مرورگری: ورود واقعی، Cookie، نبودن Token در JS، فهرست عضویت، CSRF، Tenant بیگانه → 404، تعویض فروشگاه، خروج.

**محدودیت فعلی:** Session Store در حافظه است و برای یک Instance کافی است. نسخه‌ی Valkey پیگیری بعدی است (D12 §0.1).
