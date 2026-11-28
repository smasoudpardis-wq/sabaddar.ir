#!/usr/bin/env python3
"""Cross-document contract checks for D07 (run in CI after build_openapi.py).

1. Every operation traces to at least one requirement (x-requirements).
2. Every merchant operation declares x-permission; each permission exists in the D06 role×permission matrix
   (or is the explicit 'tenant.member').
3. Money/stock-moving operations are idempotent (x-idempotent: true).
4. Every write operation with a body declares 400 and 422; every path-param operation declares 404.
"""
import pathlib, re, sys, yaml
root = pathlib.Path(__file__).resolve().parents[2]
spec = yaml.safe_load((root / "04-api/D07-API-OpenAPI.yaml").read_text(encoding="utf-8"))
d06 = (root / "03-security/D06-Security-Threat-Model.md").read_text(encoding="utf-8")
matrix = d06.split("### 6.2")[1].split("### 6.3")[0]
known = set(re.findall(r"^\| `([a-z.]+)` \|", matrix, re.M)) | {"tenant.member", "authenticated"}
MUST_BE_IDEMPOTENT = {"finalizeSale", "recordTender", "createReturn", "postGoodsReceipt", "postAdjustment",
                      "postTransfer", "confirmCount", "createPaymentIntent", "recordCollection", "postJournal",
                      "reverseJournal", "writeOff", "completeRefundManually", "paySupplier", "postPurchaseReturn",
                      "openShift", "closeShift", "portalPayBalance", "setPrices", "createProduct"}
errors, n = [], 0
for path, item in spec["paths"].items():
    for method, op in item.items():
        n += 1
        oid = op["operationId"]
        if not op.get("x-requirements"):
            errors.append(f"{oid}: missing x-requirements")
        merchant = op.get("security") == [{"merchantBearer": []}]
        perm = op.get("x-permission")
        if merchant and not perm:
            errors.append(f"{oid}: merchant operation without x-permission")
        if perm and perm not in known:
            errors.append(f"{oid}: permission '{perm}' not in D06 §6.2 matrix")
        if oid in MUST_BE_IDEMPOTENT and not op.get("x-idempotent"):
            errors.append(f"{oid}: must be idempotent")
        r = op["responses"]
        if "requestBody" in op and op["security"] and not {"400", "422"} <= r.keys():
            errors.append(f"{oid}: body without 400/422")
        if "{" in path and "404" not in r:
            errors.append(f"{oid}: path params without 404")
print(f"checked {n} operations against {len(known)} permissions from D06")
if errors:
    print("\n".join(errors)); sys.exit(1)
print("CONTRACT CHECKS PASS")
