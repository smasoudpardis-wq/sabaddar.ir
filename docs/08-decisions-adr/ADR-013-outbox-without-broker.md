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
