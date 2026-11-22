# ADR-003 — Identity Model

- **وضعیت:** Proposed (بند ۶: **Accepted** 2026-11-22) · **ارجاع:** §8.3، IAM-001..009، CUS-001، D-03، D-09، A-02

## زمینه
سه جمعیت کاربری با تهدید و چرخه عمر متفاوت داریم: کارکنان فروشگاه، مشتری نهایی و اپراتور پلتفرم. استقرار داخل ایران است و IdP ابری خارجی ممنوع است (A-02).

## تصمیم
1. **Keycloak Self-hosted** با دو Realm: `merchant` و `platform` (Realm `customer` با اصلاح بند ۶ حذف شد — پایین همین سند). هر Realm Client، Audience، سیاست MFA و Session جدا دارد.
2. **BFF الگو:** Next.js Server با OIDC Authorization Code + PKCE (Confidential Client). Token فقط سمت سرور است و مرورگر فقط Cookie `HttpOnly; Secure; SameSite=Lax` (Session Id) دارد. Session در Redis رمزنگاری‌شده است.
3. Api فقط JWT با `aud` مربوط را می‌پذیرد: `merchant-api`، `customer-api` یا `platform-api` (BR-004).
4. **مجوزدهی در RetailCloud است، نه در IdP.** IdP فقط هویت (`sub`) را می‌دهد. Membership کاربر در Tenantها، Role و Scope در ماژول IdentityAccess هستند (برای تغییر فوری و ممیزی). Claim `tid` (Tenant فعال) را BFF پس از انتخاب کاربر در Token Exchange یا Session قرار می‌دهد و Api با Membership تطبیق می‌دهد.
5. MFA: TOTP و WebAuthn برای نقش‌های حساس (IAM-003). OTP پیامکی برای ورود مشتری (در CustomerIdentity) و صندوق‌دار.
6. OTP مشتری **در RetailCloud** پیاده می‌شود (CustomerIdentity)، نه در Keycloak، چون منطق رضایت، Membership، Rate Limit به ازای شماره و Blind Index در دامنه ماست. **Session و Token مشتری را هم خود RetailCloud صادر می‌کند** (اصلاح Accepted در پایین همین سند؛ جایگزین Token Exchange یا Custom Authenticator رد شد).
7. PIN صندوق‌دار (IAM-004): قفل محلی Session روی Terminal ثبت‌شده که در Api با Hash (Argon2id) وارسی می‌شود. جایگزین ورود کامل نیست.

## پیامدها
- ➕ استاندارد، MFA و WebAuthn آماده، ممیزی Session و جداسازی قوی قلمروها.
- ➖ نگهداری Keycloak (Patch و HA) با ماست. Customization فرایند OTP به Spike نیاز دارد.

## Failure Modes
| حالت | کنترل |
|---|---|
| Keycloak Down | Sessionهای فعال BFF تا انقضای Access Token ادامه می‌یابند؛ HA دو نمونه؛ صندوق با PIN قفل/بازگشایی می‌شود |
| سرقت Cookie | `HttpOnly`، `Secure`، اتصال Session به Device؛ Refresh Rotation با تشخیص استفاده مجدد |
| Token یک Realm در Api دیگر | بررسی `aud` و `iss` |

## جایگزین‌ها
- **OpenIddict داخل Api:** کنترل کامل ولی پیاده‌سازی MFA، WebAuthn و UI ورود با ما. **گزینه دوم** اگر Spike Keycloak برای OTP مشتری ناموفق باشد.
- **Token در مرورگر (SPA خالص):** سطح XSS بزرگ‌تر. رد شد.

## نتیجه Spike SP-01 (2026-11-22)

**محیط:** Keycloak 26.0 (Docker Hub `keycloak/keycloak:26.0`؛ quay.io از این محیط در دسترس نبود، که برای Mirror داخلی هم مهم است) با Realmهای تعریف‌شده به‌صورت کد (`src/backend/dev/keycloak/*-realm.json`). اسکریپت خودکار `spike-sp01.js` ورود واقعی را با مرورگر (Playwright) انجام می‌دهد.

**۱۸ از ۱۸ بررسی PASS شد** (C8b بعداً اضافه شد):

| # | بررسی | کنترل D06 |
|---|---|---|
| C1 | درخواست بدون PKCE رد می‌شود (`invalid_request`) | AUTH-01 |
| C2 | `state` برگردانده می‌شود | AUTH-01 |
| C3 | تبادل code بدون `code_verifier` رد می‌شود | AUTH-01 |
| C4–C8 | با Verifier: Token صادر می‌شود؛ `aud=merchant-api`؛ `iss` = Realm merchant؛ عمر Access Token دقیقاً ۶۰۰ ثانیه؛ `azp` = Client BFF | AUTH-05، BR-004 |
| C8b | Access Token دارای `sub` و `auth_time` است و `email` ندارد. **سابقه:** E2E بخش US-021 نشان داد Realm-as-code وقتی `clientScopes` را صریح تعریف می‌کند، Scopeهای داخلی Keycloak (از جمله `basic` که `sub` را می‌گذارد) ساخته نمی‌شوند؛ Scopeهای `basic` و `profile` (فقط نام) صریح اضافه شدند | AUTH-05، D06 §8 |
| C10–C11 | Refresh یک Refresh Token جدید می‌دهد و استفاده دوباره از قبلی رد می‌شود | AUTH-06 |
| C9، C9b | تکرار Authorization Code رد می‌شود **و** Sessionی که از آن صادر شده باطل می‌شود | AUTH-01 |
| C12 | Password Grant برای BFF غیرفعال است | — |
| C13–C14 | Token پلتفرم `platform-api` دارد و نه `merchant-api`، با Issuer جدا. کاربر فروشگاه به Realm پلتفرم وارد نمی‌شود | BR-004 |
| C15–C16 | ۱۰ ورود ناموفق، حساب را **موقتاً** قفل می‌کند و رمز درست هم تا پایان قفل پذیرفته نمی‌شود | AUTH-04 |

**یافته‌های فرعی:**
- (۱) Keycloak 26 نام و نام خانوادگی را در پروفایل اجباری می‌کند. Onboarding کاربران (US-025) باید آن‌ها را بفرستد، وگرنه کاربر به صفحه تکمیل پروفایل می‌رود.
- (۲) قالب ورود فارسی و RTL پیش‌فرض کار می‌کند.
- (۳) Chromium برای `localhost` از IPv6 استفاده می‌کند. Redirect URIهای Dev روی `127.0.0.1` تنظیم شده‌اند.

**اصلاح بند ۶ — `Accepted` (تأیید کارفرما، 2026-11-22):** ورود **مشتری** با OTP بیرون از Keycloak بماند. ماژول CustomerIdentity OTP را تأیید می‌کند و BFF پورتال یک Session مات (HttpOnly) نگه می‌دارد. Api درخواست‌های `/c/*` را با Token کوتاه‌عمری می‌پذیرد که BFF با کلید Transit خودش امضا می‌کند (`aud=customer-api`، JWKS داخلی).

دلایل:
- (الف) منطق OTP، Rate Limit، Blind Index و رضایت از قبل در CID است (ADR-003 بند ۶).
- (ب) اتصال آن به Keycloak به افزونه سفارشی Java (Authenticator SPI) یا قابلیت Token Exchange نیاز دارد. این یعنی کد و عملیات بیشتر در مسیر حساس.
- (ج) شماره موبایل مشتری در یک سیستم دوم (Keycloak) تکرار نمی‌شود (حداقل‌سازی، D06 §8).

جایگزینِ ردشده: Realm `customer` در Keycloak با Authenticator سفارشی. **پیامد:** Keycloak فقط دو Realm دارد (`merchant`، `platform`). Token مشتری را RetailCloud صادر می‌کند (کلید OpenBao Transit، JWKS داخلی، `iss=retailcloud-customer`، `aud=customer-api`، عمر ≤ ۱۰ دقیقه، فقط در BFF پورتال). D04، D06 و D07 به‌روز شدند.
