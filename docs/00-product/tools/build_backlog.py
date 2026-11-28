#!/usr/bin/env python3
"""
Backlog source of truth for D12. Generates:
  docs/00-product/D12-backlog.csv      (import into Jira/YouTrack/GitLab; UTF-8 BOM)
  docs/00-product/D12-backlog-table.md (included in D12)
Checks:
  * every P0 requirement of D02 (incl. sub-ids via their parent) is covered by >= 1 story
  * every test id referenced by a story exists in docs/09-testing/test-catalog.csv
  * every dependency points to an existing story; no dependency cycles; dependency order respects phases
"""
import csv, pathlib, re, sys
from collections import defaultdict

DOCS = pathlib.Path(__file__).resolve().parents[2]

PHASE = {1: "Phase 1 — Platform Foundation (Gate B)", 2: "Phase 2 — Catalog & Inventory (Gate C)",
         3: "Phase 3 — POS, Payments, Accounting, Credit, Tax (Gate D)",
         4: "Phase 4 — Customer Identity & Notifications (Gate E)", 5: "Phase 5 — Pilot & Production Readiness (Gate F)"}

EPICS = {
    "EP-00": ("زیرساخت توسعه، BuildingBlocks و DevOps", 1),
    "EP-01": ("Tenant، شعب، صندوق و دستگاه", 1),
    "EP-02": ("هویت و دسترسی کاربران فروشگاه", 1),
    "EP-03": ("ممیزی زنجیره‌ای", 1),
    "EP-04": ("کاتالوگ و Resolve بارکد", 2),
    "EP-05": ("Variant، قیمت و تخفیف", 2),
    "EP-06": ("انبار و بهای تمام‌شده", 2),
    "EP-07": ("خرید و تأمین‌کننده", 2),
    "EP-08": ("POS، شیفت و آفلاین", 3),
    "EP-09": ("نهایی‌سازی فروش، فاکتور و برگشت", 3),
    "EP-10": ("پرداخت: نقد، کارتخوان، درگاه، بازپرداخت", 3),
    "EP-11": ("حسابداری دوبل", 3),
    "EP-12": ("نسیه و حساب‌های دریافتنی", 3),
    "EP-13": ("سامانه مؤدیان", 3),
    "EP-14": ("هویت سراسری مشتری و پورتال", 4),
    "EP-15": ("اعلان، پیامک و بله", 4),
    "EP-16": ("گزارش‌ها و API شرکا", 5),
    "EP-17": ("Platform Admin و Policyهای سراسری", 5),
    "EP-18": ("سخت‌سازی امنیت و انطباق", 5),
    "EP-19": ("کارایی، DR و آمادگی پایلوت", 5),
}

# Stories pulled earlier than their epic's phase because later work depends on them (checked below):
#   accounting core is needed by goods receipt/adjustments (phase 2);
#   minimal customer identity + SMS are needed by credit sales and receipt delivery (phase 3);
#   industry receipt template is needed by printing (phase 3); retention by customer deletion (phase 4).
PHASE_OVERRIDE = {"US-111": 2, "US-141": 3, "US-142": 3, "US-151": 3, "US-152": 3, "US-172": 3, "US-173": 4}

# (id, epic, story, reqs, tests, sp, deps)
S = [
    # ---------------- EP-00 foundation
    ("US-001", "EP-00", "به‌عنوان تیم توسعه، Monorepo با ساختار D04 §5.1 و Pipeline اولیه (Build، Unit، Architecture Tests R1–R9) می‌خواهیم تا مرز ماژول‌ها از روز اول کنترل شود.",
     ["NFR-MNT-001"], [], 8, []),
    ("US-002", "EP-00", "به‌عنوان توسعه‌دهنده، BuildingBlocks: Money، Quantity، TaxRate، Ids، Result و Clock با تست Property-based می‌خواهم تا محاسبات مالی یکسان باشد.",
     ["ACC-005", "ACC-008"], ["AT-105", "AT-108"], 5, ["US-001"]),
    ("US-003", "EP-00", "به‌عنوان توسعه‌دهنده، UnitOfWork مشترک بین DbContextها با تزریق `app.tenant_id` (SET LOCAL) و Guard می‌خواهم تا RLS همیشه فعال باشد.",
     ["NFR-SEC-003"], ["T01", "T05", "T06", "SEC-T-060"], 8, ["US-001", "SP-07"]),
    ("US-004", "EP-00", "به‌عنوان DBA، Migration Runner به ازای ماژول، Fitness RLS و اجرای `tests.sql` در CI می‌خواهم تا جدول بدون RLS وارد نشود.",
     ["NFR-SEC-003", "NFR-MNT-002"], ["SEC-T-061"], 5, ["US-003"]),
    ("US-005", "EP-00", "به‌عنوان توسعه‌دهنده، Outbox/Inbox و Relay در Workers (ADR-013) می‌خواهم تا اثرات بعدی بدون ثبت تکراری اجرا شوند.",
     ["NFR-OBS-002"], ["SEC-T-042"], 8, ["US-003"]),
    ("US-006", "EP-00", "به‌عنوان توسعه‌دهنده، Idempotency Filter (BR-030/031) و نگاشت خطا به problem+json می‌خواهم تا Retry امن باشد.",
     ["NFR-UX-004"], ["SEC-T-016"], 5, ["US-003"]),
    ("US-007", "EP-00", "به‌عنوان SRE، محیط‌های Dev (Compose)، Staging و Mirrorهای داخلی (Harbor، Nexus) با Build بدون اینترنت می‌خواهم.",
     ["NFR-CMP-002", "NFR-SEC-009"], ["SEC-T-072", "SEC-T-073"], 13, ["US-001"]),
    ("US-008", "EP-00", "به‌عنوان SRE، OpenTelemetry، Prometheus، Loki، Tempo، داشبوردهای پایه و Alertهای D11 می‌خواهم.",
     ["NFR-OBS-001", "NFR-OBS-002", "NFR-OBS-003"], [], 8, ["US-007"]),
    ("US-009", "EP-00", "به‌عنوان SRE، OpenBao (KV، Transit، PKI، Dynamic DB Credentials) و Kubernetes Auth می‌خواهم تا هیچ Secret ثابتی نباشد.",
     ["NFR-SEC-006"], ["SEC-T-074"], 8, ["US-007"]),
    ("US-010", "EP-00", "به‌عنوان QA، Generator تست امنیتی از OpenAPI (BOLA، Scope، نقش، Mass Assignment) در CI می‌خواهم.",
     ["NFR-SEC-002"], ["SEC-T-010", "SEC-T-011", "SEC-T-012", "SEC-T-013"], 8, ["US-006"]),
    # ---------------- EP-01 tenant
    ("US-011", "EP-01", "به‌عنوان صاحب فروشگاه، با ویزارد ثبت‌نام کنم و شعبه، انبار، صندوق، شجره حساب و دوره مالی پیش‌فرض خودکار ساخته شود.",
     ["TEN-001"], ["AT-001", "AT-290"], 8, ["US-003", "US-021"]),
    ("US-012", "EP-01", "به‌عنوان مدیر، شعب و صندوق‌های بیشتر تعریف کنم.", ["TEN-002"], ["AT-002"], 3, ["US-011"]),
    ("US-013", "EP-01", "به‌عنوان مدیر شعبه، دستگاه POS را با کد جفت‌سازی ده‌دقیقه‌ای ثبت و در صورت نیاز ابطال کنم.", ["TEN-002"], ["AT-003"], 5, ["US-012"]),
    ("US-014", "EP-01", "به‌عنوان پلتفرم، پلن، Trial و Entitlementها سمت سرور اعمال شوند.", ["TEN-003"], ["AT-004"], 5, ["US-011"]),
    ("US-015", "EP-01", "به‌عنوان پلتفرم، Tenant معلق Read-Only شود ولی صف مؤدیان و دریافت نسیه ادامه یابد.", ["TEN-004"], ["AT-005"], 3, ["US-014"]),
    ("US-016", "EP-01", "به‌عنوان صاحب فروشگاه، Export کامل داده با لینک امن و ممیزی بگیرم.", ["TEN-005"], ["AT-006", "SEC-T-066"], 5, ["US-031"]),
    # ---------------- EP-02 IAM
    ("SP-01", "EP-02", "Spike: Keycloak سه Realm، BFF با PKCE، و امکان OTP مشتری با Custom Authenticator یا Token Exchange (ADR-003).", ["ADR-003"], [], 5, []),
    ("US-021", "EP-02", "به‌عنوان کاربر فروشگاه، از طریق BFF وارد شوم (Session HttpOnly) و Tenant فعال را انتخاب کنم.",
     ["IAM-001", "IAM-002"], ["AT-010", "AT-011", "SEC-T-001", "SEC-T-003", "SEC-T-006"], 8, ["SP-01", "US-001"]),
    ("US-022", "EP-02", "به‌عنوان Owner، MFA (TOTP و WebAuthn) برای نقش‌های حساس اجباری باشد و Step-up برای عملیات حساس.",
     ["IAM-003"], ["AT-012", "SEC-T-070", "SEC-T-080"], 5, ["US-021"]),
    ("US-023", "EP-02", "به‌عنوان صندوق‌دار، روی Terminal ثبت‌شده با PIN قفل و باز کنم.", ["IAM-004"], ["AT-013"], 5, ["US-013", "US-021"]),
    ("US-024", "EP-02", "به‌عنوان کاربر، Sessionهایم را ببینم و از همه دستگاه‌ها خارج شوم؛ غیرفعال‌سازی کاربر فوراً اثر کند.",
     ["IAM-005", "IAM-009"], ["AT-014", "AT-018", "SEC-T-071"], 5, ["US-021"]),
    ("US-025", "EP-02", "به‌عنوان مدیر، کاربران را با نقش و Scope دعوت کنم؛ همه Endpointها Policy صریح داشته باشند (Default Deny).",
     ["IAM-001", "IAM-006"], ["AT-015", "SEC-T-012"], 8, ["US-021"]),
    ("US-026", "EP-02", "به‌عنوان مدیر، درخواست‌های تأیید لحظه‌ای را روی همان دستگاه یا از راه دور تأیید کنم (تأییدکننده ≠ درخواست‌کننده).",
     ["IAM-007"], ["AT-016"], 8, ["US-025"]),
    ("US-027", "EP-02", "به‌عنوان Owner، دسترسی JIT پشتیبانی را با زمان و Masking تأیید یا رد کنم.", ["IAM-008"], ["AT-017"], 5, ["US-025", "US-031"]),
    # ---------------- EP-03 audit
    ("US-031", "EP-03", "به‌عنوان ممیز، همه رویدادهای AUD-001 با زنجیره Hash (Trigger DB) ثبت شوند و وارسی روزانه انجام شود.",
     ["AUD-001", "AUD-002", "AUD-003"], ["AT-190", "AT-191", "AT-192", "AT-193", "T70", "T74"], 5, ["US-003"]),
    ("US-032", "EP-03", "به‌عنوان Auditor، رکوردهای ممیزی Tenant را جستجو کنم.", ["AUD-001"], [], 3, ["US-031"]),
    # ---------------- EP-04 catalog
    ("US-041", "EP-04", "به‌عنوان انباردار، کالا، Variant و بارکدهای چندگانه با واحد بسته‌بندی تعریف کنم.",
     ["CAT-001", "CAT-002"], ["AT-020", "AT-021", "AT-022", "T10"], 8, ["US-003"]),
    ("US-042", "EP-04", "به‌عنوان سیستم، بارکد EAN-8، EAN-13، UPC-A و GTIN-14 اعتبارسنجی و به GTIN-14 نرمال شود؛ کد داخلی فقط در Tenant بماند.",
     ["CAT-003"], ["AT-023", "AT-024"], 3, ["US-041"]),
    ("US-043", "EP-04", "به‌عنوان انباردار، با یک اسکن پیشنهاد مشخصات از Tenant، Global و Provider با منبع و Confidence بگیرم و پس از تأیید ذخیره کنم.",
     ["CAT-004", "CAT-005"], ["AT-025", "AT-026", "AT-200", "AT-201", "AT-202", "AT-203", "SEC-T-040", "SEC-T-041"], 13, ["US-041", "US-042", "SP-02"]),
    ("SP-02", "EP-04", "Spike: Open Food Facts (API، Rate Limit، ODbL) و اندازه‌گیری پوشش ۲۰۰ تا ۵۰۰ بارکد واقعی (KPI-07).", ["ADR-007"], [], 3, []),
    ("US-044", "EP-04", "به‌عنوان مدیر، کالای تکراری را با ممیزی Merge کنم و پیشنهاد کالا به کاتالوگ عمومی (Opt-in) بدهم.",
     ["CAT-005", "CAT-006"], ["AT-027", "AT-028"], 5, ["US-043"]),
    ("US-045", "EP-04", "به‌عنوان فروشگاه، Profile بارکد وزنی (وزن یا قیمت) تعریف کنم تا POS آن را Decode کند.", ["CAT-007"], ["AT-029", "AT-030"], 5, ["US-042"]),
    ("US-046", "EP-04", "به‌عنوان صندوق‌دار، کالا را با نام فارسی نرمال‌شده، SKU یا بارکد سریع پیدا کنم.", ["CAT-011"], ["AT-034"], 5, ["US-041"]),
    ("US-047", "EP-17", "به‌عنوان ناظر کاتالوگ، صف پیشنهادها را بررسی و منتشر یا رد کنم.", ["CAT-005"], [], 3, ["US-044"]),
    # ---------------- EP-05 merchandising
    ("US-051", "EP-05", "به‌عنوان مدیر بوتیک، ماتریس رنگ × سایز بسازم و موجودی هر Variant مستقل باشد.", ["CAT-008"], ["AT-031"], 5, ["US-041"]),
    ("US-052", "EP-05", "به‌عنوان مدیر، قیمت نسخه‌دار با validFrom تعیین کنم؛ تغییرات بزرگ تأیید بخواهند.", ["CAT-009"], ["AT-032", "T11", "SEC-T-105"], 5, ["US-041", "US-026"]),
    ("US-053", "EP-05", "به‌عنوان مدیر، تخفیف دستی (با سقف نقش) و قاعده خودکار (بیشترین تخفیف) تعریف کنم.", ["CAT-010"], ["AT-033", "SEC-T-101"], 8, ["US-052", "US-026"]),
    # ---------------- EP-06 inventory
    ("US-061", "EP-06", "به‌عنوان سیستم، دفتر گردش Append-only و StockBalance هم‌تراکنش با Policy موجودی منفی و WAC.",
     ["INV-001", "INV-002", "INV-003", "INV-005", "INV-006"], ["AT-040", "AT-041", "AT-042", "AT-045", "AT-046", "T12", "T13", "T14", "T15"], 13, ["US-041"]),
    ("US-062", "EP-06", "به‌عنوان سیستم، IStockIssuer با قفل مرتب و رزرو ۱۵ دقیقه‌ای برای پرداخت آنلاین.", ["INV-004"], ["AT-043", "AT-044"], 8, ["US-061"]),
    ("US-063", "EP-06", "به‌عنوان مدیر، تعدیل موجودی با دلیل و Approval بالای آستانه ثبت کنم.", ["INV-002"], ["SEC-T-104"], 5, ["US-061", "US-026", "US-111"]),
    ("US-064", "EP-06", "به‌عنوان انباردار، انتقال بین انبارها ثبت کنم.", ["INV-008"], ["AT-047"], 3, ["US-061"]),
    ("US-065", "EP-06", "به‌عنوان انباردار، انبارگردانی با Snapshot و چند شمارنده هم‌زمان (حتی بدون شبکه) انجام دهم.", ["INV-009"], ["AT-280"], 8, ["US-061", "US-063"]),
    ("US-066", "EP-06", "به‌عنوان مدیر، موجودی کم را ببینم و هشدار بگیرم.", ["INV-010"], ["AT-048"], 3, ["US-061"]),
    # ---------------- EP-07 procurement
    ("US-071", "EP-07", "به‌عنوان مسئول خرید، تأمین‌کننده تعریف و رسید خرید با WAC و سند در یک تراکنش ثبت کنم.", ["PRC-001", "PRC-002"], ["AT-050", "AT-240"], 8, ["US-061", "US-111"]),
    ("US-072", "EP-07", "به‌عنوان حسابدار، پرداخت به تأمین‌کننده و برگشت از خرید ثبت کنم.", ["PRC-003", "PRC-004"], ["AT-051", "AT-052"], 5, ["US-071"]),
    # ---------------- EP-08 POS & shift
    ("SP-06", "EP-08", "Spike: اسکن دوربین PWA (BarcodeDetector و zxing-wasm) روی دستگاه‌های رایج پایلوت (TQ-04)؛ Torch و Debounce.", ["SAL-002"], [], 3, []),
    ("US-081", "EP-08", "به‌عنوان صندوق‌دار، شیفت را باز کنم، ورود و خروج نقد ثبت کنم و با Blind Close ببندم.", ["SAL-009"], ["AT-250", "AT-251", "T20"], 8, ["US-023"]),
    ("US-082", "EP-08", "به‌عنوان صندوق‌دار، سبد بسازم، با دوربین یا بارکدخوان اسکن کنم، مقدار و واحد تغییر دهم.", ["SAL-001", "SAL-002", "SAL-010"], ["AT-060", "AT-061", "AT-068"], 13, ["US-081", "US-046", "SP-06"]),
    ("US-083", "EP-08", "به‌عنوان صندوق‌دار، سبد را نگه دارم و بعداً ادامه دهم؛ Draftهای قدیمی خودکار لغو شوند.", ["SAL-011"], ["AT-069"], 3, ["US-082"]),
    ("US-084", "EP-08", "به‌عنوان صندوق‌دار، در قطعی اینترنت سبد Draft آماده کنم و پس از اتصال بدون تکرار Sync شود.", ["SAL-013"], ["AT-071", "T29", "SEC-T-004"], 8, ["US-082"]),
    # ---------------- EP-09 finalize & returns
    ("US-091", "EP-09", "به‌عنوان صندوق‌دار، فروش را اتمی نهایی کنم: قیمت سرور، شماره فاکتور، گردش، سند و Outbox، و Retry امن.",
     ["SAL-004", "SAL-005", "SAL-007"], ["AT-062", "AT-063", "AT-064", "AT-067", "AT-210", "AT-211", "AT-213", "AT-214", "T21", "T22", "T23", "T24", "T25", "T28"], 13, ["US-062", "US-101", "US-111", "US-006"]),
    ("US-092", "EP-09", "به‌عنوان صندوق‌دار، فاکتور را با قالب صنف (۸۰mm و A4) چاپ کنم و QR فاکتور دیجیتال داشته باشد.", ["SAL-012"], ["AT-070", "SEC-T-002"], 5, ["US-091", "US-172"]),
    ("US-093", "EP-09", "به‌عنوان مدیر شعبه، برگشت کامل یا جزئی با سهم دقیق مبلغ، وضعیت کالا و بازپرداخت ثبت کنم.", ["SAL-008"], ["AT-230", "AT-231", "AT-232", "AT-233", "T26", "T27", "SEC-T-102"], 13, ["US-091", "US-104"]),
    ("US-094", "EP-09", "به‌عنوان صندوق‌دار، مشتری عضو را با QR یا کد به فاکتور وصل کنم و اطلاعات خریدار مالیاتی (اختیاری) وارد کنم.", ["SAL-003", "CUS-006"], ["AT-126"], 5, ["US-091", "US-142"]),
    # ---------------- EP-10 payments
    ("SP-03", "EP-10", "Spike: مستندات و Sandbox دو PSP اول (OQ-08): Verify، Settle، Reverse، Refund، Inquiry، فایل تسویه.", ["ADR-012"], [], 5, []),
    ("US-101", "EP-10", "به‌عنوان صندوق‌دار، پرداخت نقد (با باقیمانده) و کارتخوان مستقل (با شماره پیگیری یکتا) ثبت کنم.", ["PAY-001", "PAY-002", "SAL-006"], ["AT-065", "AT-066", "AT-080", "AT-081", "AT-082", "SEC-T-102"], 8, ["US-082"]),
    ("US-102", "EP-10", "به‌عنوان صندوق‌دار، پرداخت آنلاین با QR درگاه بگیرم؛ تأیید فقط با Verify سرور.", ["PAY-003", "PAY-004", "PAY-007"], ["AT-084", "AT-085", "AT-086", "AT-087", "AT-088", "AT-090", "AT-270", "AT-271", "AT-272", "SEC-T-030", "SEC-T-032"], 13, ["US-101", "US-062", "SP-03"]),
    ("US-103", "EP-10", "به‌عنوان مدیر، اعتبارنامه درگاه فروشگاه را ثبت کنم (در OpenBao، نمایش Masked).", ["PAY-005"], ["AT-089"], 3, ["US-009"]),
    ("US-104", "EP-10", "به‌عنوان مدیر، بازپرداخت نقد، کارتخوان و آنلاین را با Approval و پیگیری انجام دهم.", ["PAY-006"], [], 8, ["US-102", "US-026"]),
    ("US-105", "EP-10", "به‌عنوان حسابدار، فایل تسویه را Import و با پرداخت‌های کارتخوان و درگاه تطبیق دهم.", ["PAY-002"], ["AT-083"], 8, ["US-101", "US-111"]),
    # ---------------- EP-11 accounting
    ("US-111", "EP-11", "به‌عنوان سیستم، شجره حساب، دوره‌های شمسی، سند دوبل با تراز و تغییرناپذیری در DB، و PostingRule با Mapping نقش.",
     ["ACC-001", "ACC-002", "ACC-003", "ACC-004", "ACC-006", "ACC-008"], ["AT-100", "AT-101", "AT-102", "AT-103", "AT-104", "AT-106", "T30", "T31", "T32", "T33", "T34", "T35", "T38", "T39"], 13, ["US-003", "US-002"]),
    ("US-112", "EP-11", "به‌عنوان حسابدار، سند دستی Draft بسازم، Post و Reverse کنم.", ["ACC-003", "ACC-006"], [], 5, ["US-111"]),
    ("US-113", "EP-11", "به‌عنوان حسابدار، دوره را ببندم؛ رویدادهای دیررس به دوره باز بروند.", ["ACC-009"], ["AT-109", "T36", "T37"], 5, ["US-111"]),
    ("US-114", "EP-11", "به‌عنوان حسابدار، دفتر روزنامه، کل، معین، تراز آزمایشی و تراز صندوق را ببینم و Export کنم.", ["ACC-007"], ["AT-107"], 8, ["US-111"]),
    # ---------------- EP-12 credit
    ("US-121", "EP-12", "به‌عنوان حسابدار، حساب اعتباری با هویت اعتباری رمزنگاری‌شده، سقف و مهلت بسازم.", ["CRD-001"], ["AT-300", "SEC-T-106"], 5, ["US-111", "US-142"]),
    ("US-122", "EP-12", "به‌عنوان صندوق‌دار، فروش نسیه انجام دهم و سقف داخل تراکنش Finalize کنترل شود.", ["CRD-002", "CRD-003", "CRD-011"], ["AT-301", "AT-309", "AT-310", "T40", "T41", "T42", "T43"], 8, ["US-121", "US-091"]),
    ("US-123", "EP-12", "به‌عنوان صندوق‌دار یا حسابدار، دریافت از مشتری با تخصیص FIFO ثبت کنم.", ["CRD-004"], ["AT-302", "AT-311", "AT-312"], 5, ["US-122"]),
    ("US-124", "EP-12", "به‌عنوان حسابدار، سنی مطالبات و صورت‌حساب مشتری ببینم و سوخت مطالبات با Approval ثبت کنم.", ["CRD-006", "CRD-010"], ["AT-304", "AT-308"], 5, ["US-123"]),
    ("US-125", "EP-12", "به‌عنوان سیستم، یادآوری سررسید و مسدودی خودکار طبق تنظیم Tenant.", ["CRD-007", "CRD-008"], ["AT-305", "AT-306"], 3, ["US-123", "US-152"]),
    # ---------------- EP-13 tax
    ("SP-04", "EP-13", "Spike: آخرین مستندات API سامانه مؤدیان، الگوها، امضا و الگوریتم، و Sandbox؛ تأیید OpenBao Transit یا نیاز به HSM (AQ-05).", ["ADR-011"], [], 8, []),
    ("US-131", "EP-13", "به‌عنوان صاحب فروشگاه، پروفایل مالیاتی و شناسه حافظه هر شعبه را ثبت و اتصال را تست کنم.", ["TAX-001"], ["AT-160"], 5, ["SP-04"]),
    ("US-132", "EP-13", "به‌عنوان صاحب فروشگاه، کلید امضا در KMS تولید، CSR دریافت و کلید را فعال کنم.", ["TAX-002"], ["AT-161", "SEC-T-052"], 8, ["US-131", "US-009"]),
    ("US-133", "EP-13", "به‌عنوان سیستم، از Snapshot فاکتور صورتحساب بسازم، در TaxSigningService ایزوله امضا کنم و ارسال و استعلام کنم.",
     ["TAX-003", "TAX-004", "TAX-005", "TAX-008"], ["AT-162", "AT-163", "AT-166", "AT-260", "AT-261", "AT-262", "T60", "T61", "T62", "SEC-T-050", "SEC-T-051"], 13, ["US-132", "US-091", "US-005"]),
    ("US-134", "EP-13", "به‌عنوان سیستم، برای برگشت از فروش صورتحساب برگشتی مرتبط ارسال شود.", ["TAX-006"], ["AT-164", "AT-263"], 5, ["US-133", "US-093"]),
    ("US-135", "EP-13", "به‌عنوان حسابدار، داشبورد صف و مهلت‌ها را ببینم و ردشده‌ها را اصلاح و دوباره ارسال کنم.", ["TAX-007"], ["AT-165"], 5, ["US-133"]),
    # ---------------- EP-14 customer identity
    ("US-141", "EP-14", "به‌عنوان مشتری، با OTP (Rate Limit و ضد SMS Pumping) وارد شوم و کد عمومی بگیرم.", ["CUS-001", "CUS-002"], ["AT-120", "AT-121", "AT-122", "T50", "T51", "SEC-T-043"], 8, ["SP-01", "US-152"]),
    ("US-142", "EP-14", "به‌عنوان مشتری، با QR پیشخوان عضو فروشگاه شوم و رضایت‌های جدا ثبت کنم.", ["CUS-003", "CUS-005", "CUS-007"], ["AT-123", "AT-125", "AT-127", "AT-222", "AT-223", "T52", "T53", "T54"], 8, ["US-141"]),
    ("US-143", "EP-14", "به‌عنوان صندوق‌دار، مشتری را با QR کوتاه‌عمر یا کد شناسایی کنم و عضویت جدید فقط با اثبات مالکیت بسازم.", ["CUS-004", "CUS-005"], ["AT-124", "AT-220", "AT-221", "SEC-T-107"], 8, ["US-142"]),
    ("US-144", "EP-14", "به‌عنوان مشتری، Receipt Link امن ببینم؛ مهمان با OTP لحظه‌ای فاکتور بگیرد.", ["CUS-009", "CUS-011"], ["AT-129", "AT-131", "SEC-T-108"], 5, ["US-091", "US-141"]),
    ("US-145", "EP-14", "به‌عنوان مشتری، در پورتال فروشگاه‌ها، رضایت‌ها، فاکتورها، نسیه و تاریخچه دسترسی را ببینم.", ["CUS-010", "CRD-005", "CRD-009"], ["AT-130", "AT-303", "AT-307", "AT-313"], 8, ["US-142", "US-123", "US-102"]),
    ("US-146", "EP-14", "به‌عنوان پلتفرم، Merge امن دو هویت مشتری با اثبات مالکیت انجام شود.", ["CUS-008"], ["AT-128"], 5, ["US-141"]),
    ("US-147", "EP-14", "به‌عنوان مشتری، داده‌ام را Export کنم و حذف حساب درخواست دهم.", ["NFR-PRIV-003"], [], 3, ["US-145", "US-173"]),
    # ---------------- EP-15 notifications
    ("SP-05", "EP-15", "Spike: Bot API بله (sendMessage، setWebhook، Deep Link، احراز Webhook، محدودیت ارسال) در Sandbox (ADR-008).", ["ADR-008"], [], 3, []),
    ("US-151", "EP-15", "به‌عنوان سیستم، Notification Gateway با Retry، Backoff، Dead Letter و وضعیت صادق.", ["NOT-001", "NOT-005", "NOT-006", "NOT-007"], ["AT-140", "AT-144", "AT-145", "AT-148"], 8, ["US-005"]),
    ("US-152", "EP-15", "به‌عنوان سیستم، SMS Adapter با Provider داخلی و قالب‌های نسخه‌دار.", ["NOT-001", "NOT-009"], ["AT-147"], 5, ["US-151"]),
    ("US-153", "EP-15", "به‌عنوان مشتری، فاکتور را از بله یا پیامک (با Fallback و رضایت) بگیرم.", ["NOT-002", "NOT-008"], ["AT-141", "AT-146", "AT-212"], 5, ["US-151", "US-144"]),
    ("US-154", "EP-15", "به‌عنوان مشتری، حساب بله را با Deep Link یک‌بارمصرف وصل کنم (پشت Feature Flag).", ["NOT-003", "NOT-004"], ["AT-142", "AT-143", "SEC-T-031"], 8, ["SP-05", "US-151", "US-141"]),
    # ---------------- EP-16 reports & integrations
    ("US-161", "EP-16", "به‌عنوان مدیر، گزارش‌های فروش روزانه، موجودی، برگشتی و سود ناخالص از Read Model با Export ببینم.",
     ["RPT-001", "RPT-002", "RPT-003"], ["AT-170", "AT-171", "AT-172", "AT-173", "AT-174", "AT-175"], 13, ["US-091", "US-061"]),
    ("US-162", "EP-16", "به‌عنوان شریک، با Integration Key کاتالوگ و موجودی را بخوانم.", ["INT-001"], ["AT-180"], 5, ["US-061"]),
    ("US-163", "EP-16", "به‌عنوان SRE، همه Adapterها Timeout، Retry، Circuit Breaker و Metric داشته باشند.", ["INT-002"], ["AT-181"], 3, ["US-102", "US-152", "US-133"]),
    # ---------------- EP-17 platform admin
    ("US-171", "EP-17", "به‌عنوان اپراتور پلتفرم، Tenantها را مدیریت و برای پشتیبانی درخواست JIT ثبت کنم.", ["TEN-004", "IAM-008"], ["SEC-T-082"], 5, ["US-027"]),
    ("US-172", "EP-17", "به‌عنوان اپراتور پلتفرم، قالب فاکتور هر صنف (متن قانونی قفل) را نسخه‌بندی کنم؛ فروشگاه فقط بخش‌های مجاز را تغییر دهد.", ["SAL-012"], [], 5, ["US-011"]),
    ("US-173", "EP-17", "به‌عنوان اپراتور پلتفرم، RetentionPolicy به ازای دسته داده با کف قانونی تعریف کنم و Job اجرا شود.", ["TEN-005"], ["AT-007"], 8, ["US-031"]),
    ("US-174", "EP-17", "به‌عنوان اپراتور پلتفرم، TaxSubmissionPolicy (سه‌ماهه، داینامیک) را نسخه‌بندی کنم.", ["TAX-009"], ["AT-167"], 3, ["US-133"]),
    # ---------------- EP-18 security
    ("US-181", "EP-18", "به‌عنوان Security، Headerها، CSP، CORS، Rate Limit سراسری و Egress Allowlist پیکربندی و تست شوند.",
     ["NFR-SEC-004", "NFR-SEC-007", "NFR-SEC-008"], ["SEC-T-001", "SEC-T-015", "SEC-T-040", "SEC-T-090"], 8, ["US-007"]),
    ("US-182", "EP-18", "به‌عنوان Security، رمزنگاری فیلدهای PII با Envelope و Blind Index، و Scrubber لاگ.", ["NFR-SEC-005", "NFR-PRIV-001", "NFR-PRIV-002", "NFR-PRIV-004"], ["SEC-T-044", "SEC-T-062"], 8, ["US-009"]),
    ("US-183", "EP-18", "به‌عنوان Security، قواعد کشف تقلب و Alertهای D06 §13 فعال و تست شوند.", ["NFR-OBS-003"], ["SEC-T-101", "SEC-T-103", "SEC-T-109", "SEC-T-110"], 8, ["US-008", "US-091"]),
    ("US-184", "EP-18", "به‌عنوان Security، چک‌لیست ASVS 5.0 L2 در سطح الزام با شاهد، و Pentest مستقل.", ["NFR-SEC-001", "NFR-SEC-010"], [], 13, ["US-181", "US-182"]),
    ("US-185", "EP-18", "به‌عنوان UX، دسترس‌پذیری WCAG 2.2 AA (axe در CI + بازبینی Screen Reader) و RTL و Bidi.", ["NFR-UX-001", "NFR-UX-002", "NFR-UX-003", "NFR-UX-005"], [], 8, ["US-082"]),
    # ---------------- EP-19 perf / DR / pilot
    ("US-191", "EP-19", "به‌عنوان QA، سناریوهای بار L1 تا L7 با k6 و Oracleها اجرا و SLOها پاس شوند.",
     ["NFR-PERF-001", "NFR-PERF-002", "NFR-PERF-003", "NFR-PERF-004", "NFR-PERF-005", "NFR-PERF-006", "NFR-PERF-007", "NFR-CAP-001", "NFR-CAP-003"], [], 13, ["US-091", "US-161"]),
    ("US-192", "EP-19", "به‌عنوان SRE، HA پایگاه داده، Backup، PITR، سایت DR و Restore Drill ماهانه.", ["NFR-AVL-001", "NFR-AVL-002", "NFR-AVL-003", "NFR-CMP-001"], [], 13, ["US-007"]),
    ("US-193", "EP-19", "به‌عنوان QA، Fault Injection FI-01 تا FI-12 و Degraded Mode تأیید شوند.", ["NFR-AVL-004"], [], 8, ["US-191"]),
    ("US-194", "EP-19", "به‌عنوان PM، پایلوت یک سوپرمارکت و یک بوتیک با UAT، آموزش و Runbook پشتیبانی.", ["NFR-CAP-002"], [], 13, ["US-191", "US-192", "US-184"]),
    ("US-195", "EP-19", "به‌عنوان QA، Partitioning، Retention و بایگانی سرد با داده یک‌ساله مصنوعی آزموده شوند.", ["NFR-CAP-002", "NFR-MNT-002"], [], 5, ["US-173", "US-191"]),
    ("US-196", "EP-19", "به‌عنوان Domain Lead، پوشش تست Domain ماژول‌های مالی ≥ ۹۰٪ و همه Invariantهای D03.", ["NFR-MNT-003"], [], 5, ["US-091", "US-111", "US-122"]),
    ("SP-07", "EP-00", "Spike: EF Core + Npgsql با Connection و Transaction مشترک بین DbContextها، PgBouncer Transaction Pooling و set_config (D04 §5.3).", ["ADR-004"], [], 3, []),
    ("SP-08", "EP-08", "Spike: چاپ حرارتی ESC/POS از PWA (Bridge یا Native Wrapper) — برای P1؛ MVP چاپ مرورگر.", ["SAL-012"], [], 2, []),
]


def p0_requirements():
    d = (DOCS / "00-product/D02-SRS-Functional-NonFunctional.md").read_text(encoding="utf-8")
    ids = []
    for line in d.splitlines():
        m = re.match(r"^\| ([A-Z]{2,4}-\d{3}(?:\.\d+)?)(?: \([^)]*\))? \| (.+?) \| (P\d[^|]*) \|", line)
        if m and m.group(3).startswith("P0"):
            ids.append(m.group(1))
    nfr = sorted(set(re.findall(r"^\| (NFR-[A-Z]+-\d{3}) \|", d, re.M)))
    return ids, nfr


def main():
    ids = {s[0] for s in S}
    assert len(ids) == len(S), "duplicate story id"
    errors = []
    # tests exist
    cat = {r["id"] for r in csv.DictReader(open(DOCS / "09-testing/test-catalog.csv", encoding="utf-8-sig"))}
    for sid, ep, _, reqs, tests, sp, deps in S:
        for t in tests:
            if t not in cat:
                errors.append(f"{sid}: test {t} not in catalog")
        for d in deps:
            if d not in ids:
                errors.append(f"{sid}: unknown dependency {d}")
        if sp not in (1, 2, 3, 5, 8, 13):
            errors.append(f"{sid}: non-Fibonacci estimate {sp}")
        if ep not in EPICS:
            errors.append(f"{sid}: unknown epic {ep}")
    # phase order: a story may not depend on a story of a later phase
    phase_of = {s[0]: PHASE_OVERRIDE.get(s[0], EPICS[s[1]][1]) for s in S}
    for sid, ep, _, _, _, _, deps in S:
        for d in deps:
            if phase_of[d] > phase_of[sid]:
                errors.append(f"{sid} (phase {phase_of[sid]}) depends on later-phase {d} (phase {phase_of[d]})")
    # cycles
    graph = {s[0]: s[6] for s in S}
    state = {}

    def visit(n, path):
        if state.get(n) == 1:
            errors.append("cycle: " + " -> ".join(path + [n])); return
        if state.get(n) == 2:
            return
        state[n] = 1
        for m in graph[n]:
            visit(m, path + [n])
        state[n] = 2
    for n in graph:
        visit(n, [])
    # coverage
    reqs, nfr = p0_requirements()
    covered = {r for s in S for r in s[3]}

    def is_covered(r):
        return r in covered or r.split(".")[0] in covered
    missing = [r for r in reqs if not is_covered(r)]
    missing_nfr = [r for r in nfr if r not in covered]
    # outputs
    with open(DOCS / "00-product/D12-backlog.csv", "w", encoding="utf-8-sig", newline="") as f:
        w = csv.writer(f)
        w.writerow(["id", "type", "epic", "epic_title", "phase", "story", "requirements", "tests", "estimate_sp", "depends_on"])
        for sid, ep, story, rq, ts, sp, deps in S:
            w.writerow([sid, "Spike" if sid.startswith("SP-") else "Story", ep, EPICS[ep][0], PHASE[phase_of[sid]],
                        story, " ".join(rq), " ".join(ts), sp, " ".join(deps)])
    lines = []
    tot = defaultdict(int)
    for ep, (title, ph) in EPICS.items():
        items = [s for s in S if s[1] == ep]
        sp = sum(s[5] for s in items)
        for it in items:
            tot[phase_of[it[0]]] += it[5]
        lines.append(f"\n#### {ep} — {title} · فاز {ph} · {sp} SP\n")
        lines.append("| ID | Story | نیازمندی | تست | SP | فاز | وابستگی |")
        lines.append("|---|---|---|---|---|---|---|")
        for sid, _, story, rq, ts, spp, deps in items:
            lines.append(f"| {sid} | {story} | {', '.join(rq)} | {', '.join(ts) or '—'} | {spp} | {phase_of[sid]} | {', '.join(deps) or '—'} |")
    (DOCS / "00-product/D12-backlog-table.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    n_sp = sum(s[5] for s in S)
    print(f"{len(S)} items ({sum(1 for s in S if s[0].startswith('SP-'))} spikes), {n_sp} SP; per phase: " +
          ", ".join(f"P{k}={v}" for k, v in sorted(tot.items())))
    print(f"P0 requirements covered: {len(reqs) - len(missing)}/{len(reqs)}; NFR covered: {len(nfr) - len(missing_nfr)}/{len(nfr)}")
    if missing:
        errors.append("uncovered P0 requirements: " + " ".join(missing))
    if missing_nfr:
        errors.append("uncovered NFRs: " + " ".join(missing_nfr))
    if errors:
        print("\n".join(errors)); sys.exit(1)
    print("BACKLOG CHECKS PASS")


if __name__ == "__main__":
    main()
