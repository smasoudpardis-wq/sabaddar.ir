# ADR-007 — External Catalog Licensing

- **وضعیت:** Proposed · **ارجاع:** §10، CAT-004/005، INT-002، R-04

## زمینه
هیچ منبع واحدی پوشش کامل بارکد ایرانی ندارد. مجوز داده (مثلاً ODbL برای Open Food Facts) بر Cache، Republish و Attribution اثر دارد.

## تصمیم
1. `ICatalogProvider` با `Lookup(gtin) → ProviderResult{fields, license, attribution, retrievedAt, ttl}` و `Capabilities`.
2. Provider Chain به ترتیب: Tenant ← Global داخلی ← Providerهای فعال (ترتیب قابل تنظیم در Platform) ← دستی.
3. هر رکورد بیرونی در `CatalogSourceRecord` با `license`، `attribution` و `expires_at` ذخیره می‌شود. انتشار در Global Catalog فقط اگر License اجازه دهد و پس از Moderation.
4. **ODbL:** اگر داده OFF در Global ما ادغام شود، Global Catalog یک «Derivative Database» است و ممکن است الزام Share-Alike ایجاد کند. **Verification Required** با مشاور حقوقی. Default امن: داده OFF فقط به‌عنوان «پیشنهاد» به کاربر نشان داده می‌شود و در `CatalogSourceRecord` با Attribution نگهداری می‌شود، **بدون ادغام** در `CatalogProduct` تا تأیید حقوقی.
5. تصاویر بیرونی: فقط پس از بررسی License؛ دانلود از Worker با Egress Allowlist و کنترل MIME و حجم (ضد SSRF).
6. Pilot پوشش با ۲۰۰ تا ۵۰۰ بارکد واقعی پیش از Gate C (KPI-07).

## پیامدها
- ➕ نقض مجوز رخ نمی‌دهد. افزودن منبع جدید (مثلاً GS1 Iran پس از قرارداد) ساده است.
- ➖ تا تأیید حقوقی، Global Catalog کندتر رشد می‌کند.

## Failure Modes
| حالت | کنترل |
|---|---|
| Provider Down یا کند | Timeout ۲s، Circuit Breaker، Negative Cache ۲۴h |
| داده غلط Provider | تأیید انسانی اجباری (CAT-004.1) |
| نشت داده Tenant به Global | Allowlist فیلدها در Suggestion (DI-093) |
