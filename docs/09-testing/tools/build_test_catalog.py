#!/usr/bin/env python3
"""
Builds docs/09-testing/test-catalog.csv from the design documents, so the catalog can never drift from them.

Sources
  D02  requirement rows  "| <REQ-ID> | <text> | P | V | AT-nnn |" and Use Case AC lines "... (AT-nnn / P0-n)"
  D06  STRIDE / abuse rows referencing SEC-T-nnn, plus the §12 catalog ranges
  D05  executable DB checks T01..T74 in docs/05-data/sql/tests/tests.sql (already automated)

Rules (documented in D10 §4):
  gate   from the requirement area (TEN/IAM/AUD -> B, CAT/INV/PRC -> C, SAL/PAY/ACC/CRD/TAX -> D, CUS/NOT -> E, RPT/INT -> F)
  layer  from D02 verification method + keywords (concurrency, UI)
Exit code 1 if any test id referenced anywhere in docs/ is missing from the catalog.
"""
import csv, pathlib, re, sys

DOCS = pathlib.Path(__file__).resolve().parents[2]
D02 = (DOCS / "00-product/D02-SRS-Functional-NonFunctional.md").read_text(encoding="utf-8")
D06 = (DOCS / "03-security/D06-Security-Threat-Model.md").read_text(encoding="utf-8")
SQLT = (DOCS / "05-data/sql/tests/tests.sql").read_text(encoding="utf-8")
OUT = DOCS / "09-testing/test-catalog.csv"

GATE = {"TEN": "B", "IAM": "B", "AUD": "B", "CAT": "C", "INV": "C", "PRC": "C", "SAL": "D", "PAY": "D", "ACC": "D",
        "CRD": "D", "TAX": "D", "CUS": "E", "NOT": "E", "RPT": "F", "INT": "F",
        "UC-01": "C", "UC-02": "D", "UC-03": "E", "UC-04": "D", "UC-05": "C", "UC-06": "D", "UC-07": "D",
        "UC-08": "D", "UC-09": "C", "UC-10": "B", "UC-11": "D"}
P0_MAP = {}  # AT -> P0-n (from text like "(AT-210 / P0-3)")
rows = {}


def clean(s):
    s = re.sub(r"\s+", " ", s).strip()
    return s.replace("`", "")


def layer_for(text, method):
    t = text
    if re.search(r"هم‌زمان|موازی|Race|race|concurr|FOR UPDATE|قفل|Lock", t):
        return "Integration-Concurrency"
    if "D" in method and "T" not in method:
        return "UAT"
    if "D" in method:
        return "E2E"
    if method.strip() == "I":
        return "Inspection"
    return "API-Integration"


def gate_for(src):
    for k, v in GATE.items():
        if src.startswith(k):
            return v
    return "F"


# 1) requirement rows in D02
for line in D02.splitlines():
    m = re.match(r"^\| ([A-Z]{2,4}-\d{3}(?:\.\d+)?)(?: \([^)]*\))? \| (.+?) \| (P\d[^|]*) \| ([TIAD+]+) \| (.+?) \|$", line)
    if not m:
        continue
    req, text, prio, method, ac = m.groups()
    for at in re.findall(r"AT-\d{3}", ac):
        p0 = re.search(r"P0-\d+", ac)
        if p0:
            P0_MAP[at] = p0.group(0)
        rows.setdefault(at, {"id": at, "source": req, "objective": clean(text)[:400], "priority": prio.split()[0],
                             "method": method, "gate": gate_for(req), "layer": layer_for(text, method)})

# 2) Use Case acceptance criteria lines in D02 (Given/When/Then ... (AT-nnn[ / P0-n]))
current_uc = None
for line in D02.splitlines():
    u = re.match(r"^### (UC-\d{2})", line)
    if u:
        current_uc = u.group(1)
    for at in re.findall(r"\((AT-\d{3})(?: / (P0-\d+))?\)", line):
        at_id, p0 = at
        if p0:
            P0_MAP[at_id] = p0
        text = re.sub(r"^- ", "", line.strip())
        text = re.sub(r"\(AT-\d{3}[^)]*\)", "", text)
        src = current_uc or "D02"
        rows[at_id] = {"id": at_id, "source": src, "objective": clean(text)[:400], "priority": "P0",
                       "method": "T", "gate": gate_for(src), "layer": layer_for(text, "T")}

# 3) security tests in D06 (first referencing row = objective)
for line in D06.splitlines():
    if not line.startswith("|"):
        continue
    for st in re.findall(r"SEC-T-\d{3}", line):
        if st in rows:
            continue
        cells = [c.strip() for c in line.strip("|").split("|")]
        src = cells[0]
        text = next((c for c in cells[1:] if len(c) > 25 and "SEC-T" not in c), cells[1] if len(cells) > 1 else "")
        if src.startswith("AB-") and cells[1] != text:
            text = f"{cells[1]} — {text}"
        rows[st] = {"id": st, "source": src, "objective": clean(text)[:400], "priority": "P0", "method": "T",
                    "gate": "B" if src.startswith(("TM-0", "TM-1", "TM-6", "TM-7")) else "E" if src.startswith("AB") else "D",
                    "layer": "Security-Automated"}
# expand §12 ranges like SEC-T-001..006
cat12 = D06.split("## 12.")[1].split("## 13.")[0]
for a, b, desc in re.findall(r"\| \*{0,2}SEC-T-(\d{3})(?:\.\.(\d{3}))?\*{0,2} \| (.+?) \|", cat12):
    for n in range(int(a), int(b or a) + 1):
        sid = f"SEC-T-{n:03d}"
        rows.setdefault(sid, {"id": sid, "source": "D06 §12", "objective": clean(desc)[:400], "priority": "P0",
                              "method": "T", "gate": "B", "layer": "Security-Automated"})

# 4) DB checks T01..T74 (already executable)
for tid, label in re.findall(r"'(T\d{2}) ([^']+)'", SQLT):
    rows[tid] = {"id": tid, "source": "D05", "objective": clean(label), "priority": "P0", "method": "T",
                 "gate": "B" if int(tid[1:]) < 10 else "C" if int(tid[1:]) < 20 else "D" if int(tid[1:]) < 50 else "E",
                 "layer": "DB-Integration (automated, passing)"}

for k, v in rows.items():
    v["p0_scenario"] = P0_MAP.get(k, "")
    v["automation"] = {"UAT": "manual", "Inspection": "review"}.get(v["layer"], "automated")

order = lambda r: ({"AT": 0, "SE": 1, "T0": 2}.get(r["id"][:2], 2), r["id"])
with OUT.open("w", encoding="utf-8-sig", newline="") as f:
    w = csv.DictWriter(f, fieldnames=["id", "source", "objective", "priority", "method", "layer", "gate",
                                      "p0_scenario", "automation"])
    w.writeheader()
    for r in sorted(rows.values(), key=order):
        w.writerow(r)

# completeness check against every reference in docs/
referenced = set()
for p in DOCS.rglob("*.md"):
    t = p.read_text(encoding="utf-8")
    referenced |= set(re.findall(r"\b(?:AT|SEC-T)-\d{3}\b", t))
    for pre, a, b in re.findall(r"\b(AT|SEC-T)-(\d{3})\.\.(\d{3})\b", t):
        referenced |= {f"{pre}-{n:03d}" for n in range(int(a), int(b) + 1)}
missing = sorted(x for x in referenced if x not in rows)
by = {}
for r in rows.values():
    k = "SEC-T" if r["id"].startswith("SEC") else "AT" if r["id"].startswith("AT") else "DB"
    by[k] = by.get(k, 0) + 1
print(f"catalog: {len(rows)} tests {by}; P0-linked: {len([1 for r in rows.values() if r['p0_scenario']])}")
if missing:
    print("referenced but undefined:", " ".join(missing))
    sys.exit(1)
print("all referenced test ids are defined")
