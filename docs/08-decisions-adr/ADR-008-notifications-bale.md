# ADR-008 — Notification Gateway و بله

- **وضعیت:** Proposed · **ارجاع:** §16، NOT-001..009، CUS-007، R-05

## تصمیم
1. ماژول Notifications با `INotificationChannel` و پیاده‌سازی‌های `SmsChannel` و `BaleBotChannel` (MVP)، به‌علاوه `EmailChannel` و `PushChannel` (P1).
2. هر ارسال از Outbox شروع می‌شود و هرگز در تراکنش کسب‌وکار نیست. وضعیت‌ها و Retry طبق NOT-005 و NOT-006.
3. **رضایت هنگام ارسال** دوباره بررسی می‌شود (DI-065)، نه فقط هنگام Queue.
4. آدرس کانال (شماره یا chatId) فقط لحظه ارسال از CustomerIdentity گرفته می‌شود (`IRecipientPolicy`) و در جدول Notification ذخیره **نمی‌شود**. فقط Hash یا Masked ذخیره می‌شود.
5. **بله:** اتصال فقط با Deep Link `ble.ir/<bot>?start=<linkToken>` صادرشده پس از ورود OTP، و Webhook با Secret مسیر و بررسی Token. همه قابلیت‌ها Verification Required هستند: ارسال بدون Start، Rate Limit، فرمت Webhook و Mini-App. تا Verification، **بله پشت Feature Flag** است و SMS کانال پیش‌فرض.
6. پیام فقط Receipt Link دارد، نه فهرست اقلام (NOT-002).
7. SMS: Provider داخلی با خط خدماتی. Delivery Report اگر پشتیبانی شود. Adapter دوم به‌عنوان Failover `[REC]`.

## Failure Modes
| حالت | کنترل |
|---|---|
| Provider Down | Retry + Fallback کانال (با رضایت) + DeadLetter |
| ارسال تکراری | Idempotency `(notificationId, channel, attemptNo)` + Dedup سمت Provider اگر پشتیبانی شود |
| Webhook جعلی بله | Secret Token + Inbox + اعتبارسنجی `chat.id` در برابر Link فعال |
