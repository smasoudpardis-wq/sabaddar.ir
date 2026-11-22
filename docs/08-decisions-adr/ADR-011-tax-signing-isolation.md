# ADR-011 — Tax Signing Isolation و مدیریت کلید

- **وضعیت:** Proposed · **ارجاع:** D-11، TAX-002، TAX-008، DI-071/072، ریسک 9b

## زمینه
کارفرما اتصال **مستقیم** به سامانه مؤدیان را انتخاب کرده و کلید خصوصی هر Tenant نزد ماست. سوءاستفاده از کلید یعنی صدور صورتحساب جعلی به نام فروشگاه، که پیامد قانونی جدی دارد.

## تصمیم
1. **KMS:** OpenBao Transit با کلید `type` متناسب با الزام سامانه (Verification Required: الگوریتم و طول) و `exportable=false`، `allow_plaintext_backup=false`. یک کلید جدا به ازای هر `(tenant, issuingUnit)` با نام غیرقابل‌حدس و نگاشت در `tax.signing_key`. HSM سخت‌افزاری اگر الزام شود (AQ-05).
2. **TaxSigningService** جدا: Container، Namespace، Service Account و NetworkPolicy جدا. تنها Endpoint آن `POST /sign {taxInvoiceId}` است و فقط Workers با mTLS آن را صدا می‌زنند.
3. TSS محتوای صورتحساب را **خودش** با نقش `tax_signer_ro` از DB می‌خواند، Canonical را می‌سازد، Hash را با `content_hash` ثبت‌شده تطبیق می‌دهد، بررسی می‌کند که صورتحساب `Pending` و متعلق به Tenant کلید است، و سپس امضا می‌گیرد. **هیچ ورودی دلخواهی را امضا نمی‌کند.**
4. ممیزی هر امضا در `audit` و در Log جداگانه TSS. Alert برای نرخ غیرعادی (مثلاً بیش از ۳ برابر میانگین ساعتی Tenant)، امضای خارج از ساعات کاری Tenant (اختیاری) و خطای تطبیق Hash.
5. Policy OpenBao: فقط TSS به `transit/sign/*` دسترسی دارد. مدیریت کلید (Create و Rotate) فقط از ماژول TaxCompliance با Token جدا و ممیزی‌شده. **هیچ انسانی** دسترسی Sign ندارد. Break-glass با دو نفر و ممیزی.
6. Backup کلیدها: Snapshot رمزنگاری‌شده OpenBao با کلیدهای Unseal تقسیم‌شده (Shamir 3 از 5) بین افراد مسئول.
7. کلید موجود Tenant (OQ-09): Default «کلید تازه». اگر Import لازم شود، فقط از مسیر Wrapped Import با ممیزی.

## پیامدها
- ➕ نفوذ به Api یا Workers به‌تنهایی امکان امضای دلخواه نمی‌دهد. سطح حمله کلید حداقل است.
- ➖ یک سرویس و یک خوشه OpenBao بیشتر برای عملیات؛ تأخیر اضافه امضا (چند ده میلی‌ثانیه؛ ناهمگام است و مهم نیست).

## جایگزین‌ها
- امضا داخل Workers با کلید در Vault KV: کلید در حافظه Worker افشا می‌شود. رد شد.
- شرکت معتمد: کارفرما رد کرد (D-11).
