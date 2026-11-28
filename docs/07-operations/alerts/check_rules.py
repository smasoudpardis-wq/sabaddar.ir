#!/usr/bin/env python3
"""Structural check of retailcloud-rules.yaml against D11: every alert has expr, severity, summary and a runbook that exists."""
import pathlib, re, sys, yaml
here = pathlib.Path(__file__).resolve().parent
rules = yaml.safe_load((here / "retailcloud-rules.yaml").read_text(encoding="utf-8"))
d11 = (here.parent / "D11-DevOps-Runbook-SLO.md").read_text(encoding="utf-8")
runbooks = {m.lower() for m in re.findall(r"^### (RB-\d{2})", d11, re.M)}
errors, names = [], set()
for g in rules["groups"]:
    for r in g["rules"]:
        n = r.get("alert", "?")
        if n in names: errors.append(f"duplicate alert {n}")
        names.add(n)
        if not r.get("expr"): errors.append(f"{n}: missing expr")
        if r.get("labels", {}).get("severity") not in ("page", "ticket"): errors.append(f"{n}: bad severity")
        ann = r.get("annotations", {})
        if not ann.get("summary"): errors.append(f"{n}: missing summary")
        rb = ann.get("runbook_url", "").split("#")[-1]
        if rb not in runbooks: errors.append(f"{n}: runbook '{rb}' not found in D11")
        if n not in d11 and not any(n in x for x in [d11]):
            pass
# every alert named in D11 runbooks must exist in the rule file
for a in set(re.findall(r"`(RC[A-Za-z]+)`", d11)):
    if a not in names: errors.append(f"D11 references alert {a} that is not defined")
print(f"{len(names)} alerts, {len(runbooks)} runbooks in D11")
if errors: print("\n".join(errors)); sys.exit(1)
print("ALERT RULES CHECK PASS")
