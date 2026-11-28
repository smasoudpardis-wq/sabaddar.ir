# ADR-002 — Multitenant Data Isolation

- **وضعیت:** Proposed · **ارجاع:** §8.1، §8.2، BR-001..003، NFR-SEC-003، D03 §5.6

## زمینه
بزرگ‌ترین ریسک محصول نشت داده بین فروشگاه‌هاست (R-01). مشتریان SMB هستند و تعدادشان زیاد است. هزینه DB جدا برای هر Tenant بالاست.

## تصمیم
1. **Shared Database، Shared Schema (به ازای ماژول)**، با ستون `tenant_id uuid NOT NULL` روی هر جدول Tenant-owned.
2. `ENABLE` + `FORCE ROW LEVEL SECURITY` و Policy `USING/WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid)` روی همه آن جداول.
3. Tenant Context فقط از Session سرور و با `set_config(..., true)` (معادل `SET LOCAL`) داخل تراکنش تنظیم می‌شود (D04 §5.3).
4. نقش‌های DB:
   - `app_rw`: بدون `BYPASSRLS`، مالک هیچ جدولی نیست.
   - `app_worker`: بدون `BYPASSRLS`.
   - `migrator`: مالک جداول؛ فقط در Pipeline Migration استفاده می‌شود.
   - `reporting_ro`.
   - `tax_signer_ro`.
   - `cid_global` برای جداول Global مشتری.
5. FK داخل ماژول مرکب است: `(tenant_id, id)`. بین ماژول‌ها FK وجود ندارد.
6. Cache Key، Object Storage Key (`tenants/{tenantId}/…`)، Export و Job همه پیشوند Tenant دارند.
7. داده **Global** (Catalog و GlobalCustomer) در جداولی بدون `tenant_id` است. فقط نقش مخصوص ماژول مالک به آن‌ها دسترسی دارد و هیچ داده Tenant در آن‌ها نیست (DI-090، DI-064).
8. گزینه Enterprise (آینده): DB اختصاصی با همان Schema و Connection String جدا از طریق `TenantRouting`.

## پیامدها
- ➕ دفاع در عمق: باگ AuthZ در Application به‌تنهایی نشت نمی‌دهد. هزینه پایین است.
- ➖ هر Query یک Predicate اضافه دارد (ایندکس‌ها با `tenant_id` شروع می‌شوند). Noisy Neighbour ممکن است. Restore یک Tenant منفرد پیچیده است (Export/Import منطقی).

## Failure Modes و کنترل
| حالت | کنترل |
|---|---|
| جدول جدید بدون Policy | Fitness Test روی `pg_class`/`pg_policies` در CI |
| نشت Context در Pool | فقط `SET LOCAL` درون تراکنش؛ تست Pool با دو Tenant پیاپی روی یک اتصال |
| Worker بدون Context | Interceptor (غیرProd) + RLS (Prod) صفر ردیف یا خطای Insert |
| استفاده از نقش `migrator` در Runtime | Credential آن فقط در Pipeline؛ Alert ورود |
| `SECURITY DEFINER` Function که RLS را دور بزند | ممنوع بدون ADR؛ Lint روی Migrationها |

## جایگزین‌ها
- **Schema به ازای Tenant:** با ۱۰٬۰۰۰ Tenant، Migration و Catalog Bloat مدیریت‌پذیر نیست.
- **DB به ازای Tenant:** هزینه و عملیات بالا؛ فقط Enterprise.
- **فقط Application Filter (بدون RLS):** یک باگ کافی است تا داده نشت کند. رد شد.
