---
doc: D08
title: "Flows — فرایندها (BPMN-معادل) و Sequence Diagramها"
version: 0.1.0
status: Draft — برای بازبینی کارفرما
date: 2026-11-22
owner: Business Analyst / Architect (Claude، نقش طراحی)
source: "D02 §6–7، D03 §6–8، D07 (operationIdها)"
related:
  - ../00-product/D02-SRS-Functional-NonFunctional.md
  - D03-DDD-Context-Map.md
  - ../04-api/D07-API-OpenAPI.yaml
  - ../06-ux/D09-UX-Wireframe-Spec.md (بعدی)
decisions: [D-59..D-63]
open_questions: [FQ-01..FQ-04]
---

# D08 — جریان‌های فرایندی و تعاملی

> **قرارداد نمادها.** فرایندها با `flowchart` و Swimlane (هر `subgraph` یک Lane = نقش یا سیستم) رسم شده‌اند. این نمودارها **معادل عملیاتی BPMN** هستند، طبق اجازه سند مرجع. لوزی = Gateway تصمیم؛ ⏱ = رویداد زمانی؛ ⚡ = رویداد خطا یا Compensation. Sequenceها نام **operationId** در D07 را دارند تا قابل ردیابی باشند. همه نمودارها با Mermaid 11.4.1 در Chromium Render و بررسی شده‌اند (بخش 17).

## فهرست

| # | جریان | UC | مهم‌ترین قیدها |
|---|---|---|---|
| F-01 | تعریف کالا با اسکن | UC-01 | CAT-004.1، CAT-005 |
| F-02 | فروش و Finalize (نقد/کارتخوان) | UC-02 | DI-001..010، BR-056، P0-3 |
| F-03 | پرداخت آنلاین | UC-08 | PAY-003.x، PM-01/02 |
| F-04 | برگشت و بازپرداخت | UC-04 | BR-052..055، DI-005/006 |
| F-05 | عضویت با QR پیشخوان، اتصال بله و شناسایی در POS | UC-03 | CUS-004/005، BR-061 |
| F-06 | ارسال فاکتور و Fallback کانال | PM-05 | NOT-005/006/008 |
| F-07 | فروش نسیه، دریافت، یادآوری و مسدودی | UC-11 | BR-080..085 |
| F-08 | مؤدیان: راه‌اندازی کلید، ارسال، رد، اصلاح | UC-07 | BR-070..076، ADR-011 |
| F-09 | خط لوله Posting و مثال‌های عددی اسناد | — | DI-040..046، 5.8.1 D02 |
| F-10 | رسید خرید و WAC | UC-05 | BR-042/043 |
| F-11 | انبارگردانی | UC-09 | INV-009 |
| F-12 | شیفت و مغایرت صندوق | UC-06 | D-24 |
| F-13 | Draft آفلاین و Sync | — | ADR-006، DI-010 |
| F-14 | Approval لحظه‌ای | — | IAM-007، SoD-01 |
| F-15 | دسترسی JIT پشتیبانی | — | IAM-008 |

---

## F-01 — تعریف کالا با اسکن (UC-01)

### فرایند
```mermaid
flowchart TB
  subgraph L1["انباردار / مدیر شعبه"]
    A1(["اسکن بارکد"]) --> A2["ارسال resolveBarcode"]
    A6["بررسی پیشنهادها"] --> A7{"پیشنهاد مناسب؟"}
    A7 -- "بله" --> A8["ویرایش و تأیید فیلدها"]
    A7 -- "خیر" --> A9["ورود دستی"]
    A8 --> A10["قیمت، واحد، شناسه مالیاتی، موجودی اولیه"]
    A9 --> A10
    A10 --> A11["createProduct با confirmation"]
    A13(["کالا آماده فروش"])
  end
  subgraph L2["RetailCloud"]
    B1{"Check Digit معتبر؟"}
    B1 -- "خیر" --> E1["⚡ 422 invalid-barcode<br/>پیشنهاد ثبت به‌عنوان کد داخلی"]
    B1 -- "بله" --> B2{"در Tenant هست؟"}
    B2 -- "بله" --> B3["known: باز کردن کالای موجود"]
    B2 -- "خیر" --> B4{"در Global هست و معتبر؟"}
    B4 -- "بله" --> B6["candidates"]
    B4 -- "خیر" --> B5["زنجیره Provider<br/>Timeout 2s، Circuit Breaker، Negative Cache"]
    B5 --> B6
    B12{"بارکد هنوز آزاد است؟"}
    B12 -- "خیر" --> E2["⚡ 409 barcode-already-assigned<br/>پیشنهاد Merge"]
    B12 -- "بله" --> B13["یک تراکنش: Product + Variant + Barcode<br/>(+ OpeningBalance + سند)"]
  end
  A2 --> B1
  B6 --> A6
  A11 --> B12
  B13 --> A13
```

### توالی
```mermaid
sequenceDiagram
  autonumber
  actor U as انباردار
  participant P as POS/Admin PWA
  participant BFF as Web BFF
  participant API as Api (Catalog/Merch)
  participant CAT as Catalog (Global)
  participant OFFP as Open Food Facts
  participant DB as PostgreSQL

  U->>P: اسکن
  P->>BFF: resolveBarcode {barcode, branchId}
  BFF->>API: POST /catalog/barcodes/resolve (Bearer)
  API->>DB: lookup merch.tenant_product_barcode (RLS)
  alt یافت در Tenant
    API-->>P: kind=known + tenantProduct
  else یافت نشد
    API->>CAT: lookup global_barcode
    opt نبود یا منقضی و Provider فعال
      API->>OFFP: GET product (timeout 2s)
      OFFP-->>API: fields + license ODbL
      API->>CAT: save CatalogSourceRecord (license, attribution, expires_at)
    end
    API-->>P: kind=candidates, confidence, source, degraded?
  end
  U->>P: انتخاب و تأیید، قیمت و موجودی اولیه
  P->>API: createProduct (Idempotency-Key, confirmation.confirmed=true)
  API->>DB: BEGIN, insert product/variant/barcode, ux_barcode_active, opening movement + journal, COMMIT
  API-->>P: 201 Product
```

**نکته:** داده Open Food Facts فقط در `CatalogSourceRecord` ذخیره می‌شود و در `CatalogProduct` ادغام نمی‌شود تا تأیید حقوقی ODbL (ADR-007).

---

## F-02 — فروش و Finalize (UC-02)

### فرایند
```mermaid
flowchart TB
  subgraph C["صندوق‌دار"]
    C1(["شروع فروش"]) --> C2{"شیفت باز؟"}
    C2 -- "خیر" --> C3["openShift"]
    C3 --> C4
    C2 -- "بله" --> C4["createSale (clientDraftId)"]
    C4 --> C5["اسکن اقلام: addSaleLine"]
    C5 --> C6{"مشتری؟"}
    C6 -- "بله" --> C7["F-05: identify + attachCustomer"]
    C6 -- "خیر" --> C8
    C7 --> C8{"تخفیف؟"}
    C8 -- "بله" --> C9["applyDiscount (F-14 اگر بالای سقف)"]
    C8 -- "خیر" --> C10
    C9 --> C10["recordTender: نقد / کارتخوان / نسیه"]
    C10 --> C11{"باقیمانده = 0؟"}
    C11 -- "خیر" --> C10
    C11 -- "بله" --> C12["finalizeSale"]
    C14["چاپ / ارسال / فروش بعدی"]
  end
  subgraph S["RetailCloud (یک تراکنش)"]
    S1{"Idempotency: قبلاً موفق؟"}
    S1 -- "بله" --> S9["پاسخ ذخیره‌شده"]
    S1 -- "خیر" --> S2{"version و قیمت سرور = expected؟"}
    S2 -- "خیر" --> X1["⚡ 409 version-conflict / price-changed"]
    S2 -- "بله" --> S3["Lock: دوره، شماره فاکتور، شیفت، موجودی، اعتبار"]
    S3 --> S4{"موجودی کافی؟ سقف اعتبار؟"}
    S4 -- "خیر" --> X2["⚡ Rollback: 409 insufficient-stock / credit-limit-exceeded"]
    S4 -- "بله" --> S5["Snapshot، Movements، Journal، ReceivableEntry"]
    S5 --> S6["Outbox: SaleFinalized، TaxInvoiceRequested، ReceiptDeliveryRequested"]
    S6 --> S7["COMMIT"]
  end
  subgraph W["Workers (پس از Commit)"]
    W1["F-06 ارسال فاکتور"]
    W2["F-08 ارسال مؤدیان"]
    W3["Projectionهای گزارش"]
  end
  C12 --> S1
  S7 --> C14
  S9 --> C14
  S7 -.-> W1
  S7 -.-> W2
  S7 -.-> W3
  X1 --> C5
  X2 --> C5
```

### توالی Finalize با Retry شبکه
```mermaid
sequenceDiagram
  autonumber
  actor K as صندوق‌دار
  participant P as POS PWA
  participant API as Api (Sales)
  participant PAY as Payments
  participant INV as Inventory port
  participant ACC as Accounting port
  participant DB as PostgreSQL
  participant OB as Outbox Relay

  K->>P: پرداخت نقد و نهایی‌سازی
  P->>API: recordTender {Cash, amount, tendered} (key k1)
  API-->>P: Sale (remaining=0, change)
  P->>API: finalizeSale {expectedVersion, expectedTotal} (key k2)
  API->>DB: idempotency lookup k2 (none)
  API->>PAY: PaymentStatusQuery(sale) = all Confirmed
  API->>DB: BEGIN, set_config tenant, insert idempotency InProgress
  API->>DB: lock sale, recompute, lock period FOR SHARE, next invoice no
  API->>INV: IStockIssuer.Issue(lines) - lock balances in key order
  INV-->>API: costs (WAC)
  API->>ACC: IPostingService.Post(SaleRevenue) and Post(SaleCogs)
  API->>DB: outbox rows, audit, idempotency Completed, COMMIT
  API--xP: پاسخ گم شد (قطع شبکه)
  Note over P: UI: «در انتظار تأیید» — هرگز «موفق» بدون پاسخ
  P->>API: finalizeSale (همان key k2) پس از Backoff
  API->>DB: idempotency lookup k2 = Completed
  API-->>P: 200 همان پاسخ اول (همان invoiceNo)
  OB->>DB: poll outbox (SKIP LOCKED)
  OB-->>OB: dispatch SaleFinalized to Tax / Notifications / Reporting
```

**گزینه‌های خطا و پاسخ UI:**

| پاسخ | معنا | رفتار POS |
|---|---|---|
| `409 price-changed` | قیمت یا قاعده تخفیف بین Draft و Finalize عوض شده | نمایش قیمت جدید، تأیید صندوق‌دار، اصلاح پرداخت در صورت تغییر مبلغ، Finalize با **کلید جدید** |
| `409 insufficient-stock` | `context.shortages[]` | علامت‌گذاری اقلام، حذف یا کاهش، Finalize مجدد |
| `409 payment-not-confirmed` | Tender آنلاین هنوز تأیید نشده | ادامه F-03 |
| `409 in-progress` | درخواست هم‌کلید هنوز در حال اجراست (دو تب یا Retry زودهنگام) | صبر برای `Retry-After` |
| `409 period-closed` | دوره امروز بسته است (ناهنجاری) | توقف و پیام به مدیر و حسابدار |
| `403 approval-required` | تخفیف یا نسیه بالای سقف | F-14 |

---

## F-03 — پرداخت آنلاین (UC-08)

### توالی
```mermaid
sequenceDiagram
  autonumber
  actor K as صندوق‌دار
  actor M as مشتری
  participant P as POS
  participant API as Api (Payments/Sales)
  participant DB as PostgreSQL
  participant G as PSP
  participant WK as Workers (PM-01/02)

  K->>P: انتخاب پرداخت آنلاین
  P->>API: createPaymentIntent {saleId, gatewayId} (key)
  API->>DB: tx: Sale to PendingPayment, StockReservation 15m, Intent Initiated
  API->>G: CreatePayment (خارج از تراکنش)
  G-->>API: token / url
  API->>DB: Intent AwaitingCustomer
  API-->>P: paymentUrl, expiresAt
  P-->>M: نمایش QR پرداخت
  M->>G: پرداخت در صفحه بانک
  G->>API: pspCallback (form post)
  API->>DB: inbox insert (dedup providerRef)
  API->>G: Verify (server to server)
  alt مبلغ، Intent و مرجع درست و موفق
    G-->>API: verified
    API->>DB: Payment Confirmed, Outbox PaymentConfirmed
    API-->>M: صفحه «پرداخت موفق»
    P->>API: getPaymentIntent (polling 2s)
    API-->>P: Confirmed
    P->>API: finalizeSale (key)
    API-->>P: Finalized
    WK->>G: Settle (اگر PSP دارد، پس از Commit)
  else ناهمخوانی مبلغ یا مرجع
    API->>DB: NeedsReview
    API->>G: Reverse (اگر پشتیبانی شود)
    API-->>M: صفحه «پرداخت ناموفق — وجه برگشت می‌خورد»
  else Verify ناموفق
    API->>DB: Failed, Sale back to Draft, release reservation
  end
```

### Timeout، Callback دیررس و Finalize خودکار
```mermaid
flowchart LR
  T0(["⏱ هر دقیقه: PM-02"]) --> T1{"Intent AwaitingCustomer<br/>و expiresAt گذشته؟"}
  T1 -- "خیر" --> T9(["پایان"])
  T1 -- "بله" --> T2["Expired، آزادسازی Reservation،<br/>Sale به Draft"]
  T2 --> T3["⏱ Inquiry در +5 و +30 دقیقه"]
  T3 --> T4{"PSP می‌گوید موفق؟"}
  T4 -- "خیر" --> T9
  T4 -- "بله" --> T5["Payment Confirmed (دیررس)"]
  T5 --> T6{"Sale هنوز قابل Finalize؟<br/>موجودی و دوره"}
  T6 -- "بله" --> T7["PM-01: finalize خودکار<br/>key = pay:{paymentId}"]
  T6 -- "خیر" --> T8["⚡ RefundRequested + NeedsReview<br/>اعلان به مدیر"]
```

**قاعده:** پول تأییدشده هرگز «رها» نمی‌شود. یا فاکتور می‌گیرد یا Refund و NeedsReview (AT-272).

---

## F-04 — برگشت و بازپرداخت (UC-04)

### فرایند
```mermaid
flowchart TB
  subgraph K["صندوق‌دار / مدیر"]
    K1(["مشتری با کالا"]) --> K2["یافتن فاکتور: شماره، QR فاکتور یا لینک"]
    K2 --> K3["انتخاب اقلام، مقدار و وضعیت کالا"]
    K3 --> K4["انتخاب روش بازپرداخت"]
    K4 --> K5["createReturn"]
    K9(["رسید برگشت به مشتری"])
  end
  subgraph S["RetailCloud"]
    S1{"مقدار ≤ قابل‌برگشت؟"}
    S1 -- "خیر" --> E1["⚡ 422 exceeds-returnable-quantity"]
    S1 -- "بله" --> S2{"در مهلت، زیر سقف نقش،<br/>روش مجاز؟"}
    S2 -- "خیر" --> E2["⚡ 403 approval-required ← F-14"]
    S2 -- "بله" --> S3["یک تراکنش: SaleReturn، returnedQty،<br/>Movement ورودی با costAtSale، سند برگشت"]
    S3 --> S4{"روش؟"}
    S4 -- "نقد" --> S5["خروج نقد از شیفت (همان تراکنش)"]
    S4 -- "کارتخوان" --> S6["Refund AwaitingManual"]
    S4 -- "آنلاین" --> S7["Refund Requested ← PM-08"]
    S4 -- "کاهش نسیه" --> S8["ReceivableEntry CreditNote (BR-085)"]
    S5 --> S10["Outbox: SaleReturned، TaxCreditNoteRequested"]
    S6 --> S10
    S7 --> S10
    S8 --> S10
  end
  K5 --> S1
  E2 --> K4
  S10 --> K9
```

### مثال عددی سهم برگشت (BR-054)

فاکتور: ۳ واحد × ۴۰۰٬۰۰۰ = ۱٬۲۰۰٬۰۰۰ ریال؛ تخفیف سندی ۱۰۰٬۰۰۰ ← net = ۱٬۱۰۰٬۰۰۰؛ مالیات ۱۰٪ = ۱۱۰٬۰۰۰؛ lineTotal = **۱٬۲۱۰٬۰۰۰**.

| برگشت | محاسبه | مبلغ |
|---|---|---|
| ۱ واحد (اول) | round(1٬210٬000 × 1/3) = round(403٬333.33) | ۴۰۳٬۳۳۳ |
| ۱ واحد (دوم) | round(1٬210٬000 × 1/3) | ۴۰۳٬۳۳۳ |
| ۱ واحد (آخر) | **باقیمانده دقیق**: 1٬210٬000 − 806٬666 | **۴۰۳٬۳۳۴** |
| جمع | | **۱٬۲۱۰٬۰۰۰** = lineTotal ✓ (DI-006) |

همین روش برای تفکیک `net` و `tax` سهم هر برگشت اعمال می‌شود تا صورتحساب برگشتی مؤدیان دقیقاً قرینه باشد.

---

## F-05 — عضویت با QR پیشخوان، اتصال بله و شناسایی در POS (UC-03)

### عضویت و اتصال بله
```mermaid
sequenceDiagram
  autonumber
  actor M as مشتری
  participant W as Customer Portal
  participant API as Api (CustomerIdentity)
  participant SMS as SMS Provider
  participant BALE as Bale Bot API

  M->>W: اسکن QR پیشخوان: /join/inviteToken
  W->>API: portalGetInvite (امضا، انقضا، ابطال)
  API-->>W: نام فروشگاه و دامنه رسمی (هیچ داده دیگر)
  M->>W: ورود شماره
  W->>API: portalStartOtp {phone, captcha?}
  API->>API: rate limit شماره/IP/دستگاه، SMS budget
  API->>SMS: ارسال OTP (Worker، Outbox)
  M->>W: ورود کد
  W->>API: portalVerifyOtp
  API-->>W: session (isNewCustomer؟ GlobalCustomer + PublicCode)
  M->>W: انتخاب رضایت‌ها (عملیاتی روشن، بازاریابی خاموش)
  W->>API: portalJoinStore {consents, policyVersion}
  API-->>W: Membership (TenantCustomerId)
  opt اتصال بله
    M->>W: «اتصال بله»
    W->>API: portalBaleLinkToken
    API-->>W: deepLink ble.ir/bot?start=linkToken (10m، یک‌بارمصرف)
    M->>BALE: Start
    BALE->>API: baleWebhook (secret path) با start=linkToken و chat.id
    API->>API: inbox، linkToken معتبر و مصرف‌نشده، ذخیره رمز chatId
    API->>BALE: sendMessage «اتصال انجام شد»
  end
```

### شناسایی در POS
```mermaid
flowchart TB
  subgraph M["مشتری"]
    M1["Portal: QR من (120s)"]
    M4["تأیید عضویت روی گوشی"]
  end
  subgraph K["صندوق‌دار"]
    K1{"روش شناسایی؟"}
    K1 -- "QR" --> K2["اسکن QR ← identifyCustomer"]
    K1 -- "کد عمومی" --> K3["ورود کد ← identifyCustomer"]
    K7["attachCustomer"]
    K8["startCustomerOtp ← مشتری کد را می‌گوید ← linkMembership"]
  end
  subgraph S["RetailCloud"]
    S1{"توکن یا کد معتبر؟<br/>jti مصرف‌نشده، aud درست"}
    S1 -- "خیر" --> X1["⚡ 401 qr-expired / invalid-qr<br/>(پاسخ یکسان، بدون جزئیات)"]
    S1 -- "بله" --> S2{"عضو همین Tenant؟"}
    S2 -- "بله" --> S3["member + maskedName"]
    S2 -- "خیر، با QR" --> S4["not_member + joinRequestId<br/>Push به گوشی مشتری"]
    S2 -- "خیر، با کد" --> S5["not_member (بدون هیچ داده)"]
  end
  M1 --> K2
  K2 --> S1
  K3 --> S1
  S3 --> K7
  S4 --> M4
  M4 --> K7
  S5 --> K8
  K8 --> K7
```

**چرا QR عضویت را بدون OTP ممکن می‌کند ولی کد عمومی نه:** QR فقط با Session فعال خود مشتری در Portal صادر می‌شود. پس Proof of Possession است. کد عمومی را هر کسی می‌تواند بگوید (CUS-004).

---

## F-06 — ارسال فاکتور و Fallback کانال (PM-05)

```mermaid
flowchart TB
  A0(["ReceiptDeliveryRequested"]) --> A1["IRecipientPolicy: رضایت عملیاتی و کانال‌ها<br/>(هنگام ارسال دوباره بررسی می‌شود)"]
  A1 --> A2{"کانال مجاز؟"}
  A2 -- "هیچ" --> A3(["پایان: ثبت «بدون کانال مجاز»"])
  A2 -- "بله متصل" --> B1["Bale sendMessage<br/>متن: فروشگاه، مبلغ، Receipt Link"]
  A2 -- "فقط SMS" --> C1["SMS send"]
  B1 --> B2{"نتیجه؟"}
  B2 -- "موفق" --> B3["Sent (Delivered فقط با رسید Provider)"]
  B2 -- "خطای موقت" --> B4["⏱ Retry: 30s، 2m، 10m، 1h، 6h"]
  B4 --> B1
  B2 -- "خطای دائمی یا 5 شکست" --> B5{"رضایت SMS دارد؟"}
  B5 -- "بله" --> C1
  B5 -- "خیر" --> D1["DeadLettered + نمایش «ارسال نشد» در فاکتور"]
  C1 --> C2{"نتیجه؟"}
  C2 -- "موفق" --> C3["Sent ← Delivered با DLR"]
  C2 -- "شکست پس از Retry" --> D1
```

**قاعده:** هیچ شکستی در این جریان روی فروش اثر ندارد (BR-071، P0-7). صندوق‌دار می‌تواند `deliverReceipt` را دوباره صدا بزند (سقف ۵ در ساعت).

---

## F-07 — فروش نسیه، دریافت، یادآوری و مسدودی (UC-11)

### فروش نسیه و دریافت
```mermaid
sequenceDiagram
  autonumber
  actor K as صندوق‌دار
  participant API as Api (Sales)
  participant AR as Receivables port
  participant DB as PostgreSQL
  actor M as مشتری

  K->>API: identifyCustomer + attachCustomer
  API-->>K: hasCreditAccount=true، availableCredit
  K->>API: recordTender {CustomerCredit, amount} (موقت؛ بدون تعهد)
  K->>API: finalizeSale
  API->>DB: BEGIN ... lock stock balances
  API->>AR: ICreditLedger.Charge(customer, amount, dueDate)
  AR->>DB: lock credit_account (سطح 9 ترتیب قفل)
  alt outstanding + amount ≤ limit و سررسیدگذشته ندارد
    AR->>DB: insert Charge (open_amount = amount)، outstanding += amount
    AR-->>API: Approved
    API->>DB: Journal: Dr دریافتنی (تفصیلی مشتری) / Cr فروش و مالیات، COMMIT
    API-->>K: Finalized (فاکتور: مبلغ نسیه و سررسید)
  else بیش از سقف یا سررسیدگذشته
    AR-->>API: Rejected(reason)
    API->>DB: ROLLBACK
    API-->>K: 409 credit-limit-exceeded / credit-overdue یا 403 approval-required
  end
  Note over M,API: چند روز بعد
  M->>K: پرداخت بخشی از بدهی
  K->>API: recordCollection {amount, Cash, shiftId} (key)
  API->>DB: tx: lock account، FIFO allocation روی قدیمی‌ترین Charge، نقد شیفت، Journal
  API-->>K: Collection + allocations
```

### پایش روزانه (PM-07)
```mermaid
flowchart LR
  T(["⏱ 09:00 به وقت Tenant"]) --> A["Chargeهای باز با dueDate نزدیک یا گذشته"]
  A --> B{"یادآوری فعال و رضایت عملیاتی؟"}
  B -- "بله" --> C["DueReminderRequested ← F-06"]
  B -- "خیر" --> D
  C --> D{"سررسید > N روز گذشته؟"}
  D -- "بله" --> E["حساب Suspended + اعلان به مدیر (CRD-008)"]
  D -- "خیر" --> F(["پایان"])
  E --> F
```

---

## F-08 — مؤدیان: راه‌اندازی کلید، ارسال، رد و اصلاح (UC-07)

### راه‌اندازی کلید (یک‌بار برای هر واحد صدور)
```mermaid
sequenceDiagram
  autonumber
  actor O as صاحب فروشگاه
  participant A as Merchant Admin
  participant API as Api (TaxCompliance)
  participant KMS as OpenBao Transit
  participant MOAD as کارپوشه سامانه مؤدیان

  O->>A: تکمیل پروفایل مالیاتی
  A->>API: updateTaxProfile
  O->>A: «تولید کلید» برای شعبه
  A->>API: generateSigningKey (key)
  API->>KMS: create key exportable=false
  KMS-->>API: public key
  API-->>A: SigningKey Generated
  A->>API: getSigningKeyCsr
  API-->>A: CSR (PEM)
  O->>MOAD: ثبت کلید عمومی و دریافت شناسه حافظه (خارج از سیستم ما)
  O->>A: ورود شناسه حافظه، activateSigningKey
  A->>API: activateSigningKey
  A->>API: testTaxConnection
  API-->>A: ok (ارسال فعال)
```

### ارسال، رد و اصلاح
```mermaid
sequenceDiagram
  autonumber
  participant OB as Outbox Relay
  participant TX as Workers (PM-04)
  participant TSS as TaxSigningService
  participant KMS as OpenBao Transit
  participant MOAD as سامانه مؤدیان
  participant DB as PostgreSQL

  OB->>TX: TaxInvoiceRequested(saleId)
  TX->>DB: inbox dedup، build TaxInvoice از Snapshot، content_hash، deadline_at از Policy
  alt داده ناقص (مثلاً شناسه کالا)
    TX->>DB: NeedsReview + هشدار داشبورد
  else کامل
    TX->>TSS: Sign(tenantId, taxInvoiceId) over mTLS
    TSS->>DB: read payload as rc_tax_signer (RLS tenant)
    TSS->>TSS: verify hash، status Pending، source sale exists
    TSS->>KMS: transit sign (key of tenant/unit)
    KMS-->>TSS: signature
    TSS-->>TX: signature + keyRef
    TX->>DB: status Signed
    TX->>MOAD: submit
    MOAD-->>TX: tracking id
    TX->>DB: status Sent
    loop Inquiry با Backoff
      TX->>MOAD: inquiry
      MOAD-->>TX: pending / accepted / rejected
    end
    alt Accepted
      TX->>DB: Accepted، event TaxInvoiceAccepted
    else Rejected
      TX->>DB: Rejected + errorCodes، هشدار
      Note over TX,DB: کاربر داده را اصلاح می‌کند و retryTaxInvoice را صدا می‌زند ← صورتحساب جدید related_to، قبلی Superseded
    end
  end
```

**هشدار مهلت (BR-076):** Job ساعتی صورتحساب‌های `Pending/NeedsReview/Rejected` را با `deadline_at` مقایسه می‌کند و در ۵۰، ۸۰ و ۹۵ درصد زمان هشدار می‌دهد.

---

## F-09 — خط لوله Posting و مثال‌های عددی

### خط لوله
```mermaid
flowchart LR
  E["رویداد مالی در ماژول مبدأ<br/>(Sale، Return، Receipt، Collection، ...)"] --> R["PostingRequest<br/>eventType + مؤلفه‌های مبلغ + sourceRef"]
  R --> P["Accounting: PostingRuleSet نسخه فعال"]
  P --> M["AccountMapping: نقش ← حساب Tenant (و شعبه)"]
  M --> J["JournalEntry Draft + Lines"]
  J --> V["UPDATE Posted<br/>Trigger: دوره Open، بازه تاریخ"]
  V --> C["COMMIT: Constraint Trigger تراز، ≥2 سطر، تفصیلی"]
```

### مثال‌ها (نرخ مالیات فرضی ۱۰٪، TaxExclusive، WAC = ۳۰۰٬۰۰۰ ریال)

> ارقام صرفاً آموزشی‌اند (همانند §11.4 سند مرجع). نرخ واقعی مالیات از Tax Policy نسخه‌دار می‌آید. همه اسناد زیر متوازن‌اند و با یک اسکریپت بررسی شده‌اند (بخش 17).

| # | رویداد | بدهکار | بستانکار |
|---|---|---|---|
| J1 | فروش نقد ۲ × ۵۰۰٬۰۰۰ | صندوق ۱٬۱۰۰٬۰۰۰ | فروش ۱٬۰۰۰٬۰۰۰ · مالیات بر ارزش افزوده پرداختنی ۱۰۰٬۰۰۰ |
| J2 | بهای تمام‌شده J1 | بهای تمام‌شده کالای فروش‌رفته ۶۰۰٬۰۰۰ | موجودی کالا ۶۰۰٬۰۰۰ |
| J3 | همان فروش، پرداخت ترکیبی: ۵۰۰٬۰۰۰ نقد + ۶۰۰٬۰۰۰ کارتخوان | صندوق ۵۰۰٬۰۰۰ · وجوه کارتخوان در راه ۶۰۰٬۰۰۰ | فروش ۱٬۰۰۰٬۰۰۰ · مالیات پرداختنی ۱۰۰٬۰۰۰ |
| J4 | تطبیق تسویه کارتخوان (PAY-002.2) | بانک ۶۰۰٬۰۰۰ | وجوه کارتخوان در راه ۶۰۰٬۰۰۰ |
| J5 | فروش نسیه ۱٬۱۰۰٬۰۰۰ | حساب‌های دریافتنی (مشتری X) ۱٬۱۰۰٬۰۰۰ | فروش ۱٬۰۰۰٬۰۰۰ · مالیات پرداختنی ۱۰۰٬۰۰۰ |
| J6 | دریافت نقد از مشتری X | صندوق ۱٬۱۰۰٬۰۰۰ | حساب‌های دریافتنی (مشتری X) ۱٬۱۰۰٬۰۰۰ |
| J7 | برگشت ۱ واحد از J1 (نقد) | برگشت از فروش ۵۰۰٬۰۰۰ · مالیات پرداختنی ۵۰٬۰۰۰ | صندوق ۵۵۰٬۰۰۰ |
| J8 | برگشت بهای J7 (به costAtSale) | موجودی کالا ۳۰۰٬۰۰۰ | بهای تمام‌شده ۳۰۰٬۰۰۰ |
| J9 | رسید خرید ۱۰ × ۲۸۰٬۰۰۰ + مالیات خرید ۱۰٪ | موجودی کالا ۲٬۸۰۰٬۰۰۰ · مالیات خرید (اعتبار) ۲۸۰٬۰۰۰ | حساب‌های پرداختنی (تأمین‌کننده Y) ۳٬۰۸۰٬۰۰۰ |
| J10 | کسری انبار ۱ واحد | هزینه کسری و ضایعات ۳۰۰٬۰۰۰ | موجودی کالا ۳۰۰٬۰۰۰ |
| J11 | کسری صندوق | کسری صندوق ۱۰۰٬۰۰۰ | صندوق ۱۰۰٬۰۰۰ |
| J12 | فروش با درگاه آنلاین ۱٬۱۰۰٬۰۰۰ | وجوه درگاه در راه ۱٬۱۰۰٬۰۰۰ | فروش ۱٬۰۰۰٬۰۰۰ · مالیات پرداختنی ۱۰۰٬۰۰۰ |
| J13 | سوخت مطالبات مشتری Z | هزینه مطالبات سوخت‌شده ۲۰۰٬۰۰۰ | حساب‌های دریافتنی (مشتری Z) ۲۰۰٬۰۰۰ |
| J14 | موجودی افتتاحیه ۵ × ۳۰۰٬۰۰۰ | موجودی کالا ۱٬۵۰۰٬۰۰۰ | تعدیلات افتتاحیه (حقوق مالکانه) ۱٬۵۰۰٬۰۰۰ |
| J15 | اصلاح WAC پس از موجودی منفی (BR-043؛ مثال F-10) | موجودی کالا ۴۰٬۰۰۰ | تعدیل بهای موجودی ۴۰٬۰۰۰ |

**تصمیم `[DEC D-59]`:** نقش حساب جدید **`CardInTransit`** (وجوه کارتخوان در راه) به فهرست D-30 اضافه می‌شود. فروش با کارتخوان مستقیم به «بانک» ثبت نمی‌شود، چون وجه یک تا دو روز بعد و گاهی با کسر کارمزد تسویه می‌شود. تطبیق فایل تسویه (J4) آن را صفر می‌کند و اختلاف به «کارمزد بانکی» می‌رود.

### بستن دوره
```mermaid
flowchart TB
  P0(["حسابدار: closePeriod"]) --> P1{"صورتحساب Pending یا NeedsReview در این دوره؟"}
  P1 -- "بله" --> P2["هشدار (مانع نیست): مؤدیان مستقل از دوره حسابداری است"]
  P1 -- "خیر" --> P3
  P2 --> P3{"سند Draft در دوره؟"}
  P3 -- "بله" --> X1["⚡ 409: ابتدا Post یا حذف Draftها"]
  P3 -- "خیر" --> P4["Lock دوره (exclusive)، status=Closed"]
  P4 --> P5["PeriodClosed ← Reporting و Tax (هشدار)"]
  P5 --> P6(["از این پس: رویدادهای دیررس با تاریخ مؤثر امروز در دوره باز (ACC-009)"])
```

---

## F-10 — رسید خرید و WAC (UC-05)

```mermaid
sequenceDiagram
  autonumber
  actor W as انباردار
  participant API as Api (Procurement)
  participant INV as Inventory port
  participant ACC as Accounting port
  participant DB as PostgreSQL

  W->>API: postGoodsReceipt {supplier, warehouse, lines: qty, unit, unitCost} (key)
  API->>DB: BEGIN، posted_source(GoodsReceipt, id)
  API->>INV: IStockReceiver.Receive(lines)
  INV->>DB: lock balances (warehouse, variant)
  INV->>INV: newWAC = round((onHand×WAC + qty×cost) / (onHand+qty))
  INV->>DB: movements (unit_cost)، balance (on_hand, wac, value)
  API->>ACC: Post(GoodsReceipt{inventory, inputVat, payable})
  API->>DB: COMMIT
  API-->>W: GoodsReceipt + newWac هر قلم + warnings
```

| حالت | قبل | ورود | WAC جدید | توضیح |
|---|---|---|---|---|
| عادی | ۱۰ واحد، WAC ۳۰۰٬۰۰۰، ارزش ۳٬۰۰۰٬۰۰۰ | ۱۰ × ۲۸۰٬۰۰۰ = ۲٬۸۰۰٬۰۰۰ | round(5٬800٬000 ÷ 20) = **۲۹۰٬۰۰۰** | BR-042 |
| پس از موجودی منفی | −۲ واحد، WAC ۳۰۰٬۰۰۰، ارزش −۶۰۰٬۰۰۰ | ۱۰ × ۲۸۰٬۰۰۰ | **۲۸۰٬۰۰۰** (= بهای ورودی) | BR-043: ارزش باید ۸ × ۲۸۰٬۰۰۰ = ۲٬۲۴۰٬۰۰۰ باشد. Σ ارزش Ledger = ۲٬۲۰۰٬۰۰۰ ← اختلاف ۴۰٬۰۰۰ با سند J15 |
| بهای صفر (جایزه یا هدیه) | ۱۰ @ ۳۰۰٬۰۰۰ | ۲ × ۰ | round(3٬000٬000 ÷ 12) = ۲۵۰٬۰۰۰ | هشدار و تأیید صریح کاربر (UC-05) |

---

## F-11 — انبارگردانی (UC-09)

```mermaid
sequenceDiagram
  autonumber
  actor C1 as شمارنده ۱
  actor C2 as شمارنده ۲
  actor BM as مدیر شعبه
  participant API as Api (Inventory)
  participant DB as PostgreSQL

  BM->>API: createCount {warehouse, scope, blind}
  API->>DB: snapshot onHand هر Variant در snapshotAt
  par شمارش موازی
    C1->>API: addCountEntries {entries, clientBatchId}
  and
    C2->>API: addCountEntries {entries, clientBatchId}
  end
  Note over API,DB: فروش‌ها ادامه دارند و Movementها ثبت می‌شوند
  BM->>API: getCountResult
  API->>DB: difference = counted − (snapshot + Σ movements since snapshotAt)
  API-->>BM: اختلاف‌ها و ارزش (WAC)، approvalRequired؟
  BM->>API: confirmCount (key)
  alt شمارنده = تأییدکننده یا ارزش > آستانه
    API-->>BM: 403 approval-required ← F-14
  else مجاز
    API->>DB: tx: CountAdjustment movements + journal (J10 یا اضافی)
    API-->>BM: Adjustment
  end
```

---

## F-12 — شیفت و مغایرت صندوق (UC-06)

```mermaid
stateDiagram-v2
  [*] --> Open: openShift (openingCash)
  Open --> Open: sale cash / refund cash / cash movement
  Open --> Closing: closeShift begins (no new sales)
  Closing --> Closed: counted, |variance| ≤ threshold
  Closing --> PendingApproval: |variance| > threshold
  PendingApproval --> Closed: approveShiftVariance (supervisor ≠ cashier)
  Closed --> [*]
```

`PendingApproval` یک زیروضعیت منطقی `Closing` است: در DB `status=Closing` و `approval_request_id` تنظیم‌شده `[DEC D-60]`. نقد مورد انتظار در حالت Blind Close فقط پس از ثبت شمارش نمایش داده می‌شود (D-24).

---

## F-13 — Draft آفلاین و Sync (ADR-006)

```mermaid
sequenceDiagram
  autonumber
  actor K as صندوق‌دار
  participant P as POS PWA
  participant SW as Service Worker و IndexedDB
  participant API as Api

  Note over P: اتصال قطع شد
  P-->>K: بنر «آفلاین — فروش نهایی ممکن نیست»
  K->>P: اسکن اقلام
  P->>SW: جستجوی کاتالوگ Cache (نام، بارکد، قیمت فروش)
  P->>SW: ذخیره Draft {clientDraftId ULID, lines}
  Note over P: دکمه پرداخت و Finalize غیرفعال
  Note over P: اتصال برقرار شد
  P->>API: createSale {clientDraftId} (اگر تکراری: همان Sale)
  P->>API: addSaleLine × n (قیمت‌ها سمت سرور دوباره محاسبه می‌شوند)
  API-->>P: Sale (اختلاف قیمت Cache با سرور در warnings)
  P->>SW: حذف Draft محلی پس از تأیید سرور
  K->>P: ادامه عادی F-02
```

---

## F-14 — Approval لحظه‌ای (IAM-007)

```mermaid
sequenceDiagram
  autonumber
  actor K as صندوق‌دار
  actor BM as مدیر شعبه
  participant P as POS
  participant API as Api

  K->>P: تخفیف ۲۰٪ (سقف نقش ۰٪)
  P->>API: applyDiscount
  API-->>P: 403 approval-required + approvalRequestId + context(limit, value)
  alt مدیر روی همان دستگاه
    BM->>P: ورود PIN و MFA
    P->>API: decideApproval {Approve, pin}
  else مدیر از دستگاه خودش
    API-->>BM: اعلان درون‌برنامه‌ای
    BM->>API: decideApproval {Approve}
  end
  API->>API: approver ≠ requester، مجوز و Scope مدیر، MFA تازه
  API-->>P: Approved
  P->>API: applyDiscount {approvalId}
  API-->>P: Sale (تخفیف با approvalRef ممیزی‌شده)
```

اعتبار Approval: **۵ دقیقه**، یک‌بارمصرف، و فقط برای همان `action + resource + value` `[DEC D-61]`.

---

## F-15 — دسترسی JIT پشتیبانی (IAM-008)

```mermaid
flowchart TB
  S1(["Support: platformRequestSupportAccess<br/>(دلیل، مدت ≤ 4h)"]) --> S2["اعلان به TenantOwner"]
  S2 --> S3{"Owner تأیید می‌کند؟"}
  S3 -- "خیر" --> S4(["Rejected"])
  S3 -- "بله" --> S5["Grant تا grantedUntil<br/>Token Support با claim jit و Masking اجباری"]
  S5 --> S6["همه اقدام‌ها: Audit با actorKind=Support"]
  S6 --> S7{"⏱ پایان زمان یا لغو Owner؟"}
  S7 -- "بله" --> S8(["Revoked + گزارش اقدام‌ها به Owner"])
```

---

## 16. تصمیم‌ها و پرسش‌های باز

| ID | نوع | شرح |
|---|---|---|
| D-59 | DEC | نقش حساب `CardInTransit` برای کارتخوان مستقل؛ تسویه از طریق تطبیق فایل |
| D-60 | DEC | Approval مغایرت شیفت به‌صورت زیروضعیت Closing |
| D-61 | DEC | Approval پنج‌دقیقه‌ای، یک‌بارمصرف و مقید به action، resource و value |
| D-62 | DEC | پاسخ یکسان 401 برای QR منقضی، تکراری یا متعلق به Audience دیگر (بدون افشای دلیل دقیق به POS) |
| D-63 | DEC | حذف Draft آفلاین فقط پس از تأیید سرور |
| FQ-01 | OQ | کارمزد بانکی کارتخوان و درگاه: در تطبیق تسویه خودکار به هزینه ثبت شود؟ (Default: بله، با حساب «کارمزد بانکی») |
| FQ-02 | OQ | یادآوری سررسید نسیه: چند روز قبل و بعد؟ (Default: ۳ روز قبل، ۱ و ۷ روز بعد؛ پیش‌فرض خاموش) |
| FQ-03 | OQ | آیا مشتری می‌تواند بدون Membership فقط با OTP لحظه‌ای فاکتور بگیرد و بعداً عضو شود (اتصال فاکتورهای قبلی)؟ (Default: بله، فقط فاکتورهای همان شماره و همان Tenant، با تأیید مشتری) |
| FQ-04 | OQ | Settle درگاه‌هایی که مرحله جدا دارند: بلافاصله پس از Commit یا دسته‌ای پایان روز؟ (Default: بلافاصله) |

---

## 17. اعتبارسنجی این سند

- **Render همه نمودارها:** اسکریپت Mermaid 11.4.1 در Chromium (Playwright) همه بلوک‌های Mermaid این سند و سایر اسناد را Render کرد (نتیجه در گزارش Commit).
- **تراز اسناد F-09:** جمع بدهکار و بستانکار هر سند J1 تا J15 با یک اسکریپت محاسبه و برابر بود. مقادیر WAC جدول F-10 و سهم برگشت F-04 هم با همان اسکریپت بازمحاسبه شدند.
