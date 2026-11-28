# ADR-012 — Multi-PSP بدون نگهداری وجه

- **وضعیت:** Proposed · **ارجاع:** D-12، PAY-001..007، DI-020..024

## تصمیم
1. `IPaymentGateway` با Capabilities اختیاری (`Settle`, `Reverse`, `Refund`, `Inquiry`). هر PSP یک Adapter با Contract Test (Recorded Fixtures از Sandbox).
2. **هر Tenant قرارداد پذیرندگی خودش را دارد**. اعتبارنامه‌ها در OpenBao KV، مسیر `psp/{tenantId}/{gatewayId}` است. **وجه هرگز به حساب پلتفرم نمی‌آید.** پس پلتفرم «پرداخت‌یار» نیست (Verification حقوقی: این مدل نیاز به مجوز ندارد).
3. Callback در مسیر `/callbacks/psp/{gateway}/{intentId}` با Rate Limit جدا، Inbox و پاسخ سریع. Verify هم‌زمان در همان درخواست یا در Worker (بسته به Timeout PSP).
4. Return URL فقط UI را به‌روز می‌کند. وضعیت مرجع، نتیجه Verify است (PAY-003.2).
5. Reconciliation: Import فایل تسویه (MVP دستی) و Job تطبیق با `providerRef` و مبلغ.
6. دو Adapter اول: OQ-08.

## Failure Modes
| حالت | کنترل |
|---|---|
| Callback نیامد ولی پرداخت موفق بود | PM-02 Inquiry |
| Verify موفق، Finalize ناموفق | PM-01 + Reverse/Refund + NeedsReview |
| PSP بدون Reverse | NeedsReview و Refund دستی ممیزی‌شده |
| نشت اعتبارنامه | Vault، Masking، دسترسی فقط Adapter؛ چرخش |
