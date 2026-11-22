# ADR-004 — Transaction Consistency

- **وضعیت:** Proposed · **ارجاع:** SAL-007، §11.1، BR-030..035، D03 §6

## زمینه
فروش باید فاکتور، موجودی، سند، نسیه و رویدادهای بعدی را بدون تکرار و بدون حالت نیمه‌کاره ثبت کند. Retry شبکه موبایل رایج است.

## تصمیم
1. عملیات فهرست D03 §6.1 در **یک تراکنش PostgreSQL** اجرا می‌شوند، از طریق tx-portها، با UoW مشترک.
2. **Idempotency دوسطحی:** (الف) Header `Idempotency-Key` با رکورد `(tenant, key, payloadHash, response)`؛ (ب) Unique `sourceRef` روی Movement، Journal، ReceivableEntry و TaxInvoice (BR-032).
3. **Optimistic Version** روی Aggregateهای قابل ویرایش، **Pessimistic Lock** با ترتیب سراسری (D03 §6.2) در عملیات اتمی.
4. **هیچ I/O خارجی داخل تراکنش نیست.** Verify پرداخت پیش از تراکنش انجام می‌شود. ارسال پیام، مالیات و Refund بعد از Commit و از Outbox.
5. **Transactional Outbox** برای همه اثرات بعدی؛ **Inbox** برای همه مصرف‌کننده‌ها و Callbackها.
6. وضعیت‌های `Pending / Confirmed / Failed / NeedsReview` و Reconciliation برای هر تعامل بیرونی (پرداخت، Refund، مؤدیان).

## پیامدها
- ➕ Invariantهای مالی در سطح DB تضمین می‌شوند. هر Retry امن است.
- ➖ تراکنش‌ها چند Schema را لمس می‌کنند و جداسازی آینده این ماژول‌ها را دشوار می‌کنند (آگاهانه؛ ADR-001). قفل موجودی در Peak گلوگاه احتمالی است.

## Failure Modes
| حالت | کنترل |
|---|---|
| Deadlock | ترتیب قفل سراسری + Fitness Test |
| تراکنش طولانی | Timeout تراکنش (`idle_in_transaction_session_timeout`، `statement_timeout` = ۵s در مسیر POS)؛ Metric |
| Crash بعد از Commit و پیش از پاسخ | Idempotency Replay |
| پرداخت موفق، Finalize ناموفق | PM-01 و PM-08: Refund یا Reverse + NeedsReview |
| Outbox Backlog | Alert > ۵ دقیقه؛ Workers افقی |

## جایگزین‌ها
- **Saga بین ماژول‌ها:** پیچیدگی و پنجره ناسازگاری. رد شد (برای MVP).
- **Event Sourcing کامل:** Overengineering؛ D03 D-29.
