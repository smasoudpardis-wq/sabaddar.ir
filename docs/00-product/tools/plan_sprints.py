#!/usr/bin/env python3
"""
Dependency- and capacity-aware sprint plan for the MVP backlog (D13 §3). Reads the stories from build_backlog.py.

Rules
  * 2-week sprints; Sprint 1 started 2026-11-22 (actual start of implementation).
  * capacity: 25 SP in sprints 1–2 (ramp-up), then VELOCITY (default 40).
  * an item is scheduled only after all its dependencies finished in an EARLIER sprint
    (Spikes and their consumers are therefore never in the same sprint).
  * EARLIEST encodes external blockers (sandbox access, contracts) from D12 §4.2 as "not before sprint N".
  * among ready items: spikes first (retire risk early, D12 §1), then lower phase, then items that unblock the
    most downstream work. The pilot (US-194) is scheduled only after every other item.
  * items already done are pinned to sprint 1.
Writes docs/00-product/D13-sprint-plan.md and prints a summary. Exit 1 if anything cannot be scheduled.
"""
import datetime as dt
import importlib.util
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("backlog", HERE / "build_backlog.py")
bl = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bl)

VELOCITY = int(sys.argv[1]) if len(sys.argv) > 1 else 40
START = dt.date(2026, 11, 22)
DONE = {"US-001", "US-002", "SP-07", "SP-01", "US-003", "US-021", "US-022", "US-006", "US-005", "US-011", "US-025", "US-012", "US-013", "US-026", "US-023", "US-031", "US-032", "US-004", "US-014", "US-015", "US-016", "US-010", "US-041", "US-042", "US-081", "US-111", "US-151", "US-046", "US-024", "US-045", "US-052", "US-061", "US-051", "US-053", "US-063", "US-071", "US-082", "US-101", "US-091", "US-064", "US-066", "US-062", "US-083", "US-065", "US-072", "US-112", "US-113", "US-172", "US-084", "US-027"}  # D12 §0.1
AS_OF = "2026-11-24"                           # date of the progress snapshot (update with DONE)
PARTIAL = {"US-007": "Compose برای Dev هست؛ Staging و Mirrorها منتظر زیرساخت (X-07)"}
OPEN_QUESTIONS = [                             # decisions waiting on the product owner; the default is what is built (D12 §8)
    ("RQ-05", "آستانه‌ی اختلاف صندوق در بستن شیفت (US-081)", "۵۰۰٬۰۰۰ ریال؛ بیشتر از آن تأیید مدیر"),
    ("RQ-06", "زمان طراحی UI نهایی (D09) پیش از پایلوت", "صفحه‌های فعلی کاربردی و ساده؛ طراحی پیش از Gate F"),
    ("RQ-07", "آستانه‌ی تأیید تغییر قیمت (US-052)", "بیش از ۲۰ قلم یا تغییر بیش از ±۳۰٪"),
    ("RQ-08", "واحد قیمت در برچسب ترازو (US-053)", "ریال"),
    ("RQ-09", "معافیت قیمت اولیه‌ی کالای تازه از تأیید", "معاف"),
    ("RQ-10", "آستانه‌ی تأیید اصلاح موجودی و اختلاف انبارگردانی (US-063/065)", "۵۰ میلیون ریال"),
    ("RQ-11", "ثبت رسید خرید با تاریخ گذشته (US-071)", "فعلاً پشتیبانی نمی‌شود (effectiveDate رد می‌شود)؛ با حسابدار (X-04)"),
    ("RQ-12", "انتقال جدول‌های خرید به Schema جدا (proc)", "فعلاً در inv (رسید، پرداخت و برگشت)"),
    ("RQ-13", "انقضای سبد خرید بی‌فعالیت (US-082/083)", "۲۴ ساعت"),
    ("RQ-15", "منظور از «داده مالی Masked» در دسترسی پشتیبانی (IAM-008)", "شماره پیگیری، ارقام کارت و اطلاعات بانکی پنهان؛ مبلغ‌ها دیده می‌شوند"),
    ("RQ-14", "کالاهای شمرده‌نشده در انبارگردانی کامل (US-065)", "در دامنه‌ی «همه» صفر یافت‌شده حساب می‌شوند (اختلاف ثبت می‌شود)"),
]
FOLLOW_UPS = [                                 # work deliberately left for a later item (shown in D12-progress.md)
    ("US-084", "Service Worker برای باز شدن خود صفحه‌ی صندوق بدون شبکه (PWA)؛ فعلاً صفحه‌ی بازمانده آفلاین کار می‌کند", "US-185 / پیش از پایلوت"),
    ("US-172", "کنسول پلتفرم و ممیزی سراسری اقدام‌های اپراتور (فعلاً ردیف نسخه و لاگ)", "US-171"),
    ("US-172", "لوگوی فاکتور و Snapshot نسخه قالب روی هر فاکتور", "US-092"),
    ("US-113", "بازگشایی دوره با تأیید دونفره (D-45) و قفل نهایی سال (Locked)", "P1"),
    ("US-027", "اطلاع درخواست پشتیبانی به فروشگاه (DET-09)، نماهای پشتیبانی دیگر، کنسول پلتفرم", "US-171"),
    ("US-101", "پرداخت آنلاین، رزرو موجودی و وضعیت PendingPayment", "US-102"),
    ("US-091", "صورتحساب مالیاتی (مؤدیان) و ارسال رسید به مشتری", "EP-08 / EP-09"),
    ("US-065", "انبارگردانی و تخفیف با دامنه‌ی دسته‌بندی کالا", "پس از US-047"),
    ("—", "آستانه‌های تأیید و مهلت‌ها به‌صورت تنظیمات Tenant", "US-034"),
    ("US-072", "پرداخت نقد به تأمین‌کننده از کشوی صندوق (registerShiftId)، با برداشت نقد شیفت", "EP-08 (پیگیری)"),
    ("US-072", "برگرداندن مالیات خرید (InputVat) در برگشت از خرید", "تأیید حسابدار (X-04)"),
    ("US-072", "صورت‌حساب تأمین‌کننده (گردش رسید، پرداخت و برگشت)", "EP-11 گزارش‌ها"),
    ("US-112", "تأیید شخص دوم برای سند دستی بالاتر از آستانه (SoD-04، پیش‌فرض خاموش)", "US-034"),
    ("US-112", "ویرایش پیش‌نویس سند (فعلاً حذف و ساخت دوباره) و تفصیلی مشتری در فرم", "EP-11"),
    ("US-083", "ادامه‌ی فروش معلق در شعبه‌ی دیگر: آزمون جدا ندارد", "EP-08 (پیگیری)"),
    ("US-007", "Staging و Mirrorها", "X-07"),
]
BLOCKER = {                                    # external input each item waits for (D13 §2, D12 §4.2)
    "SP-02": "X-06 مجوز ODbL + فهرست بارکدهای واقعی", "SP-06": "X-08 دستگاه‌های پایلوت", "SP-08": "پرینتر حرارتی (P1)",
    "SP-04": "X-01 Sandbox مؤدیان", "SP-03": "X-02 قرارداد PSP", "US-141": "X-03 قرارداد SMS", "US-152": "X-03 قرارداد SMS",
    "SP-05": "X-09 Sandbox بله", "US-184": "X-10 شرکت Pentest", "US-007": "X-07 زیرساخت",
}
EARLIEST = {                                   # external critical path (D12 §4.2), in sprints
    "SP-04": 3,    # X-01 Moadian sandbox/docs requested now; assume available after Gate A (~4 weeks)
    "SP-03": 6,    # X-02 PSP contracts + sandbox (week 12)
    "US-141": 8,   # X-03 SMS provider contract (week 16)
    "US-152": 8,
    "SP-05": 15,   # X-09 Bale sandbox (week 30)
    "US-184": 17,  # X-10 independent pentest firm
    "US-194": 19,  # pilot after hardening
}

items = {s[0]: {"id": s[0], "epic": s[1], "sp": s[5], "deps": s[6],
                "phase": bl.PHASE_OVERRIDE.get(s[0], bl.EPICS[s[1]][1])} for s in bl.S}
downstream = {k: 0 for k in items}


def count_down(k, seen):
    for o in items.values():
        if k in o["deps"] and o["id"] not in seen:
            seen.add(o["id"])
            count_down(o["id"], seen)
    return len(seen)


for k in items:
    downstream[k] = count_down(k, set())

sprint_of = {k: 1 for k in DONE}
sprint, remaining = 1, set(items) - DONE
plan = {1: sorted(DONE)}
while remaining:
    cap = (25 if sprint <= 2 else VELOCITY) - sum(items[k]["sp"] for k in plan.get(sprint, []))
    ready = [items[k] for k in remaining
             if all(sprint_of.get(d, 10**9) < sprint for d in items[k]["deps"]) and EARLIEST.get(k, 1) <= sprint
             and (k != "US-194" or remaining == {"US-194"})]
    ready.sort(key=lambda i: (not i["id"].startswith("SP-"), i["phase"], -downstream[i["id"]], i["id"]))
    for it in ready:
        if it["sp"] <= cap:
            plan.setdefault(sprint, []).append(it["id"])
            sprint_of[it["id"]] = sprint
            remaining.discard(it["id"])
            cap -= it["sp"]
    if sprint > 60:
        print("cannot schedule:", sorted(remaining)); sys.exit(1)
    sprint += 1
    plan.setdefault(sprint, [])

last = max(k for k, v in plan.items() if v)
gate_sprint = {}
for ph in range(1, 6):
    gate_sprint[ph] = max(sprint_of[k] for k, v in items.items() if v["phase"] == ph)

GATE = {1: "B", 2: "C", 3: "D", 4: "E", 5: "F"}
lines = [f"<!-- generated by docs/00-product/tools/plan_sprints.py (velocity {VELOCITY}) — do not edit by hand -->",
         "| Sprint | بازه | SP | آیتم‌ها | Gate |", "|---|---|---|---|---|"]
for s in range(1, last + 1):
    ids = plan.get(s, [])
    a = START + dt.timedelta(days=14 * (s - 1))
    b = a + dt.timedelta(days=13)
    gates = ", ".join(f"**{GATE[p]}**" for p, gs in gate_sprint.items() if gs == s)
    sp = sum(items[k]["sp"] for k in ids)
    lines.append(f"| {s} | {a.isoformat()} → {b.isoformat()} | {sp} | {', '.join(sorted(ids))} | {gates} |")
(HERE.parent / "D13-sprint-plan.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
end = START + dt.timedelta(days=14 * last - 1)

# ---------------------------------------------------------------- progress snapshot (D12-progress.md, generated)
def bar(frac, width=10):
    n = round(frac * width)
    return "▓" * n + "░" * (width - n)

def pct(a, b):
    return f"{(100 * a / b):.0f}٪" if b else "—"

total_sp = sum(i["sp"] for i in items.values())
done_sp = sum(items[k]["sp"] for k in DONE)
as_of = dt.date.fromisoformat(AS_OF)
current = (as_of - START).days // 14 + 1
P = ["<!-- generated by docs/00-product/tools/plan_sprints.py — do not edit by hand -->",
     f"# پیشرفت توسعه MVP — وضعیت {AS_OF}", "",
     "منبع: آیتم‌های Done در `plan_sprints.py` (`DONE`) و Backlog در `build_backlog.py`. شواهد هر آیتم: D12 §0.1. "
     "برنامه‌ی کامل Sprint به Sprint: [D13-sprint-plan.md](D13-sprint-plan.md).", "",
     "## خلاصه", "", "| شاخص | مقدار |", "|---|---|",
     f"| آیتم‌های Done | **{len(DONE)}** از {len(items)} ({pct(len(DONE), len(items))}) |",
     f"| Story Point انجام‌شده | **{done_sp}** از {total_sp} ({pct(done_sp, total_sp)}) {bar(done_sp / total_sp)} |",
     f"| Sprint جاری | {current} ({(START + dt.timedelta(days=14 * (current - 1))).isoformat()} → {(START + dt.timedelta(days=14 * current - 1)).isoformat()}) |",
     f"| پایان پایلوت MVP (Velocity {VELOCITY}) | **{end.isoformat()}** ({last} Sprint) |",
     "| Gateها (Sprint) | " + "، ".join(f"{GATE[p]}=S{gs}" for p, gs in gate_sprint.items()) + " |",
     f"| نیمه‌کاره | {', '.join(f'{k} ({v})' for k, v in PARTIAL.items()) or '—'} |", "",
     "## پیشرفت هر فاز (Gate)", "", "| فاز | Gate | آیتم Done | SP Done | پیشرفت |", "|---|---|---|---|---|"]
for ph in range(1, 6):
    ks = [k for k, v in items.items() if v["phase"] == ph]
    d = [k for k in ks if k in DONE]
    sp_all, sp_d = sum(items[k]["sp"] for k in ks), sum(items[k]["sp"] for k in d)
    P.append(f"| {ph} | {GATE[ph]} (S{gate_sprint[ph]}) | {len(d)}/{len(ks)} | {sp_d}/{sp_all} | {bar(sp_d / sp_all)} {pct(sp_d, sp_all)} |")
P += ["", "## پیشرفت هر Epic", "", "| Epic | عنوان | فاز | آیتم Done | SP Done | پیشرفت |", "|---|---|---|---|---|---|"]
for ep, (name, ph) in bl.EPICS.items():
    ks = [k for k, v in items.items() if v["epic"] == ep]
    if not ks:
        continue
    d = [k for k in ks if k in DONE]
    sp_all, sp_d = sum(items[k]["sp"] for k in ks), sum(items[k]["sp"] for k in d)
    P.append(f"| {ep} | {name} | {ph} | {len(d)}/{len(ks)} | {sp_d}/{sp_all} | {bar(sp_d / sp_all)} {pct(sp_d, sp_all)} |")
title = {x[0]: x[2] for x in bl.S}
upcoming = sorted({sprint_of[k] for k in items if k not in DONE})[:2]
P += ["", "## کارهای بعدی (Sprint " + " و ".join(map(str, upcoming)) + ")", "", "| Sprint | آیتم | SP | شرح | وضعیت |", "|---|---|---|---|---|"]
for sn in upcoming:
    for k in sorted(k for k in plan.get(sn, []) if k not in DONE):
        t = title[k].replace("|", "/")
        t = t if len(t) <= 90 else t[:89] + "…"
        state = f"⏳ منتظر {BLOCKER[k]}" if k in BLOCKER else "آماده"
        P.append(f"| {sn} | {k} | {items[k]['sp']} | {t} | {state} |")
P += ["", "## پرسش‌های باز (پیش‌فرض پیاده‌سازی شده است؛ D12 §8)", "", "| ID | پرسش | پیش‌فرض فعلی |", "|---|---|---|"]
P += [f"| {q} | {t} | {d} |" for q, t, d in OPEN_QUESTIONS]
P += ["", "## کارهای باز و پیگیری‌ها", "", "| از آیتم | کار باز | به |", "|---|---|---|"]
P += [f"| {a} | {t} | {b} |" for a, t, b in FOLLOW_UPS]
P += ["", "## منتظر ورودی بیرونی", "", "| آیتم | منتظر | زودترین Sprint | Sprint برنامه |", "|---|---|---|---|"]
for k in sorted(BLOCKER):
    if k in DONE:
        continue
    P.append(f"| {k} | {BLOCKER[k]} | {EARLIEST.get(k, '—')} | {sprint_of.get(k, '—')} |")
(HERE.parent / "D12-progress.md").write_text("\n".join(P) + "\n", encoding="utf-8")
print(f"velocity {VELOCITY}: {last} sprints, MVP pilot done by {end.isoformat()}; gates: " +
      ", ".join(f"{GATE[p]}=S{gs}" for p, gs in gate_sprint.items()))
