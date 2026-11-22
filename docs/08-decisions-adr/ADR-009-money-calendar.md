# ADR-009 — Money و Calendar

- **وضعیت:** Proposed · **ارجاع:** §11.2، §13، BR-010..023، D-07، D-08، D-13

## تصمیم
- **Money:** `long` ریال به‌علاوه `Currency` (IRR). Postgres: `bigint` (نه `numeric` برای مبلغ، برای سرعت و سادگی؛ سقف حدود ۹٫۲×۱۰¹⁸ ریال کافی است). JSON: رشته عدد صحیح. فرانت: `BigInt` یا رشته. Float ممنوع است (Analyzer: هیچ `double` یا `float` در ماژول‌های مالی).
- **گردکردن:** half-up به ریال، در نقاط تعریف‌شده BR-013. سرشکن کردن با Largest Remainder.
- **نرخ:** basis points (`int`).
- **Quantity:** `numeric(18,6)` با دقت Unit.
- **تومان:** فقط نمایش (÷۱۰) و ورودی (×۱۰). ترجیح نمایش، تنظیم کاربر است.
- **زمان:** `timestamptz` UTC. `TenantTimeZone` (IANA، پیش‌فرض `Asia/Tehran`). داده TZ (tzdata) در Container به‌روز نگه داشته می‌شود. ایران از ۱۴۰۱ ساعت تابستانی ندارد، ولی کد به Rule ثابت وابسته نیست.
- **تقویم:** دوره و سال مالی شمسی با مرزهای ذخیره‌شده به‌صورت `tstzrange` (BR-023). تبدیل شمسی با کتابخانه تست‌شده (`System.Globalization.PersianCalendar` در .NET؛ در فرانت کتابخانه منتخب با تست تطبیق).
- **روز کاری:** `businessDate = (finalizedAt AT TIME ZONE tenantTz)::date`. گزارش‌های روزانه بر اساس آن.

## Failure Modes
| حالت | کنترل |
|---|---|
| Overflow ضرب `qty × unitPrice` | محاسبه با `decimal` و سپس Round؛ Checked Arithmetic |
| اختلاف تبدیل تقویم بین .NET و JS | تست تطبیق ۱۰۰ سال تاریخ در CI |
| تغییر قانونی واحد پول (حذف صفرها) | A-22: ارز جدید، نه تغییر معنا |
