# ADR-001 — Modular Monolith

- **وضعیت:** Proposed · **تاریخ:** 2026-11-22 · **ارجاع:** §6.1، §23، D03، D04

## زمینه
فروش، انبار، حسابداری، نسیه و پرداخت باید **اتمی** و قابل ممیزی باشند (P0-3، P0-4). تیم در شروع کوچک است، استقرار در ایران است و هزینه عملیات اهمیت دارد.

## تصمیم
- یک Backend Deployable (`RetailCloud.Api`) با ۱۵ ماژول. هر ماژول Schema جدا در یک PostgreSQL دارد و مرزش با Architecture Test اعمال می‌شود (D04 §5.2).
- `RetailCloud.Workers` و `TaxSigningService` Deployableهای جدا هستند، با همان قراردادها.
- ارتباط هم‌زمان فقط از Contracts، و از فهرست بسته tx-portها (D03 §4.3).

## پیامدها
- ➕ تراکنش ACID ساده برای Invariantهای مالی؛ Debug و استقرار ساده؛ هزینه کم.
- ➖ مقیاس افقی برای کل فرایند است، نه به ازای ماژول. یک خطای حافظه یا CPU کل Api را تحت تأثیر قرار می‌دهد. خطر «گِل شدن» مرزها وجود دارد.

## Failure Modes و کنترل
| حالت | کنترل |
|---|---|
| ارجاع مستقیم بین ماژول‌ها | NetArchTest در CI (R1–R9) |
| Query بین Schemaها | Mapping هر DbContext به Schema خودش؛ Review؛ فقط Reporting Join دارد |
| یک ماژول پرمصرف (مثلاً Export) بقیه را کند کند | اجرای کارهای سنگین در Workers؛ Bulkhead و Rate Limit |

## جایگزین‌ها
- **Microservices از روز اول:** رد شد. Saga برای Finalize، Invariantهای DI-004، DI-031، DI-040 و DI-051 را تضعیف می‌کند و هزینه زیرساخت و تیم را چند برابر می‌کند.
- **Monolith لایه‌ای بدون مرز ماژول:** رد شد. جداسازی آینده Notifications و Catalog ناممکن می‌شود.

## معیار بازنگری
معیارهای §23: حجم مستقل، نیاز Deploy جدا، تیم مالک مستقل، اثبات حفظ Invariantها. کاندیدهای اول: Notifications و Catalog.
