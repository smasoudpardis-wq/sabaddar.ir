# ADR-003 — Identity Model

- **وضعیت:** Proposed · **ارجاع:** §8.3، IAM-001..009، CUS-001، D-03، D-09، A-02

## زمینه
سه جمعیت کاربری با تهدید و چرخه عمر متفاوت داریم: کارکنان فروشگاه، مشتری نهایی و اپراتور پلتفرم. استقرار داخل ایران است و IdP ابری خارجی ممنوع است (A-02).

## تصمیم
1. **Keycloak Self-hosted** با سه Realm: `merchant`، `customer` و `platform`. هر Realm Client، Audience، سیاست MFA و Session جدا دارد.
2. **BFF الگو:** Next.js Server با OIDC Authorization Code + PKCE (Confidential Client). Token فقط سمت سرور است و مرورگر فقط Cookie `HttpOnly; Secure; SameSite=Lax` (Session Id) دارد. Session در Redis رمزنگاری‌شده است.
3. Api فقط JWT با `aud` مربوط را می‌پذیرد: `merchant-api`، `customer-api` یا `platform-api` (BR-004).
4. **مجوزدهی در RetailCloud است، نه در IdP.** IdP فقط هویت (`sub`) را می‌دهد. Membership کاربر در Tenantها، Role و Scope در ماژول IdentityAccess هستند (برای تغییر فوری و ممیزی). Claim `tid` (Tenant فعال) را BFF پس از انتخاب کاربر در Token Exchange یا Session قرار می‌دهد و Api با Membership تطبیق می‌دهد.
5. MFA: TOTP و WebAuthn برای نقش‌های حساس (IAM-003). OTP پیامکی برای Realm `customer` و ورود صندوق‌دار.
6. OTP مشتری **در RetailCloud** پیاده می‌شود (CustomerIdentity)، نه در Keycloak، چون منطق رضایت، Membership، Rate Limit به ازای شماره و Blind Index در دامنه ماست. پس از تأیید، Keycloak Realm `customer` از طریق Token Exchange یا Custom Authenticator سشن صادر می‌کند (Spike: AQ مرتبط در D06).
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
