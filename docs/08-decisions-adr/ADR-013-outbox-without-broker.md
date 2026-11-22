# ADR-013 — Outbox Relay بدون Broker در MVP

- **وضعیت:** Proposed · **ارجاع:** §6.4، §7 (RabbitMQ «پس از نیاز واقعی»)، D04 §5.5

## تصمیم
- Outbox در Postgres (به ازای Schema). Relay در Workers با Polling کوتاه (۲۰۰ms، با Backoff وقتی خالی است) به‌علاوه `LISTEN/NOTIFY` برای بیدار شدن سریع. Dispatch درون‌فرایندی به Handlerها در Workers. Inbox در Schema مصرف‌کننده.
- Partitioning جداول Outbox بر اساس زمان و حذف پیام‌های منتشرشده پس از ۷ روز.

## معیار ورود Broker (RabbitMQ یا Kafka)
- بیش از ۵۰۰ پیام در ثانیه به‌طور پایدار، یا
- نیاز مصرف‌کننده خارج از Monolith (جداسازی Notifications یا Catalog)، یا
- نیاز به Fan-out گسترده یا Replay طولانی.

در آن صورت Relay پیام را به Broker منتشر می‌کند. قرارداد Envelope (D03 §7.1) تغییر نمی‌کند.

## پیامدها
- ➕ یک سیستم HA کمتر؛ تراکنش و ترتیب ساده؛ Debug آسان (SQL).
- ➖ بار Polling روی DB (ناچیز با ایندکس Partial روی `published_at IS NULL`). مقیاس محدودتر از Broker.

## اصلاح — پیاده‌سازی US-005 (2026-11-22)

**تغییر:** به‌جای «Outbox به ازای Schema»، **یک** `core.outbox` و **یک** `core.inbox` برای همه‌ی ماژول‌ها.

دلایل:
- (۱) Relay یک Query و یک تابع «Tenantهای دارای پیام» دارد. اگر هر Schema جدول خودش را داشت، این تابع باید روی همه‌ی جدول‌ها UNION می‌زد و هر ماژول جدید آن را تغییر می‌داد.
- (۲) نگهداری، Metric و Alert یکجا هستند.
- (۳) مالکیت ماژول با دو کنترل حفظ می‌شود: ستون `producer` و قید `event_type LIKE lower(producer) || '.%'` (T92)، و قاعده‌ی معماری R10 (فقط BuildingBlocks مستقیم می‌نویسد؛ ماژول‌ها از `IOutbox` استفاده می‌کنند).

| موضوع | پیاده‌سازی |
|---|---|
| تولید | `IOutbox.AddAsync` در همان UnitOfWork تولیدکننده؛ نقش `rc_app` فقط INSERT دارد (T91) |
| کشف کار | `core.outbox_due_tenants()` (SECURITY DEFINER، فقط شناسه‌ی Tenant). Worker بدون Context هیچ ردیفی نمی‌بیند (T94، TM-042) |
| Relay | به ازای هر Tenant یک UoW با همان Context؛ `FOR UPDATE SKIP LOCKED LIMIT 100`؛ چند Worker هم‌زمان مجازند |
| مصرف | هر Consumer در UoW جدا همراه ردیف `core.inbox`. پیام فقط وقتی منتشرشده علامت می‌خورد که همه Consumerها موفق باشند. Consumer موفق در تلاش بعدی با Inbox رد می‌شود |
| خطا | Backoff نمایی ۲^n ثانیه (±۲۰٪، سقف ۱۵ دقیقه)؛ بعد از ۱۰ تلاش `dead_at` و Alert `RCOutboxDeadLetter` |
| بیدار شدن | `LISTEN rc_outbox` (Trigger سطح Statement، در Commit). Polling ۲۰۰ms تا ۲s فقط پشتوانه است |
| نگهداری | `MessagingRetention` هر ساعت به ازای هر Tenant: Outbox منتشرشده > ۷ روز، Inbox > ۳۰ روز، Idempotency > ۸ روز |
| Metric | `rc_outbox_pending`، `rc_outbox_oldest_pending_seconds`، `rc_outbox_dead` (از `core.outbox_stats()`، فقط تجمیعی) و Counterهای انتشار و خطا |

**ترتیب:** یک Relay پیام‌های یک Aggregate را به ترتیب انتشار می‌دهد (تست O7). با چند Worker هم‌زمان ترتیب تضمین نمی‌شود، پس Consumer باید نسبت به ترتیب مقاوم باشد (D04 §5.5).

**شواهد:**
- ۱۰ تست Integration با نقش‌های واقعی `rc_app` و `rc_worker`، از جمله ۳۰۰ پیام با ۴ Relay هم‌زمان که هر کدام دقیقاً یک بار تحویل شد، و بیدار شدن با NOTIFY زیر ۱ ثانیه.
- Mutation: حذف Dedup Inbox، ۲ تست را قرمز کرد.

