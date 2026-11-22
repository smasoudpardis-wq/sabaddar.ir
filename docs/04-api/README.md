---
doc: D07
title: "API Contract — OpenAPI 3.1 (راهنما)"
version: 0.1.0
status: Draft — برای بازبینی کارفرما
date: 2026-11-22
owner: Backend Lead / Architect (Claude، نقش طراحی)
source: "D02 v0.2، D03، D04، D05، D06؛ سند مرجع §15"
related: [D07-API-OpenAPI.yaml, tools/build_openapi.py, tools/check_contract.py, ../03-security/D06-Security-Threat-Model.md]
decisions: [D-53..D-58]
open_questions: [ApiQ-01..ApiQ-05]
---

# D07 — قرارداد API

**فایل اصلی:** [`D07-API-OpenAPI.yaml`](D07-API-OpenAPI.yaml) — OpenAPI **3.1.0**، **۱۵۸ Operation**، ۱۳۹ مسیر و ۱۶۶ Schema.

## وضعیت اعتبارسنجی (اجراشده)

| بررسی | ابزار | نتیجه |
|---|---|---|
| انطباق با Schema رسمی OpenAPI 3.1 | `openapi-spec-validator` 0.7.1 | **VALID** |
| Lint با قواعد `recommended-strict` (شامل اعتبار Exampleها در برابر Schema) | `@redocly/cli` 1.25.11 | **۰ خطا، ۰ هشدار** (دو قاعده آگاهانه خاموش؛ [`redocly.yaml`](redocly.yaml)) |
| سازگاری بین اسناد: هر Operation به نیازمندی D02 ارجاع دارد؛ هر مجوز در ماتریس D06 وجود دارد؛ عملیات پولی و موجودی Idempotent‌اند؛ پاسخ‌های 400/404/422 الزامی وجود دارند | [`tools/check_contract.py`](tools/check_contract.py) | **PASS** (۱۵۸ Operation، ۳۱ مجوز) |

```bash
python3 docs/04-api/tools/build_openapi.py      # YAML را از Generator می‌سازد
python3 docs/04-api/tools/check_contract.py     # بررسی‌های بین‌سندی
npx @redocly/cli@1.25.11 lint docs/04-api/D07-API-OpenAPI.yaml --config docs/04-api/redocly.yaml
```

> **YAML تولیدی است** `[DEC D-53]`. منبع ویرایش `tools/build_openapi.py` است. دلیل: ۱۵۸ Operation مدل خطا، امنیت، Idempotency و Traceability یکسان دارند و Generator این یکنواختی را تضمین می‌کند. Generator ساده (Python + PyYAML) است. اگر تیم ترجیح دهد، پس از Gate A می‌توان YAML را منبع کرد و Generator را کنار گذاشت (ApiQ-01).

---

## 1. قراردادهای کلی

| موضوع | قاعده | مرجع |
|---|---|---|
| Base URL | `/api/v1` (Placeholder: `api.example.invalid`؛ دامنه در D11) | §15.1 |
| Audienceها | `merchantBearer` (BFF)، `customerBearer` (`/c/*`)، `platformBearer` (`/platform/*`)، `integrationKey` (`/partner/*`)، عمومی (`/r/{token}`، `/callbacks/*`، `/integrations/bale/webhook/*`، شروع OTP) | ADR-003، BR-004 |
| Tenant | هرگز در Path یا Body یا Query نیست؛ از Session | BR-001 |
| منبع Tenant دیگر | **404** (نه 403) | BR-003، D-48 |
| پول | `Money`: رشته عدد صحیح ریال با Pattern `^-?[0-9]{1,19}$` | ADR-009 |
| مقدار | `Quantity`: رشته اعشاری تا ۶ رقم | BR-012 |
| نرخ | `BasisPoints` (عدد صحیح) | — |
| زمان | ISO-8601 UTC؛ شمسی فقط در گزارش (`jalaliDate`) | BR-020 |
| هم‌زمانی | `expectedVersion` در بدنه Commandهای تغییردهنده؛ `ETag` در GET تکی؛ ناهمخوانی ← `409 version-conflict` | BR-034 |
| Idempotency | Header `Idempotency-Key` (۱۶–۶۴ نویسه) برای `x-idempotent: true` | BR-030/031، D-34 |
| Approval | پاسخ `403` با `code=approval-required` و `approvalRequestId` ← مدیر `POST /approvals/{id}/decision` ← تکرار درخواست با `approvalId` در بدنه یا `X-Approval-Id` | IAM-007 |
| صفحه‌بندی | Cursor (`cursor`، `limit` ≤ ۲۰۰)؛ پاسخ `{items, page:{nextCursor, hasMore}}` | §15.1 |
| خطا | `application/problem+json` (RFC 9457) با `code` پایدار، `traceId`، و `errors[]` برای Validation | §15.1 |
| فیلد ناشناخته | همه Schemaها `additionalProperties: false`؛ سرور فیلد ناشناخته را با 400 رد می‌کند (ضد Mass Assignment، TM-013) | D06 |
| گزارش‌ها | `format=json|csv|xlsx`؛ `meta.dataAsOf` تازگی Read Model را نشان می‌دهد | RPT-002 |

### 1.1 Extensionهای سفارشی (قرارداد با تست‌ها)

| Extension | معنی | مصرف‌کننده |
|---|---|---|
| `x-permission` | مجوز موردنیاز (از ماتریس D06 §6.2). `tenant.member` یعنی هر عضو فعال Tenant جلسه. | Policy در Api؛ SEC-T-012 |
| `x-scope` | سطح Scope: `Tenant`، `Branch`، `Warehouse`، `Register`، `OwnShift` | SEC-T-011 |
| `x-idempotent` | Header اجباری Idempotency | Filter Idempotency؛ AT-210 |
| `x-requirements` | شناسه‌های D02، D03 و D06 | ماتریس ردیابی D10 |
| `x-audited` | ثبت در Audit زنجیره‌ای | AUD-001 |
| `x-approval` | شرطی که Approval می‌خواهد | IAM-007 |
| `x-rate-limit-class` | کلاس Rate Limit (جدول 4) | NFR-SEC-007 |
| `x-error-catalog` (ریشه) | فهرست کدهای خطا و Status | Api Error Mapper؛ UI پیام فارسی |

---

## 2. نمای Endpointها (به تفکیک Tag)

| Tag | تعداد | مهم‌ترین Operationها |
|---|---|---|
| Tenant | 11 | `registerTenant`، `updateMyTenant`، `pairTerminal`، `createExport` |
| Access | 12 | `getMe`، `selectActiveTenant`، `inviteUser`، `decideApproval`، `decideSupportAccess` |
| Catalog | 15 | **`resolveBarcode`**، `createProduct` (با تأیید و موجودی افتتاحیه)، `mergeProduct`، `setPrices` |
| Inventory | 11 | `postGoodsReceipt`، `postAdjustment`، `createCount` ← `addCountEntries` ← `getCountResult` ← `confirmCount` |
| Procurement | 4 | `createSupplier`، `paySupplier`، `postPurchaseReturn` |
| POS | 16 | `openShift`، `createSale`، `addSaleLine`، `applyDiscount`، `attachCustomer`، **`recordTender`**، `closeShift` |
| Sales | 6 | **`finalizeSale`**، `createReturn`، `deliverReceipt` |
| Payments | 7 | `createPaymentIntent`، `pspCallback`، `completeRefundManually`، `importSettlement` |
| Accounting | 11 | `createJournalDraft` ← `postJournal`، `reverseJournal`، `closePeriod`، `getTrialBalance`، `getLedger` |
| Receivables | 8 | `createCreditAccount`، `recordCollection`، `getAging`، `getStatement`، `writeOff` |
| Customers | 5 | **`identifyCustomer`**، `startCustomerOtp`، `linkMembership`، `createStoreInvite` |
| Portal | 18 | OTP، QR، عضویت با رضایت، رضایت‌ها، تاریخچه دسترسی، فاکتورها، نسیه و پرداخت مانده، اتصال بله، Export و حذف |
| Public | 2 | `getPublicReceipt`، `baleWebhook` |
| Tax | 10 | پروفایل، **کلید** (تولید ← CSR ← فعال‌سازی)، تست اتصال، صورتحساب‌ها، Retry، داشبورد |
| Settings | 4 | قالب فاکتور، کلید یکپارچگی |
| Reports | 4 | فروش روزانه، موجودی، برگشتی، سود ناخالص |
| Audit | 1 | `listAudit` |
| PartnerApi | 2 | کاتالوگ و موجودی (Scopeدار) |
| Platform | 11 | Tenantها، JIT، Policyهای مؤدیان و نگهداری، صنف‌ها، Moderation کاتالوگ |

---

## 3. جریان‌های کلیدی با API

### 3.1 فروش نقدی (UC-02)
```text
POST /shifts                               (Idempotency-Key)             → Shift Open
POST /pos/sales {registerId, clientDraftId}                              → Sale Draft v1
POST /pos/sales/{id}/lines {barcode, qty}  (×n)                          → Sale (preview totals)
POST /pos/sales/{id}/tenders {Cash, amount, tendered} (Idempotency-Key)  → Sale (paid = total)
POST /sales/{id}/finalize {expectedVersion, expectedTotal} (Idempotency-Key) → Sale Finalized + invoiceNo + receiptUrl
```
Retry با همان کلید ← همان پاسخ (P0-3). `409 price-changed` ← نمایش قیمت جدید ← Finalize با کلید جدید.

### 3.2 فروش نسیه (UC-11)
`attachCustomer` (QR یا کد عضو) ← در پاسخ `customer.hasCreditAccount` و `availableCredit` (با مجوز) ← `recordTender {CustomerCredit}` ← `finalizeSale`. سقف اعتبار **در تراکنش Finalize** بررسی می‌شود. نقض آن ← `409 credit-limit-exceeded` با `context.available`، یا `403 approval-required`.

### 3.3 پرداخت آنلاین (UC-08)
`createPaymentIntent` ← نمایش `paymentUrl` به‌صورت QR ← مشتری پرداخت می‌کند ← `pspCallback` (عمومی، Inbox) ← Verify سرور-به-سرور ← `getPaymentIntent` (Polling) = `Confirmed` ← `finalizeSale` (یا PM-01 خودکار، DQ-02).

### 3.4 شناسایی مشتری (UC-03)
`identifyCustomer {qrToken}`:
- `member` ← `attachCustomer`.
- `not_member` + `joinRequestId` ← مشتری روی گوشی `portalAcceptJoinRequest` را تأیید می‌کند.
- با کد عمومی و غیرعضو ← `startCustomerOtp` ← `linkMembership`.

### 3.5 مؤدیان (UC-07)
راه‌اندازی: `updateTaxProfile` ← `generateSigningKey` ← `getSigningKeyCsr` (ثبت در کارپوشه) ← `activateSigningKey` ← `testTaxConnection`. پس از آن ارسال خودکار است. رسیدگی از `getTaxDashboard`، `listTaxInvoices?status=Rejected` و `retryTaxInvoice`.

---

## 4. Rate Limit (کلاس‌ها، پیش‌فرض‌های قابل تنظیم)

| کلاس | حد پیش‌فرض | کلید |
|---|---|---|
| `otp` | ۵ در ساعت و ۱۰ در روز به ازای شماره؛ ۲۰ در ساعت به ازای IP؛ CAPTCHA پس از آستانه | شماره + IP + دستگاه |
| `customer-lookup` | ۳۰ در دقیقه؛ قفل پس از ۱۰ خطای پیاپی | کاربر + دستگاه |
| `catalog-resolve` | ۱۲۰ در دقیقه | کاربر |
| `callback` | ۳۰۰ در دقیقه | IP + Gateway |
| `receipt-link` | ۶۰ در دقیقه | IP |
| `receipt-delivery` | ۵ در ساعت به ازای فاکتور | فاکتور |
| `qr-issue` | ۳۰ در ساعت | مشتری |
| `pairing` | ۱۰ در ساعت | IP |
| `tax-test` | ۱۰ در ساعت | Tenant |
| `partner` | ۶۰۰ در دقیقه | کلید |
| پیش‌فرض | ۶۰۰ در دقیقه به ازای کاربر؛ ۶٬۰۰۰ به ازای Tenant | — |

---

## 5. نسخه‌بندی و تغییرات

- Path Versioning `/api/v1`. **تغییرات Additive** (فیلد اختیاری جدید در پاسخ، Operation جدید، مقدار Enum جدید در پاسخ) در v1 مجازند. کلاینت‌ها باید فیلد و مقدار ناشناخته در **پاسخ** را تحمل کنند `[DEC D-54]`.
- **تغییر ناسازگار** ← v2 موازی؛ v1 با Header‌های `Deprecation` و `Sunset` دست‌کم ۶ ماه نگه داشته می‌شود.
- کلاینت‌های فرانت از `packages/contracts` تولیدشده از همین YAML استفاده می‌کنند (D04 §6). CI ناسازگاری را با `oasdiff` بین شاخه و main بررسی می‌کند `[DEC D-55]`.
- **API9 (Inventory):** هر Endpointی که در کد هست و در YAML نیست، Build را می‌شکند (Startup Endpoint Registry در برابر OpenAPI) `[DEC D-56]`.

---

## 6. تصمیم‌ها و پرسش‌های باز

| ID | نوع | شرح |
|---|---|---|
| D-53 | DEC | YAML تولیدشده از Generator؛ منبع ویرایش Generator است |
| D-54 | DEC | تحمل فیلد و مقدار ناشناخته در پاسخ؛ رد فیلد ناشناخته در درخواست |
| D-55 | DEC | Breaking-change Detection با `oasdiff` در CI |
| D-56 | DEC | Endpoint بدون مستند = خطای Build |
| D-57 | DEC | `tenant.member` به‌عنوان مجوز صریح برای عملیات خواندنی عمومی اعضا |
| D-58 | DEC | Callback و Webhook بدنه آزاد (`additionalProperties: true`) دارند. Validation و Mapping در Adapter هر Provider است، چون قالب Providerها متفاوت و بیرون از کنترل ماست. |
| ApiQ-01 | OQ | آیا تیم YAML را منبع می‌خواهد (و Generator حذف شود)؟ |
| ApiQ-02 | OQ | آیا اپ موبایل Native آینده (P1) همین BFF را استفاده می‌کند یا Token Flow جدا (PKCE + Secure Storage) لازم است؟ |
| ApiQ-03 | OQ | قالب دقیق Callback هر PSP و Webhook بله پس از Verification به Adapterها اضافه می‌شود؛ آیا Schema اختصاصی هر Provider در YAML مستند شود؟ |
| ApiQ-04 | OQ | Server-Sent Events برای وضعیت پرداخت (به‌جای Polling) در MVP؟ Default: Polling هر ۲ ثانیه با سقف ۱۵ دقیقه |
| ApiQ-05 | OQ | GraphQL یا BFF-specific Aggregation برای داشبورد در P1؟ Default: خیر؛ REST + Read Model |

**نمونه‌های بارکد در Exampleها نمایشی‌اند** و GTIN واقعی تضمین‌شده نیستند (طبق §15.4 سند مرجع).
