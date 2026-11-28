#!/usr/bin/env python3
"""Consolidates every open-question row (OQ/PQ/SQ/DQ/BQ/AQ/SecQ/ApiQ/FQ/UQ/TQ/OpsQ/RQ) from docs/*.md into
docs/01-architecture/Gap-Analysis-Readiness.md between the OQ-REGISTER markers. A question is 'Closed' when its row is
struck through (~~) or says بسته / پاسخ. The first definition of an id wins; later mentions are listed as references."""
import pathlib, re
DOCS = pathlib.Path(__file__).resolve().parents[1]
TARGET = DOCS / "01-architecture/Gap-Analysis-Readiness.md"
pat = re.compile(r"^\| ((?:OQ|PQ|SQ|DQ|BQ|AQ|SecQ|ApiQ|FQ|UQ|TQ|OpsQ|RQ)-\d{2}) \| (.+)$")
order = ["00-product/D01", "01-architecture/00-", "00-product/D02", "02-domain-model/D03", "01-architecture/D04",
         "05-data/D05", "03-security/D06", "04-api/README", "02-domain-model/D08", "06-ux/D09", "09-testing/D10",
         "07-operations/D11", "00-product/D12"]
files = sorted((p for p in DOCS.rglob("*.md") if p != TARGET),
               key=lambda p: next((i for i, o in enumerate(order) if o in str(p)), 99))
reg = {}
for p in files:
    for line in p.read_text(encoding="utf-8").splitlines():
        m = pat.match(line)
        if not m:
            continue
        qid, rest = m.groups()
        cells = [c.strip() for c in rest.rstrip("|").split("|")]
        closed = "~~" in line or "بسته" in line or "**پاسخ" in line
        doc = p.stem.split("-")[0] if p.stem.startswith("D") else p.stem
        if qid not in reg:
            reg[qid] = {"doc": doc, "q": cells[0].replace("~~", ""), "default": cells[-3] if len(cells) >= 3 else "",
                        "closed": closed, "refs": []}
        else:
            reg[qid]["refs"].append(doc)
            reg[qid]["closed"] = reg[qid]["closed"] or closed
rows = ["| ID | سند | پرسش | وضعیت |", "|---|---|---|---|"]
for qid, r in reg.items():
    q = re.sub(r"\s+", " ", r["q"])[:160]
    rows.append(f"| {qid} | {r['doc']} | {q} | {'✅ بسته' if r['closed'] else '⏳ باز'} |")
n_open = sum(1 for r in reg.values() if not r["closed"])
summary = f"**{len(reg)} پرسش** در کل اسناد؛ **{len(reg) - n_open} بسته**، **{n_open} باز** (هر باز یک Default ثبت‌شده دارد و کار را متوقف نمی‌کند، جز مواردی که در §3 «مسدودکننده Gate» علامت خورده‌اند)."
text = TARGET.read_text(encoding="utf-8")
start, end = "<!-- OQ-REGISTER:START -->", "<!-- OQ-REGISTER:END -->"
pre, rest = text.split(start); _, post = rest.split(end)
TARGET.write_text(pre + start + "\n" + summary + "\n\n" + "\n".join(rows) + "\n" + end + post, encoding="utf-8")
print(summary)
