#!/usr/bin/env python3
"""
Builds docs/04-api/D07-API-OpenAPI.yaml (OpenAPI 3.1) for the RetailCloud MVP.

Why a generator: ~120 operations share the same error model, security, idempotency and
traceability conventions (D02/D06). Generating them keeps every operation consistent and lets
the security test generator (SEC-T-010/011/012, D06 §12) rely on the x-* extensions.

Usage:  python3 docs/04-api/tools/build_openapi.py        (writes the YAML next to this folder)
Edit THIS file, not the YAML.
"""
import json
import re
import pathlib
import yaml

OUT = pathlib.Path(__file__).resolve().parent.parent / "D07-API-OpenAPI.yaml"

# ----------------------------------------------------------------------------- schema DSL
# 'uuid' 'str' 'int' 'bool' 'dt' 'date' 'money' 'qty' 'bp' '#Ref' '[x]'  ; value ending '?' => nullable
# property key ending '?' => optional (not in required)


def t(x):
    if isinstance(x, dict):
        return obj(**x)
    if isinstance(x, tuple) and x[0] == "enum":
        return {"type": "string", "enum": list(x[1])}
    if isinstance(x, tuple) and x[0] == "enum?":
        return {"type": ["string", "null"], "enum": list(x[1]) + [None]}
    if isinstance(x, tuple) and x[0] == "desc":
        s = t(x[1]); s["description"] = x[2]; return s
    if isinstance(x, list):
        return {"type": "array", "items": t(x[0])}
    nullable = x.endswith("?")
    base = x[:-1] if nullable else x
    m = {
        "uuid": {"type": "string", "format": "uuid"},
        "str": {"type": "string"},
        "int": {"type": "integer", "format": "int32"},
        "long": {"type": "integer", "format": "int64"},
        "bool": {"type": "boolean"},
        "dt": {"type": "string", "format": "date-time"},
        "date": {"type": "string", "format": "date"},
        "obj": {"type": "object"},
        "money": {"$ref": "#/components/schemas/Money"},
        "qty": {"$ref": "#/components/schemas/Quantity"},
        "bp": {"$ref": "#/components/schemas/BasisPoints"},
        "jalali": {"type": "string", "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}$",
                   "description": "Jalali date for display/filter only (BR-023)"},
    }
    if base.startswith("#"):
        s = {"$ref": f"#/components/schemas/{base[1:]}"}
    else:
        s = dict(m[base])
    if nullable:
        if "$ref" in s:
            return {"anyOf": [s, {"type": "null"}]}
        s["type"] = [s["type"], "null"]
    return s


def obj(**props):
    required, properties = [], {}
    desc = props.pop("_desc", None)
    for k, v in props.items():
        name = k.rstrip("?")
        if not k.endswith("?"):
            required.append(name)
        properties[name] = t(v)
    s = {"type": "object", "additionalProperties": False, "properties": properties}
    if required:
        s["required"] = required
    if desc:
        s["description"] = desc
    return s


SCHEMAS = {}


def schema(name, /, **props):
    SCHEMAS[name] = obj(**props)


def raw_schema(name, s):
    SCHEMAS[name] = s


# ----------------------------------------------------------------------------- common schemas
raw_schema("Money", {
    "type": "string", "pattern": "^-?[0-9]{1,19}$",
    "description": "Integer amount in IRR (rial), encoded as a string to avoid floating point loss in JSON/JS "
                   "(BR-010, ADR-009). Toman is a display concern only (BR-011).",
    "examples": ["5500000"]})
raw_schema("Quantity", {
    "type": "string", "pattern": "^[0-9]{1,12}(\\.[0-9]{1,6})?$",
    "description": "Decimal quantity (max 6 fraction digits; precision limited per unit, BR-012).",
    "examples": ["1", "0.750"]})
raw_schema("BasisPoints", {"type": "integer", "minimum": 0, "maximum": 10000,
                           "description": "Rate in basis points; 9% = 900."})
raw_schema("Problem", {
    "type": "object",
    "description": "RFC 9457 problem details. `code` is a stable machine-readable identifier (see x-error-catalog).",
    "required": ["type", "title", "status", "code", "traceId"],
    "properties": {
        "type": {"type": "string", "format": "uri-reference"},
        "title": {"type": "string"},
        "status": {"type": "integer"},
        "code": {"type": "string", "examples": ["insufficient-stock"]},
        "detail": {"type": "string"},
        "traceId": {"type": "string"},
        "errors": {"type": "array", "items": {
            "type": "object", "additionalProperties": False,
            "required": ["field", "message"],
            "properties": {"field": {"type": "string"}, "message": {"type": "string"},
                           "code": {"type": "string"}}}},
        "approvalRequestId": {"type": "string", "format": "uuid",
                              "description": "Present when code = approval-required (IAM-007)."},
        "context": {"type": "object", "description": "Code-specific structured data (e.g. shortages, new prices).",
                    "additionalProperties": True},
    },
    "additionalProperties": False})
schema("PageInfo", nextCursor="str?", hasMore="bool")

# ----------------------------------------------------------------------------- domain schemas
# Platform / IAM
schema("TenantRegistration", legalName="str", industryCode=("enum", ["supermarket", "apparel", "general"]),
       **{"timeZone?": "str", "pricingMode?": ("enum", ["TaxExclusive", "TaxInclusive"])},
       firstBranchName="str")
schema("Tenant", id="uuid", legalName="str", industryCode="str", timeZone="str", baseCurrency=("enum", ["IRR"]),
       pricingMode=("enum", ["TaxExclusive", "TaxInclusive"]),
       status=("enum", ["Trial", "Active", "Suspended", "Closed"]), version="int",
       plan=("desc", "str", "Subscription plan code (TEN-003, US-014), e.g. Trial, Basic, Pro, Enterprise"),
       trialEndsAt="dt?", entitlements="#Entitlements",
       readOnlyReason=("desc", ("enum?", ["suspended", "closed", "trial-expired"]),
                       "Set while the store is read-only (TEN-004): writes answer 403 tenant-read-only except x-allowed-when-read-only"))
schema("Entitlements", maxBranches=("desc", "int?", "null = unlimited"), maxRegisters=("desc", "int?", "null = unlimited"),
       maxUsers=("desc", "int?", "null = unlimited; active members plus open invitations count"))
schema("TenantSetup", complete="bool", steps=[{"name": ("enum", ["branch-and-register", "warehouse", "chart-of-accounts", "fiscal-periods"]), "complete": "bool"}])
schema("TenantUpdate", expectedVersion="int", **{"legalName?": "str", "timeZone?": "str",
                                                  "pricingMode?": ("enum", ["TaxExclusive", "TaxInclusive"]),
                                                  "returnWindowDays?": "int", "negativeStockPolicy?": ("enum", ["Forbid", "AllowWithWarning"])})
schema("BranchCreate", code="str", name="str")
schema("Branch", id="uuid", code="str", name="str", status=("enum", ["Active", "Inactive"]))
schema("RegisterCreate", code="str")
schema("Register", id="uuid", branchId="uuid", code="str")
schema("PairingCode", code="str", branchId="uuid", expiresAt="dt")
schema("TerminalPairRequest", pairingCode="str", deviceName="str", **{"userAgent?": "str"})
schema("Terminal", id="uuid", branchId="uuid", deviceName="str", pairedAt="dt", status=("enum", ["Active", "Revoked"]),
       **{"deviceSecret?": ("desc", "str", "Only in the pairTerminal response; kept by the BFF, never by page scripts")})
schema("ExportCreate", categories=[("enum", ["Products", "Customers", "Sales", "Inventory", "Journals", "Receivables", "TaxInvoices"])],
       **{"from?": "dt", "to?": "dt", "includeFullPhone?": "bool"})
schema("ExportJob", id="uuid", status=("desc", ("enum", ["Queued", "Running", "Completed", "Failed", "Expired"]),
                                       "Expired: the file is deleted 24 h after completion (TEN-005)"),
       categories=["str"], **{"from": "dt?", "to": "dt?"}, rowCount="long?", createdAt="dt", completedAt="dt?", expiresAt="dt?",
       downloadUrl=("desc", "str?", "Presigned, valid 5 minutes (D-49); issued (and audited) on every getExport of a Completed export"),
       downloadUrlExpiresAt="dt?")
schema("Me", userId="uuid", displayName="str", activeTenantId="uuid?",
       memberships=[{"tenantId": "uuid", "tenantName": "str", "roles": ["str"], "mfaRequired": "bool"}],
       permissions=[{"permission": "str", "scopeType": ("enum", ["Tenant", "Branch", "Warehouse", "Register"]), "scopeId": "uuid?"}],
       mfaSatisfied="bool", mfaFreshUntil="dt?",
       **{"terminal?": {"_desc": "The registered POS device of this request (X-RC-Device), US-013.", "id": "uuid", "branchId": "uuid"}})
schema("ActiveTenantSelect", tenantId="uuid")
schema("InvitationCreate", **{"email?": "str", "mobile?": "str"}, displayName="str",
       roleAssignments=["#RoleAssignmentInput"])
schema("RoleAssignmentInput", roleId="uuid", scopeType=("enum", ["Tenant", "Branch", "Warehouse", "Register"]), **{"scopeId?": "uuid"})
schema("RoleAssignment", id="uuid", roleId="uuid", roleName="str", scopeType="str", scopeId="uuid?")
schema("RoleGrant", role="str", scopeType=("enum", ["Tenant", "Branch", "Warehouse", "Register"]), scopeId="uuid?")
schema("Invitation", id="uuid", displayName="str", status=("enum", ["Pending", "Accepted", "Revoked"]), expiresAt="dt",
       roleAssignments=["#RoleGrant"], **{"inviteToken?": ("desc", "str", "Only in the creation response")})
schema("InvitationAccepted", tenantId="uuid", tenantName="str", roles=["str"])
schema("User", id="uuid", displayName="str", status=("enum", ["Invited", "Active", "Disabled"]),
       roleAssignments=["#RoleAssignment"], lastLoginAt="dt?", version="int")
schema("UserUpdate", expectedVersion="int", **{"status?": ("enum", ["Active", "Disabled"]), "displayName?": "str"})
schema("Role", id="uuid", name="str", isSystem="bool", permissions=["str"])
schema("ApprovalRequest", id="uuid", action="str", resourceType="str", resourceId="str?", reason="str?",
       requestedBy="uuid", status=("enum", ["Pending", "Approved", "Rejected", "Expired", "Consumed"]),
       context={"_desc": "Condition that triggered approval (ABAC-02/03/06).", "condition": "str", "value?": "str", "limit?": "str"},
       expiresAt="dt", decidedBy="uuid?", decidedAt="dt?",
       **{"validUntil?": ("desc", "dt?", "Approved: usable once until then (5 minutes, D-61)")})
schema("ApprovalDecision", decision=("enum", ["Approve", "Reject"]),
       **{"comment?": "str", "pin?": ("desc", "str", "On-device approval: the approver's PIN (with approverId), registered terminal only"),
          "approverId?": "uuid"})
schema("PinInput", pin=("desc", "str", "4-6 digits"))
schema("SupportAccessRequestCreate", reason=("desc", "str", "10-500 characters, shown to the store owner"),
       durationMinutes=("desc", "int", "15-240 (IAM-008: at most 4 h)"))
schema("SupportSale", id="uuid", status="str", invoiceNo="str?", branchId="uuid", registerId="uuid", channel="str", lastActivityAt="dt",
       finalizedAt="dt?", lines=[{"name": "str", "qty": "qty", "unit": "str", "lineTotal": "money"}],
       totals={"gross": "money", "discount": "money", "tax": "money", "total": "money", "paid": "money"},
       tenders=[{"method": "str", "amount": "money", "status": "str"}],
       customer=("desc", "obj?", "Always null: masked for support (IAM-008)"))
schema("SupportAccessRequest", id="uuid", requestedBy="str", reason="str", durationMinutes="int",
       status=("desc", ("enum", ["Pending", "Approved", "Rejected", "Expired", "Revoked"]),
               "Expired: a pending request after 24 h, or an approved one after grantedUntil"),
       requestedAt="dt", decidedAt="dt?", grantedUntil="dt?", revokedAt="dt?")

# Catalog / Merchandising
schema("BarcodeResolveRequest", barcode="str", branchId="uuid",
       **{"preferredSources?": [("enum", ["tenant", "global", "licensed_providers"])]})
schema("BarcodeCandidate", source=("enum", ["tenant", "global", "open_food_facts", "manual"]),
       confidence=("desc", "str", "Decimal string 0..1; global (moderated) 0.95, a provider at most 0.7 (CAT-004.1)"),
       lastVerifiedAt="dt?", license="str?", attribution="str?",
       catalogProductId=("desc", "uuid?", "For a global candidate: send it as createProduct.catalogProductId to adopt it"),
       fields={"name?": "str", "brand?": "str", "category?": "str", "netContent?": "str",
               "imageUrl?": ("desc", "str", "https on the provider's image host only; shown by the browser, never fetched by the server")},
       conflictsWith=("desc", ["int"], "Indexes of candidates with another name or brand: none is pre-selected (UC-01 6a)"),
       preselect=("desc", "bool", "The one unopposed candidate at or above 0.8; saving still needs confirmation (D-17)"))
schema("BarcodeResolveResponse", kind=("enum", ["known", "candidates", "not_found"]),
       normalized={"gtin14?": "str", "isInternal": "bool", "isWeighted": "bool",
                   "weighted?": {"variantId": "uuid", "value": "str", "valueType": ("enum", ["Weight", "Price"]),
                                 "quantity?": "qty", "amount?": "money"}},
       **{"tenantProduct?": "#ProductSummary"}, candidates=["#BarcodeCandidate"], degraded="bool")
schema("ProductSummary", productId="uuid", variantId="uuid", name="str", sku="str", unitPrice="money?",
       **{"discount?": ("desc", {"ruleId": "uuid", "percent": "bp", "name": "str"}, "The one automatic rule in force here (the largest; D-18)")})
schema("VariantInput", sku="str", **{"axisValues?": {"color?": "str", "size?": "str", "model?": "str"},
                                     "unitPrice?": "money", "barcodes?": ["#BarcodeInput"]})
schema("BarcodeInput", code="str", **{"packagingUnit?": "str", "unitFactor?": "qty", "isInternal?": "bool"})
schema("ProductCreate", name="str", baseUnit="str", **{"brand?": "str", "categoryId?": "uuid",
       "catalogProductId?": "uuid", "taxCode?": "str", "taxRate?": "bp", "axes?": [("enum", ["color", "size", "model"])]},
       variants=["#VariantInput"], confirmation={"_desc": "Explicit user confirmation of suggested data (CAT-004.1).",
                                                  "confirmed": "bool", "sourceCandidate?": "int"},
       **{"openingStock?": {"warehouseId": "uuid", "qty": "qty", "unitCost": "money"}})
schema("Barcode", id="uuid", code=("desc", "str", "As entered or scanned (CAT-003 keeps the original)"),
       codeNormalized=("desc", "str", "GTIN-14 for every GS1 code; the code itself for a free internal code"),
       packagingUnit="str", unitFactor="qty",
       isInternal=("desc", "bool", "Store-only: free code or GS1 restricted circulation (02, 20-29); never sent to the global catalog (CAT-003.1)"),
       active=("desc", "bool", "false once removed; removed barcodes stay as history (CAT-002)"))
schema("Variant", id="uuid", sku="str", axisValues={"color?": "str", "size?": "str", "model?": "str"},
       status=("enum", ["Active", "Inactive"]), barcodes=["#Barcode"], unitPrice="money?")
schema("Product", id="uuid", name="str", brand="str?", baseUnit="str", taxCode="str?", taxRate="bp",
       status=("enum", ["Active", "Inactive", "Merged"]), variants=["#Variant"], version="int",
       axes=("desc", [("enum", ["color", "size", "model"])], "CAT-008 matrix axes; every variant has a value for exactly these"),
       **{"costVisible?": ("desc", "money?", "Current WAC; only with catalog.product.manage or reports.view (TM-014)")})
schema("ProductUpdate", expectedVersion="int", **{"name?": "str", "brand?": "str", "taxCode?": "str", "taxRate?": "bp",
                                                  "status?": ("enum", ["Active", "Inactive"])})
schema("VariantUpdate", expectedVersion=("desc", "int", "The product's version"), status=("enum", ["Active", "Inactive"]))
schema("ProductMerge", targetProductId="uuid", expectedVersion=("desc", "int", "The duplicate's version"), reason=("desc", "str", "3-200 characters"))
schema("CatalogSuggestionCreate", consent=("desc", "bool", "Must be true: the store opts in for this suggestion (CAT-005.1)"),
       **{"category?": "str"})
schema("CatalogSuggestionReceipt", submitted=["str"], alreadySubmitted=("desc", ["str"], "Codes this store had already suggested"))
schema("PriceSet", items=[{"variantId": "uuid", "unitPrice": "money"}], validFrom="dt", **{"reason?": "str"})
schema("PriceList", id="uuid", name="str", isDefault=("desc", "bool", "The tenant's list; created on first use"),
       branchIds=("desc", ["uuid"], "Branches with their own list; empty: every branch sells from the default list (CAT-009)"))
schema("PriceVersion", variantId="uuid", unitPrice="money", validFrom="dt",
       validTo=("desc", "dt?", "Exclusive; null = until changed"), reason="str?", createdBy="uuid?", createdAt="dt")
schema("DiscountRuleCreate", name="str", target={"kind": ("enum", ["Product", "Category"]), "id": "uuid"},
       percent="bp", validFrom="dt", **{"validTo?": "dt", "branchIds?": ["uuid"]})
schema("DiscountRule", id="uuid", name="str", target={"kind": ("enum", ["Product", "Category"]), "id": "uuid"}, percent="bp",
       validFrom="dt", validTo=("desc", "dt?", "Exclusive; null = until ended"), branchIds=("desc", ["uuid"], "Empty: every branch"))
schema("DiscountCaps", caps=("desc", {"Cashier?": "bp", "BranchManager?": "bp", "TenantAdmin?": "bp", "TenantOwner?": "bp"},
       "BR-021: manual discount a role may give without approval; defaults Cashier 0, the others 10000"))
schema("WeightedBarcodeProfile", **{"id?": "uuid"}, prefix=("desc", "str", "1-3 digits starting with 2 (GS1 restricted circulation)"),
       itemCodeLength=("desc", "int", "1-6; the item code is the variant's internal code (its PLU on the scale)"),
       valueType=("enum", ["Weight", "Price"]), valueLength="int",
       decimals=("desc", "int", "Weight: kilogram decimals 0-3; Price: 0 (whole rials)"),
       hasInnerCheckDigit=("desc", "bool", "GS1 value check digit before a 4- or 5-digit value. "
                           "prefix + itemCodeLength + valueLength (+1) = 12, the data digits of an EAN-13"))
schema("WeightedBarcodeProfileSet", version="int", profiles=["#WeightedBarcodeProfile"])
schema("WeightedBarcodeProfileSetUpdate", expectedVersion="int", profiles=["#WeightedBarcodeProfile"])

# Inventory / Procurement
schema("WarehouseCreate", branchId="uuid", code="str", **{"kind?": ("enum", ["Sellable", "ReturnsDamaged"])})
schema("Warehouse", id="uuid", branchId="uuid", code="str", kind="str")
schema("ReceiptLineInput", variantId="uuid", qty=("desc", "qty", "In unit, as on the supplier's invoice"),
       unit=("desc", "str", "The base unit or a packaging unit of the variant (BR-044); otherwise 422 unknown-unit"),
       unitCost=("desc", "money", "Rials per unit"),
       **{"discount?": ("desc", "money", "Rials off the line"), "inputVat?": ("desc", "money", "Rials of purchase VAT on the line")})
schema("GoodsReceiptCreate", supplierId="uuid", warehouseId="uuid", lines=["#ReceiptLineInput"],
       **{"supplierInvoiceNo?": "str", "effectiveDate?": ("desc", "date", "Not yet supported (a back-dated receipt waits for OQ-05)"),
          "confirmedWarnings?": ("desc", ["str"], "UC-05 warnings the user confirmed: zero-cost, duplicate-supplier-invoice")})
schema("GoodsReceipt", id="uuid", supplierId="uuid", warehouseId="uuid", supplierInvoiceNo="str?", effectiveAt="dt",
       netTotal=("desc", "money", "Σ line cost: the Inventory debit"), inputVat="money", total=("desc", "money", "The supplier's payable credit"),
       lines=[{"lineNo": "int", "variantId": "uuid", "qty": "qty", "unit": "str", "unitFactor": "qty", "qtyBase": "qty", "unitCost": "money",
               "discount": "money", "inputVat": "money", "lineCost": "money",
               "newWac": ("desc", "money?", "The WAC after this line; only in the response to postGoodsReceipt")}],
       journalEntryId=("desc", "uuid?", "Null only for a receipt worth nothing"), createdBy="uuid", warnings=["str"], postedAt="dt")
schema("AdjustmentCreate", warehouseId="uuid", reason=("enum", ["Damage", "Expiry", "Theft", "CountError", "Other"]),
       lines=[{"variantId": "uuid", "qtyDelta": ("desc", "str", "Signed decimal")}], **{"note?": "str"})
schema("Adjustment", id="uuid", warehouseId="uuid", reason="str", note="str?", lineCount="int",
       value=("desc", "money", "Σ |Δqty| × WAC of the moment (BR-033) = shrinkage + overage"), shrinkage="money", overage="money",
       journalEntryId=("desc", "uuid?", "Null only when the adjustment moved no value (WAC 0)"), approvalId="uuid?", createdBy="uuid",
       postedAt="dt", warnings=["str"])
schema("TransferCreate", fromWarehouseId="uuid", toWarehouseId="uuid", lines=[{"variantId": "uuid", "qty": ("desc", "qty", "In the base unit")}],
       **{"note?": "str"})
schema("Transfer", id="uuid", fromWarehouseId="uuid", toWarehouseId="uuid", status=("enum", ["Completed", "InTransit"]), lineCount="int",
       value=("desc", "money", "Σ qty × the source's WAC, carried to the destination (INV-008)"), note="str?", createdBy="uuid", postedAt="dt",
       lines=("desc", [{"line": "int", "variantId": "uuid", "qty": "qty", "unitCost": "money", "value": "money"}], "Only in postTransfer's response"))
schema("CountCreate", warehouseId="uuid", scope=("enum", ["All", "Categories"]), **{"categoryIds?": ["uuid"]},
       blind="bool")
schema("Count", id="uuid", warehouseId="uuid", scope=("enum", ["All", "Categories"]), blind="bool", status=("enum", ["Open", "Closed", "Confirmed"]),
       snapshotAt="dt", closedAt="dt?", confirmedAt="dt?", adjustmentId="uuid?", createdBy="uuid", version="int",
       counters=("desc", "int", "How many people counted (SoD-03)"))
schema("CountConfirmation", count="#Count", adjustmentId=("desc", "uuid?", "The CountAdjustment posted; null when nothing differed"))
schema("CountEntryInput", entries=[{"variantId": "uuid", "qty": ("desc", "str", "Signed base-unit quantity; negative corrects a wrong scan")}],
       clientBatchId=("desc", "str", "8-64 of A-Z a-z 0-9 _ -: the same batch again (an offline device syncing twice) adds nothing"))
schema("CountResult", lines=[{"variantId": "uuid", "systemAtSnapshot": "qty", "movementsSince": ("desc", "str", "signed decimal"),
                             "counted": "qty", "wasCounted": "bool", "difference": ("desc", "str", "signed decimal"), "value": "money"}],
       totalValueDifference="money", approvalRequired="bool")
schema("StockBalance", warehouseId="uuid", variantId="uuid", onHand="qty", reserved="qty", available="qty",
       **{"wac?": "money", "value?": "money"}, reorderPoint=("desc", "qty?", "INV-010: the point at or below which the item is low; null if none"),
       belowReorderPoint=("desc", "bool", "available (onHand − reserved) ≤ reorderPoint"),
       **{"name?": ("desc", "str?", "With belowReorderPoint=true only"), "sku?": "str?"})
schema("ReorderPointSet", warehouseId="uuid", variantId="uuid", reorderPoint=("desc", "qty?", "Base unit; null removes it"))
schema("StockMovement", id="uuid", recordedAt="dt", effectiveAt="dt", type="str", qtyBase=("desc", "str", "signed decimal, base unit"),
       unitCost=("desc", "money", "Per base unit: the WAC of the moment, or the line's own cost for a receipt (INV-006)"),
       totalCost=("desc", "str", "Signed rials; Σ totalCost = the balance's value"), sourceType="str", sourceId="uuid", sourceLine="int",
       enteredQty=("desc", "str?", "As the document said it, e.g. 2 (cartons); BR-044 snapshot"), enteredUnit="str?", unitFactor="qty?")
schema("NegativeStockPolicySet", scopeType=("enum", ["Tenant", "Branch", "Variant"]),
       **{"scopeId?": ("desc", "uuid", "Branch or variant id; omitted for Tenant")}, policy=("enum", ["Forbid", "AllowWithWarning"]))
schema("SupplierCreate", name="str", **{"nationalId?": "str", "economicCode?": "str", "phone?": "str"})
schema("Supplier", id="uuid", name="str", nationalId="str?", economicCode="str?", phone="str?",
       balance=("desc", "money", "What the store owes: TradePayables credit − debit with this supplier as detail"))
schema("SupplierPaymentCreate", amount="money", method=("enum", ["Cash", "Bank"]),
       **{"reference?": ("desc", "str", "Transfer or cheque number; required for Bank"), "note?": "str",
          "registerShiftId?": ("desc", "uuid", "Not yet supported (cash out of a register drawer, with US-084)"),
          "confirmedWarnings?": ("desc", ["str"], "exceeds-balance: paying more than the store owes (an advance)")})
schema("SupplierPayment", id="uuid", supplierId="uuid", amount="money", method=("enum", ["Cash", "Bank"]), reference="str?", note="str?",
       journalEntryId="uuid", balanceAfter=("desc", "money", "What the store owes the supplier after this payment (negative: an advance)"),
       createdBy="uuid", paidAt="dt", warnings=["str"])
schema("PurchaseReturnCreate", supplierId="uuid", warehouseId="uuid",
       lines=[{"variantId": "uuid", "qty": ("desc", "qty", "In the base unit")}],
       agreedAmount=("desc", "money", "What the supplier takes off the store's payable"),
       **{"note?": "str", "confirmedWarnings?": ("desc", ["str"], "exceeds-balance: the supplier will owe the store")})
schema("PurchaseReturn", id="uuid", supplierId="uuid", warehouseId="uuid",
       lines=[{"lineNo": "int", "variantId": "uuid", "qty": "qty", "unitCost": ("desc", "money", "WAC when it left"), "lineCost": "money"}],
       cost=("desc", "money", "Σ line cost: the Inventory credit"), agreedAmount=("desc", "money", "The TradePayables debit"),
       variance=("desc", "money", "agreedAmount − cost: credited (positive) or debited (negative) to PurchaseReturnVariance"),
       note="str?", journalEntryId=("desc", "uuid?", "Null only for a return worth nothing"),
       balanceAfter=("desc", "money?", "Only in the response to postPurchaseReturn"), createdBy="uuid", postedAt="dt", warnings=["str"])

# Sales / Shift
schema("ShiftOpen", registerId="uuid", openingCash="money")
schema("Shift", id="uuid", registerId="uuid", branchId="uuid", cashierId="uuid", status=("enum", ["Open", "Closing", "Closed"]),
       openingCash="money", expectedCash=("desc", "money?", "null until the shift is counted and closed (blind close, D-24)"),
       countedCash="money?", variance=("desc", "money?", "counted − expected; negative = shortage (UC-06)"),
       varianceStatus=("desc", ("enum", ["None", "PendingApproval", "Approved", "Rejected"]),
                       "PendingApproval when |variance| exceeds the threshold; decided by shift.supervise, never the cashier (SoD-01)"),
       varianceReason="str?", openedAt="dt", closedAt="dt?", version="int")
schema("CashMovementCreate", direction=("enum", ["In", "Out"]), amount="money", reason="str")
schema("ShiftClose", countedCash="money", expectedVersion="int", **{"denominations?": [{"value": "money", "count": "int"}], "varianceReason?": "str"})
schema("PosContext", terminalId="uuid", branchId="uuid",
       registers=[{"id": "uuid", "code": "str", "mayOpenShift": "bool",
                   "shift": ("desc", {"id": "uuid", "status": "str", "mine": "bool", "openedAt": "dt"}, "The register's shift not yet closed; null if none")}])
schema("SaleCreate", registerId="uuid", clientDraftId=("desc", "str", "8-64 of A-Z a-z 0-9 _ - (DI-010)"),
       **{"lines?": ("desc", ["#SaleLineInput"], "SAL-013: a cart built offline, added at server prices when the sale is created; ignored when "
                     "the clientDraftId already exists (no line is added twice)")})
schema("SaleLineInput", **{"variantId?": "uuid", "barcode?": ("desc", "str", "Exactly one of barcode or variantId; a scale label sets its own quantity (CAT-007)"),
                            "unit?": ("desc", "str", "Default: the scanned barcode's packaging unit, else the base unit (BR-044)")},
       qty=("desc", "qty", "Added to the line of the same item and unit, if there is one"))
schema("SaleLinePatch", expectedVersion="int", **{"qty?": "qty", "unit?": "str"})
schema("LineDiscount", kind=("enum", ["Percent", "Amount"]), value=("desc", "str", "basis points for Percent, IRR for Amount"),
       source=("enum", ["Manual", "Rule"]), **{"amount?": "money"}, approvalId="uuid?")
schema("SaleLine", lineNo="int", variantId="uuid", name="str", sku="str", qty="qty", unit="str",
       **{"unitFactor?": "qty", "weighted?": ("desc", "bool", "From a scale label: the quantity is fixed"), "gross?": "money"}, unitPrice="money",
       discounts=["#LineDiscount"], net="money", taxRate="bp", tax="money", lineTotal="money", returnedQty="qty")
schema("Tender", seq="int", method=("enum", ["Cash", "ExternalCardTerminal", "OnlineGateway", "CustomerCredit", "StoreCredit"]),
       amount="money", status=("enum", ["Confirmed", "Pending", "Failed", "Voided"]), paymentId="uuid?", reference="str?",
       **{"voidReason?": "str?"},
       **{"tendered?": ("desc", "money?", "Cash: what was handed over"), "change?": ("desc", "money?", "Cash: what was given back (SAL-006.1)")})
schema("Sale", id="uuid", status=("enum", ["Draft", "Held", "PendingPayment", "Finalized", "Cancelled"]),
       returnStatus=("enum", ["None", "Partial", "Full"]), branchId="uuid", registerId="uuid", shiftId="uuid",
       **{"cashierId?": "uuid", "terminalId?": "uuid", "channel?": ("enum", ["POS", "Admin", "API"]), "clientDraftId?": "str",
          "pricingMode?": ("enum", ["TaxExclusive", "TaxInclusive"]), "holdNote?": "str?", "cancelReason?": "str?"},
       invoiceNo="str?", customer=("desc", "#SaleCustomer?", "Masked; never phone/global id (BR-061)"),
       lines=["#SaleLine"], saleDiscount="#LineDiscount?", tenders=["#Tender"],
       totals={"gross": "money", "discount": "money", "net": "money", "tax": "money", "total": "money",
               "paid": "money", "remaining": "money", "change": "money"},
       finalizedAt="dt?", receiptUrl="str?", version="int",
       warnings=[("enum", ["missing-tax-code", "price-preview-only", "negative-stock-allowed", "offline-lines-rejected"])],
       **{"rejectedLines?": ("desc", [{"index": "int", "code": "str?"}],
                             "Only in the response to createSale with offline lines: lines the store could not add (e.g. barcode-not-found)")})
schema("SaleCustomer", tenantCustomerId="uuid", maskedName="str", hasCreditAccount="bool",
       **{"availableCredit?": ("desc", "money", "Shown only with sales.credit.sell (D-25)")})
schema("SaleDiscountInput", expectedVersion="int", kind=("enum", ["Percent", "Amount"]), value="str",
       **{"lineNo?": "int", "approvalId?": "uuid"})
schema("AttachCustomer", expectedVersion="int", **{"qrToken?": "str", "publicCode?": "str", "tenantCustomerId?": "uuid"},
       **{"buyerTaxInfo?": {"type": ("enum", ["Individual", "Legal"]), "nationalId?": "str", "economicCode?": "str"}})
schema("TenderInput", method=("enum", ["Cash", "ExternalCardTerminal", "CustomerCredit", "StoreCredit"]), amount="money",
       **{"tendered?": ("desc", "money", "Cash only: amount handed over; change is computed (SAL-006.1)"),
          "referenceNo?": ("desc", "str", "ExternalCardTerminal: tracking/reference number (PAY-002)"),
          "terminalNo?": "str", "cardLast4?": "str",
          "approvalId?": ("desc", "uuid", "A manager's approval to record a repeated card reference (PAY-002.1)")})
schema("VersionOnly", expectedVersion="int")
schema("TenderVoid", expectedVersion="int", reason=("desc", "str", "3-200 characters, audited"))
schema("CancelRequest", expectedVersion="int", reason=("desc", "str", "3-200 characters, audited"))
schema("HoldRequest", expectedVersion="int", **{"note?": ("desc", "str", "Up to 100 characters: a name or note to find the cart by (SAL-011)")})
schema("ResumeRequest", expectedVersion="int", **{"registerId?": ("desc", "uuid", "A register of the cart's branch with the caller's open shift; default: the cart's")})
schema("HeldSale", id="uuid", holdNote="str?", total="money", lines="int", cashierId="uuid", registerId="uuid", heldAt="dt", version="int")
schema("JournalReverse", reason=("desc", "str", "3-200 characters; becomes the reversal's description"))
schema("FinalizeRequest", expectedVersion="int", expectedTotal="money",
       **{"paymentConfirmationId?": ("desc", "str", "Optional; payments are read server-side (D03 §6.3)"),
          "approvalId?": "uuid"})
schema("ReturnLineInput", lineNo="int", qty="qty", condition=("enum", ["Resellable", "Damaged"]))
schema("ReturnCreate", lines=["#ReturnLineInput"], reason=("enum", ["Defective", "WrongItem", "CustomerChangedMind", "Other"]),
       refundMethod=("enum", ["Original", "Cash", "StoreCredit", "ReduceCredit"]), **{"note?": "str", "approvalId?": "uuid", "branchId?": "uuid"})
schema("SaleReturn", id="uuid", saleId="uuid", lines=[{"lineNo": "int", "qty": "qty", "amount": "money"}],
       refundTotal="money", refund={"id": "uuid", "method": "str", "status": ("enum", ["Completed", "Processing", "NeedsReview", "AwaitingManual"])},
       createdAt="dt")
schema("DeliveryRequest", channel=("enum", ["Auto", "Sms", "Bale"]), **{"guestPhone?": "str", "otpChallengeId?": "uuid", "otpCode?": "str"})
schema("Notification", id="uuid", channel=("enum", ["Sms", "Bale"]),
       status=("enum", ["Queued", "Sending", "Sent", "Delivered", "Failed", "Retrying", "DeadLettered"]),
       attempts="int", lastError="str?", updatedAt="dt")

# Payments
schema("PaymentIntentCreate", saleId="uuid", gatewayId="uuid", **{"amount?": "money", "sendLinkToCustomer?": "bool"})
schema("PaymentIntent", id="uuid", saleId="uuid?", collectionFor="uuid?", amount="money",
       status=("enum", ["Initiated", "AwaitingCustomer", "PendingVerification", "Confirmed", "Failed", "Expired",
                        "NeedsReview", "Settled", "Reversed"]),
       paymentUrl="str?", expiresAt="dt", gateway="str", providerRef="str?")
schema("RefundManualCompletion", reference="str", **{"note?": "str"})
schema("Refund", id="uuid", amount="money", method="str", status=("enum", ["Requested", "Processing", "Completed", "Failed", "NeedsReview", "ManualCompleted"]))
schema("SettlementImportCreate",
       fileName=("desc", "str", "1-200 characters; shown in the import list"),
       content=("desc", "str", "The bank's settlement report as CSV text (UTF-8, at most 1 MiB, 5000 rows). First row names the columns "
                "(English or Persian): reference_no, transaction_date, amount required; terminal_no, fee optional. Persian digits and "
                "thousands separators accepted; dates ISO or Jalali (1405/09/03); amounts in rials (RQ-16)."))
SETTLEMENT_OUTCOMES = ["Matched", "AmountMismatch", "NotFound", "Ambiguous", "AlreadySettled", "DuplicateInFile", "Unfinalized"]
schema("SettlementLine", lineNo="int", terminalNo="str?", referenceNo="str", transactionDate="date", amount="money", fee="money",
       outcome=("enum", SETTLEMENT_OUTCOMES),
       paymentId=("desc", "uuid?", "The payment it settled (Matched) or resembles (AmountMismatch, Unfinalized)"))
schema("SettlementImport", id="uuid", fileName="str", lineCount="int", matchedCount="int",
       matchedAmount=("desc", "money", "Σ gross of the matched lines: what left CardInTransit (D08 J4)"),
       fees=("desc", "money", "What the bank kept (BankFees)"), deposited=("desc", "money", "matchedAmount − fees (Bank)"),
       journalEntryId=("desc", "uuid?", "The J4 entry; null when nothing matched"), importedBy="uuid", importedAt="dt",
       **{"outcomes?": ("desc", "obj", "Count of lines per outcome (with the lines)"), "lines?": ["#SettlementLine"]})
schema("UnreconciledPayment", paymentId="uuid", saleId="uuid", invoiceNo="str", referenceNo="str", terminalNo="str?", cardLast4="str?",
       amount="money", businessDate="date", cashierId="uuid", cashierName="str?", registerId="uuid?", recordedAt="dt")
schema("UnreconciledReport", **{"from": "date"}, to="date",
       truncated=("desc", "bool", "More than 2000 open payments in the range: narrow it"),
       total={"count": "int", "amount": "money"},
       byCashier=[{"cashierId": "uuid", "displayName": "str?", "count": "int", "amount": "money"}],
       items=["#UnreconciledPayment"])
schema("GatewayConfigInput", gatewayType="str", displayName="str", credentials={"_desc": "Write-only; stored in OpenBao (PAY-005)",
                                                                                 "merchantId": "str", "terminalId?": "str", "secret?": "str"},
       enabled="bool")
schema("GatewayConfig", id="uuid", gatewayType="str", displayName="str", enabled="bool",
       capabilities=[("enum", ["Settle", "Reverse", "Refund", "Inquiry"])], credentialsMasked="str")

# Accounting
schema("AccountCreate", code="str", name="str", accountType=("enum", ["Asset", "Liability", "Equity", "Revenue", "Expense"]),
       **{"parentId?": "uuid", "requiresDetail?": "bool"})
schema("Account", id="uuid", code="str", name="str", accountType="str", parentId="uuid?", isSystem="bool", requiresDetail="bool")
schema("AccountMappingSet", mappings=[{"role": "str", "accountId": "uuid", "branchId?": "uuid"}])
schema("JournalLineInput", accountId="uuid", **{"detailType?": ("enum", ["Customer", "Supplier", "Branch"]), "detailId?": "uuid",
                                                "debit?": "money", "credit?": "money", "description?": "str"})
schema("JournalCreate", effectiveAt="dt", description="str", lines=["#JournalLineInput"])
schema("Journal", id="uuid", status=("enum", ["Draft", "Posted", "Reversed"]), effectiveAt="dt", description="str?",
       source="#SourceRef?", lines=["#JournalLine"], reversalOf="uuid?", reversedBy="uuid?")
schema("SourceRef", type="str", id="uuid")
schema("JournalLine", lineNo="int", accountId="uuid", accountCode="str", detailType="str?", detailId="uuid?", debit="money", credit="money")
schema("Period", id="uuid", fiscalYear="int", periodNo="int", start="dt", end="dt", status=("enum", ["Open", "Closed", "Locked"]),
       closedAt="dt?", closedBy="uuid?", version="int")
schema("TrialBalanceRow", accountId="uuid", accountCode="str", accountName="str",
       accountType=("enum", ["Asset", "Liability", "Equity", "Revenue", "Expense"]), parentId="uuid?",
       postable=("desc", "bool", "false: a group; its amounts are the sum of the accounts under it"),
       level=("desc", "int", "Depth in the chart (0 = top group)"),
       openingDebit=("desc", "money", "Net balance before `from`, on its side"), openingCredit="money",
       periodDebit=("desc", "money", "Gross movement in [from, to)"), periodCredit="money",
       closingDebit=("desc", "money", "Net balance at `to`, on its side"), closingCredit="money")
TB_AMOUNTS = {k: "money" for k in ["openingDebit", "openingCredit", "periodDebit", "periodCredit", "closingDebit", "closingCredit"]}
schema("TrialBalance", **{"from": "dt"}, to="dt", items=["#TrialBalanceRow"],
       totals={**TB_AMOUNTS, "balanced": "bool"}, meta="#ReportMeta",
       _desc="ACC-007: 4-column (opening, period debit/credit, closing) and 2-column (closing) views of the same data. "
             "Totals count postable accounts only.")
schema("LedgerRow", journalId="uuid", effectiveAt="dt", description="str?", sourceType="str?", accountId="uuid", accountCode="str",
       detailType="str?", detailId="uuid?", debit="money", credit="money",
       runningBalance=("desc", "money", "Debit − credit after this line, from the opening (negative: a credit balance)"))
schema("Ledger", account={"id": "uuid", "code": "str", "name": "str", "accountType": "str", "postable": "bool"}, detailId="uuid?",
       **{"from": "dt"}, to="dt", opening={"debit": "money", "credit": "money"}, periodDebit="money", periodCredit="money",
       closing={"debit": "money", "credit": "money"}, items=["#LedgerRow"], page="#PageInfo")

# Receivables
schema("CreditAccountCreate", tenantCustomerId="uuid", creditIdentity={"type": ("enum", ["Individual", "Legal"]),
       "fullName": "str", "nationalId?": "str", "economicCode?": "str"}, creditLimit="money",
       **{"paymentTermDays?": "int", "blockOnOverdue?": "bool", "approvalId?": "uuid"})
schema("CreditAccount", id="uuid", tenantCustomerId="uuid", maskedName="str", creditLimit="money", outstanding="money",
       available="money", paymentTermDays="int", status=("enum", ["Active", "Suspended", "Closed"]),
       oldestOverdueDueDate="date?", version="int")
schema("CreditAccountUpdate", expectedVersion="int", **{"creditLimit?": "money", "paymentTermDays?": "int",
       "status?": ("enum", ["Active", "Suspended", "Closed"]), "approvalId?": "uuid", "reason?": "str"})
schema("CollectionCreate", creditAccountId="uuid", amount="money", method=("enum", ["Cash", "ExternalCardTerminal"]),
       **{"referenceNo?": "str", "shiftId?": "uuid", "allocations?": [{"chargeId": "uuid", "amount": "money"}]})
schema("Collection", id="uuid", amount="money", allocations=[{"chargeId": "uuid", "saleId": "uuid", "amount": "money"}],
       advanceCreated="money", journalEntryId="uuid", recordedAt="dt")
schema("AgingRow", creditAccountId="uuid", maskedName="str", bucket0to30="money", bucket31to60="money",
       bucket61to90="money", bucketOver90="money", total="money")
schema("StatementLine", date="dt", type=("enum", ["Charge", "Collection", "CreditNote", "WriteOff", "Adjustment"]),
       reference="str", debit="money", credit="money", balance="money", dueDate="date?")
schema("WriteOffCreate", chargeIds=["uuid"], reason="str", approvalId="uuid")

# Customers (merchant audience)
schema("IdentifyRequest", **{"qrToken?": "str", "publicCode?": "str"})
schema("IdentifyResponse", result=("enum", ["member", "not_member", "invalid"]),
       **{"tenantCustomerId?": "uuid", "maskedName?": "str", "hasCreditAccount?": "bool",
          "joinRequestId?": ("desc", "uuid", "When not_member via QR: consent prompt pushed to customer's device")})
schema("CustomerOtpChallengeCreate", purpose=("enum", ["LinkMembership", "GuestReceipt"]), **{"publicCode?": "str", "phone?": "str"})
schema("OtpChallenge", challengeId="uuid", expiresAt="dt", resendAfter="dt")
schema("MembershipLinkRequest", challengeId="uuid", code="str",
       consents=[{"purpose": ("enum", ["Operational", "Marketing"]), "channel": ("enum", ["Sms", "Bale"]), "granted": "bool"}])
schema("TenantCustomer", tenantCustomerId="uuid", maskedName="str", maskedPhone="str", joinedAt="dt",
       consents=["#ConsentState"], **{"phone?": ("desc", "str", "Full phone only with customers.pii.view; audited (BR-062)")})
schema("ConsentState", purpose=("enum", ["Operational", "Marketing"]), channel=("enum", ["Sms", "Bale", "Email", "Push"]),
       granted="bool", updatedAt="dt", policyVersion="str")
schema("StoreInviteCreate", branchId="uuid", **{"expiresInDays?": "int"})
schema("StoreInvite", id="uuid", url="str", qrPngUrl="str", expiresAt="dt")

# Customer portal
schema("PortalOtpStart", phone="str", **{"captchaToken?": "str"})
schema("PortalOtpVerify", challengeId="uuid", code="str")
schema("PortalSession", expiresAt="dt", isNewCustomer="bool")
schema("CustomerMe", publicCode="str", maskedPhone="str", baleLinked="bool")
schema("CustomerQr", token="str", expiresAt="dt")
schema("Membership", membershipId="uuid", storeName="str", joinedAt="dt", consents=["#ConsentState"], hasCreditAccount="bool")
schema("ConsentUpdate", consents=[{"purpose": ("enum", ["Operational", "Marketing"]), "channel": ("enum", ["Sms", "Bale", "Email", "Push"]), "granted": "bool"}],
       policyVersion="str")
schema("JoinRequest", consents=[{"purpose": ("enum", ["Operational", "Marketing"]), "channel": ("enum", ["Sms", "Bale"]), "granted": "bool"}],
       policyVersion="str")
schema("CustomerReceiptSummary", saleId="uuid", storeName="str", invoiceNo="str", total="money", finalizedAt="dt", receiptUrl="str")
schema("BaleLinkToken", deepLink="str", expiresAt="dt")
schema("CustomerCreditSummary", storeName="str", outstanding="money", overdue="money", nextDueDate="date?",
       openCharges=[{"chargeId": "uuid", "invoiceNo": "str", "amount": "money", "open": "money", "dueDate": "date"}])
schema("PortalPaymentCreate", membershipId="uuid", amount="money")
schema("StoreAccessEvent", storeName="str", at="dt", kind=("enum", ["Lookup", "ReceiptLinked"]))
schema("ReceiptPublic", storeName="str?", branchName="str?", invoiceNo="str", finalizedAt="dt",
       lines=[{"name": "str", "qty": "qty", "unit": "str", "unitPrice": "money", "discount": "money", "tax": "money", "lineTotal": "money"}],
       totals={"gross": "money", "discount": "money", "net": "money", "tax": "money", "total": "money"},
       tenders=[{"method": "str", "amount": "money"}], credit="#ReceiptCredit?",
       legalText=("desc", "str?", "The industry template version's legal text (BR-086): never removed by the store"),
       lockedFields=["str"], templateVersion=("desc", "str?", "The version in force at Finalize, e.g. supermarket@3"),
       footer="str?", signature="str?", **{"taxStatus?": ("enum", ["Pending", "Accepted", "Rejected"])})
schema("PrintRequest", format=("enum", ["80mm", "A4"]))
schema("PrintedReceipt", receipt="#ReceiptPublic", format=("enum", ["80mm", "A4"]),
       copy=("desc", "int", "1 for the original; every reprint is numbered and printed as a copy"), isCopy="bool",
       receiptUrl=("desc", "str", "For the QR: a new 128-bit receipt link (90 days, revocable; CUS-009), shown only now"),
       linkExpiresAt="dt")
schema("ReceiptCredit", amount="money", dueDate="date")
schema("RevokedCount", revoked="int")

# Tax
schema("TaxProfile", personType=("enum", ["Individual", "Legal"]), nationalId="str", economicCode="str",
       issuingUnits=[{"branchId": "uuid", "memoryId": ("desc", "str", "شناسه حافظه مالیاتی"), "activeKeyId": "uuid?"}],
       submissionEnabled="bool", version="int")
schema("TaxProfileUpdate", expectedVersion="int", personType=("enum", ["Individual", "Legal"]), nationalId="str", economicCode="str",
       issuingUnits=[{"branchId": "uuid", "memoryId": "str"}])
schema("SigningKeyCreate", branchId="uuid")
schema("SigningKey", id="uuid", branchId="uuid", status=("enum", ["Generated", "Registered", "Active", "Retired", "Revoked"]),
       publicKeyFingerprint="str", createdAt="dt")
schema("Csr", format=("enum", ["PEM"]), content="str")
schema("ConnectionTestResult", ok="bool", checkedAt="dt", message="str?")
schema("TaxInvoice", id="uuid", sourceType=("enum", ["Sale", "SaleReturn"]), sourceId="uuid",
       kind=("enum", ["Original", "Return", "Correction", "Cancellation"]),
       status=("enum", ["Pending", "NeedsReview", "Signed", "Sent", "Accepted", "Rejected", "Superseded"]),
       taxUniqueNo="str?", deadlineAt="dt", errors=[{"code": "str", "message": "str"}], lastAttemptAt="dt?")
schema("TaxDashboard", byStatus=[{"status": "str", "count": "int"}], oldestPendingAt="dt?", nearestDeadlineAt="dt?",
       atRisk="int")

# Templates / Reports / Audit / Integrations
schema("ReceiptTemplate", industryTemplateVersion=("desc", "str", "e.g. supermarket@3: the industry version a receipt cites (BR-086)"),
       industryName="str", lockedFields=["str"], legalText="str",
       overrides={"logoObjectKey?": "str", "footerText?": "str", "signature?": "str"},
       version=("desc", "int", "The store's override version; 0 before its first change"))
schema("ReceiptTemplateUpdate", expectedVersion="int",
       overrides={"logoObjectKey?": ("desc", "str", "Not yet supported (uploads with US-092)"), "footerText?": ("desc", "str", "Up to 300 characters"),
                  "signature?": ("desc", "str", "Up to 100 characters")})
schema("SalesDailyRow", businessDate=("desc", "date", "The store's local day (its time zone), not UTC"), jalaliDate="jalali", branchId="uuid",
       registerId=("desc", "uuid?", "Only with groupBy=register"), cashierId=("desc", "uuid?", "Only with groupBy=cashier"),
       invoices="int", gross="money", discount="money", tax="money", net=("desc", "money", "Σ line net: gross − discount"),
       returns=("desc", "money", "Always 0 until sales returns exist (US-093)"))
schema("StockReportRow", warehouseId="uuid", variantId="uuid", sku="str?", name="str?", onHand="qty", value=("desc", "money", "Σ qty × WAC"),
       belowReorderPoint="bool")
schema("GrossProfitRow", key=("desc", "str", "The day (yyyy-MM-dd, store-local), the branch id or the variant id, by groupBy"), name=("desc", "str?", "SKU and name, with groupBy=product"),
       netSales="money", cost=("desc", "money", "Σ costAtSale: the moving average of the moment of sale"), grossProfit="money", marginBp="int",
       costBasis=("enum", ["MovingWeightedAverageAtSale"]),
       itemsWithZeroCost=("desc", "int", "Lines sold with a zero or unknown cost: their profit is overstated (RPT-001.3)"))
schema("ReturnsReportRow", reason="str", cashierId="uuid", count="int", amount="money")
schema("ReportMeta", generatedAt="dt", dataAsOf=("desc", "dt", "Read model freshness (RPT-002)"))
schema("AuditRecord", seq="long", occurredAt="dt", actorKind="str", actorId="uuid?", actorRoles=["str"], impersonatedBy="uuid?",
       action="str", resourceType="str", resourceId="str?", reason="str?", branchId="uuid?", terminalId="uuid?",
       clientIp="str?", details=("desc", "obj", "Sanitised at write time: no OTP, token, PIN, key, card number or contact PII (AUD-003)"))
schema("IntegrationKeyCreate", name="str", scopes=[("enum", ["catalog.read", "inventory.read"])],
       expiresAt=("desc", "dt", "At most 365 days ahead"))
schema("IntegrationKey", id="uuid", name="str", prefix=("desc", "str", "The public part of the key (rck_<prefix>_...)"), scopes=["str"],
       createdAt="dt", expiresAt="dt", revokedAt="dt?", lastUsedAt="dt?", status=("enum", ["Active", "Expired", "Revoked"]),
       **{"secret?": ("desc", "str?", "The full key, returned once at creation only; only the SHA-256 of its secret is kept")})
schema("PartnerBalance", warehouseId="uuid", variantId="uuid", onHand="qty", reserved="qty", available="qty")

# Platform admin
schema("PlatformTenant", id="uuid", legalName="str", status="str", plan="str", createdAt="dt", branches="int")
schema("PlatformTenantStatusChange", status=("enum", ["Active", "Suspended", "Closed"]), reason="str")
schema("TaxSubmissionPolicy", id="uuid", validFrom="dt", periodType=("enum", ["Monthly", "Quarterly", "Custom"]),
       deadlineOffsetDays="int", **{"appliesTo?": "str"}, version="int")
schema("TaxSubmissionPolicyCreate", validFrom="dt", periodType=("enum", ["Monthly", "Quarterly", "Custom"]),
       deadlineOffsetDays="int", reason="str", **{"appliesTo?": "str"})
RETENTION_CATEGORIES = ["FinancialDocuments", "TaxInvoices", "InventoryLedger", "Audit", "CustomerCrm", "CustomerIdentity", "Notifications", "TechnicalLogs"]
schema("RetentionPolicy", category=("enum", RETENTION_CATEGORIES), version="int", minimumDays="int", defaultDays="int", validFrom="dt",
       reason="str")
schema("RetentionPolicyInput", category=("enum", RETENTION_CATEGORIES), minimumDays="int", defaultDays="int",
       **{"validFrom?": ("desc", "dt", "Defaults to now; a future date schedules the new version")}, reason="str")
schema("LegalHoldInput", reason="str", **{"until?": ("desc", "dt", "Omitted: until an operator lifts it")})
schema("LegalHold", tenantId="uuid", category=("enum", RETENTION_CATEGORIES), reason="str", until="dt?")
schema("StoreRetention", category=("enum", RETENTION_CATEGORIES), minimumDays="int", defaultDays="int", overrideDays="int?", effectiveDays="int",
       legalHold="bool", holdReason="str?", holdUntil="dt?")
schema("RetentionDaysInput", days="int")
schema("SmsTemplate", code=("enum", ["receipt", "due-reminder"]), version=("desc", "int", "0 = the store never changed it"), platformText="str",
       storeName="str?", signature="str?", preview="str")
schema("SmsTemplateInput", expectedVersion="int", **{"storeName?": "str", "signature?": "str"})
schema("RetentionRun", category=("enum", RETENTION_CATEGORIES), outcome=("enum", ["Purged", "Held"]), cutoff="dt", deletedRows="int", ranAt="dt")
schema("IndustryProfile", code="str", name="str", receiptTemplateVersion="str", version="int", lockedFields=["str"], legalText="str",
       reason="str", publishedAt="dt")
schema("IndustryProfileUpdate", expectedVersion=("desc", "int", "The newest version; the update publishes the next one"), legalText="str",
       lockedFields=("desc", ["str"], "Known receipt fields; legalName, invoiceNo, issuedAt, lines and totals are always locked"), reason="str")
schema("CatalogReviewDecision", decision=("enum", ["Publish", "Reject"]),
       reason=("desc", "str", "3-500 characters; kept with the decision"),
       **{"edits?": ("desc", {"name?": "str", "brand?": "str", "category?": "str"},
                     "Publish only: the moderator's correction of the suggested text")})
schema("CatalogSuggestion", id="uuid", gtin14="str", fields={"name": "str", "brand?": "str?", "category?": "str?"}, submittedAt="dt",
       storesForBarcode=("desc", "int", "Pending suggestions for the same code (each from a different store; which ones is never known)"),
       existing=("desc", {"catalogProductId": "uuid", "name": "str", "brand?": "str?", "category?": "str?", "verifiedAt": "dt"},
                 "What the global catalog already holds for this code; null when nothing"))

# ----------------------------------------------------------------------------- reusable components
PARAMS = {
    "IdempotencyKey": {"name": "Idempotency-Key", "in": "header", "required": True,
                       "description": "Client-generated unique key (UUID/ULID), 16–64 chars. Same key + same payload "
                                      "replays the stored success (same status, identical body, header Idempotent-Replayed: true); "
                                      "same key + different payload or path -> 409 idempotency-key-reused; a duplicate still "
                                      "running after 5 s -> 409 in-progress with Retry-After (BR-030/031, D-34).",
                       "schema": {"type": "string", "minLength": 16, "maxLength": 64}},
    "Cursor": {"name": "cursor", "in": "query", "required": False, "schema": {"type": "string"}},
    "Limit": {"name": "limit", "in": "query", "required": False,
              "schema": {"type": "integer", "minimum": 1, "maximum": 200, "default": 50}},
    "From": {"name": "from", "in": "query", "required": False, "schema": {"type": "string", "format": "date-time"}},
    "To": {"name": "to", "in": "query", "required": False, "schema": {"type": "string", "format": "date-time"}},
    "BranchFilter": {"name": "branchId", "in": "query", "required": False, "schema": {"type": "string", "format": "uuid"}},
}
STD_RESPONSES = {
    "BadRequest": ("400", "Malformed request."),
    "Unauthorized": ("401", "Missing/expired credentials, or wrong audience (BR-004)."),
    "Forbidden": ("403", "Permission/scope denied, mfa-required, step-up-required, entitlement-exceeded, or "
                         "approval-required (with approvalRequestId)."),
    "NotFound": ("404", "Not found — also returned for resources of another tenant (BR-003, D-48)."),
    "Conflict": ("409", "Business conflict (version-conflict, insufficient-stock, price-changed, period-closed, ...)."),
    "Unprocessable": ("422", "Validation failed; see errors[]."),
    "TooManyRequests": ("429", "Rate limit exceeded; see Retry-After."),
}
RESPONSES = {}
for name, (code, desc) in STD_RESPONSES.items():
    r = {"description": desc, "content": {"application/problem+json": {"schema": {"$ref": "#/components/schemas/Problem"}}}}
    if name == "TooManyRequests":
        r["headers"] = {"Retry-After": {"schema": {"type": "integer"}, "description": "Seconds"}}
    RESPONSES[name] = r

ERROR_CATALOG = {
    "version-conflict": (409, "expectedVersion mismatch (BR-034)"),
    "idempotency-key-reused": (409, "Same key, different payload (BR-031)"),
    "in-progress": (409, "Same key still processing; retry after Retry-After"),
    "insufficient-stock": (409, "context.shortages[] (INV-003)"),
    "price-changed": (409, "context.lines[] new prices; re-confirm (SAL-005.1)"),
    "payment-not-confirmed": (409, "A tender is not confirmed yet"),
    "payment-failed": (409, "Gateway verify failed"),
    "period-closed": (409, "Fiscal period closed (DI-042)"),
    "period-has-drafts": (409, "Draft entries are dated in the period; post or delete them (ACC-009)"),
    "earlier-period-open": (409, "Periods are closed in order (ACC-009)"),
    "period-not-ended": (409, "A period is closed once it has ended (ACC-009)"),
    "no-open-period": (422, "No fiscal period covers this date (ACC-001.1)"),
    "payment-incomplete": (409, "Confirmed payments do not cover the bill; context.remaining (SAL-006)"),
    "sale-empty": (409, "A sale without items is not finalized or held"),
    "sale-not-held": (409, "Only a held cart is resumed (SAL-011)"),
    "count-open": (409, "The warehouse already has a count in progress (INV-009)"),
    "count-not-open": (409, "The count is closed: nothing more is counted"),
    "count-not-closed": (409, "A count is closed before it is confirmed"),
    "count-blind": (403, "While a blind count is open its system quantities are shown only to who may confirm it (UC-09)"),
    "no-sellable-warehouse": (422, "The branch has no sellable warehouse to issue the stock from (INV-001)"),
    "amount-exceeds-remaining": (409, "A payment larger than what is left on the bill; context.remaining (SAL-006)"),
    "sale-has-payments": (409, "Payments were taken; the bill's lines no longer change (RC026)"),
    "barcode-not-found": (404, "No product of this store has this code: define it (UC-01) or search by name (UC-02 2a)"),
    "price-missing": (422, "The product has no price in the default list yet (CAT-009)"),
    "not-sellable": (422, "The product or its combination is switched off (CAT-008)"),
    "weighted-line": (409, "A weighed item's quantity comes from its label (CAT-007)"),
    "sale-not-draft": (409, "Only a draft cart changes (RC026)"),
    "client-draft-id-taken": (409, "The clientDraftId belongs to another sale (DI-010)"),
    "account-role-unmapped": (422, "An automatic entry needs an account role with no account mapped (ACC-004, D-30)"),
    "journal-not-draft": (409, "Only a draft entry can be posted (ACC-003)"),
    "journal-already-reversed": (409, "An entry is reversed once (DI-045)"),
    "sale-finalized": (409, "Finalized sale is immutable (DI-003)"),
    "shift-not-open": (409, "Cashier has no open shift on this register (BR-050)"),
    "shift-already-open": (409, "Register already has an open shift (DI-008)"),
    "variance-not-pending": (409, "The shift has no variance waiting for approval (UC-06)"),
    "barcode-already-assigned": (409, "context.productId (CAT-006)"),
    "duplicate-reference": (409, "Card terminal reference already used (PAY-002.1)"),
    "credit-limit-exceeded": (409, "context.available (BR-082)"),
    "credit-overdue": (409, "Overdue charges block new credit (BR-082)"),
    "credit-account-suspended": (409, "CRD-008"),
    "exceeds-returnable-quantity": (422, "BR-053"),
    "confirmation-required": (422, "Low-confidence catalog data (CAT-004.1) or UC-05 receipt warnings (context.warnings[]) must be confirmed"),
    "unknown-unit": (422, "The unit has no conversion to the product's base unit (BR-044); context.units[]"),
    "duplicate-national-id": (409, "A supplier with this national id already exists (PRC-001)"),
    "invalid-barcode": (422, "Check digit/format (CAT-003)"),
    "approval-required": (403, "approvalRequestId returned; retry with approvalId (IAM-007)"),
    "mfa-required": (403, "IAM-003"),
    "step-up-required": (403, "ABAC-05"),
    "entitlement-exceeded": (403, "TEN-003"),
    "owner-only": (403, "Only a TenantOwner may do this, even with the permission (SoD-05: support access)"),
    "support-access-not-open": (409, "The support request is no longer pending, or the grant already ended (IAM-008)"),
    "export-in-progress": (409, "One export at a time per store (TEN-005, TM-015)"),
    "tenant-read-only": (403, "Suspended, closed or trial-expired tenant: writes refused except x-allowed-when-read-only (TEN-004)"),
    "terminal-not-registered": (403, "ABAC-04"),
    "qr-expired": (401, "Customer QR expired or already used (CUS-005)"),
    "invalid-qr": (401, "Wrong audience/signature"),
    "otp-invalid": (422, "Wrong/expired OTP; attempts remaining in context"),
    "rate-limited": (429, "Generic rate limit"),
    "not-found": (404, "Not found or belongs to another tenant — indistinguishable (BR-003, D-48)"),
    "invalid-token": (401, "Token lacks required claims"),
    "user-not-provisioned": (403, "Valid identity but no active RetailCloud user (IAM-001, IAM-009)"),
    "tenant-not-selected": (403, "Tenant-scoped operation without an active tenant (BR-001)"),
    "idempotency-key-required": (400, "Missing or malformed Idempotency-Key on an operation of BR-030"),
    "validation": (400, "Malformed request body or parameters; context.errors[]"),
    "internal": (500, "Unexpected error; details only in logs, correlate with traceId (SEC-T-016)"),
    "forbidden": (403, "The caller's roles do not grant the permission at this scope (IAM-006, D06 §6.1)"),
    "duplicate-assignment": (409, "The user already has this role at this scope"),
    "last-owner": (409, "A tenant must keep at least one active TenantOwner"),
    "cannot-disable-self": (409, "A user cannot disable their own access (updateUser)"),
    "pin-invalid": (403, "Wrong PIN; context attemptsLeft (AUTH-08)"),
    "pin-locked": (403, "PIN locked after 5 failures; a full sign-in and a new PIN are needed"),
    "pin-not-set": (409, "The user has not set a PIN"),
    "reauth-required": (403, "Sign in again (setting a PIN needs a sign-in within 10 minutes)"),
    "self-approval": (403, "The requester cannot approve their own request (SoD-01)"),
    "approval-expired": (409, "The pending request expired (10 minutes)"),
    "approval-not-pending": (409, "The request was already decided"),
    "below-retention-floor": (422, "The store cannot keep data for less than the platform's minimum for the category (BR-087, RC036)"),
    "report-too-large": (422, "The report has more rows than the cap (5,000; 100,000 as csv): narrow the range or the branch; context.limit"),
    "already-imported": (409, "This settlement file was imported before; context.importId (PAY-002.2)"),
    "invalid-settlement-file": (400, "The settlement CSV has unreadable rows; nothing imported; context.rows[] (line, field, message)"),
    "duplicate-code": (409, "Branch code (per tenant), register code (per branch), product SKU (per tenant) or variant axis values (per product, DI-081) already used"),
}

# ----------------------------------------------------------------------------- operations
PATHS = {}
TAGS = []
OPIDS = set()


def tag(name, desc):
    TAGS.append({"name": name, "description": desc})


SEC = {
    "merchant": [{"merchantBearer": []}],
    "customer": [{"customerBearer": []}],
    "platform": [{"platformBearer": []}],
    "integration": [{"integrationKey": []}],
    "public": [],
}


# TEN-004 / US-015: a suspended or trial-expired tenant is read-only. Every write is refused with 403 tenant-read-only
# EXCEPT these: reads stay open anyway; export, debt collection and the tax queue continue (TEN-004); access can still
# be reduced (revoke a device, remove a role, disable a user — re-enabling is refused) and the register unlocked to read; support can be let in to help.
READ_ONLY_ALLOWED = {
    "selectActiveTenant", "verifyMyPin", "revokeTerminal", "removeRoleAssignment", "updateUser", "createExport",
    "recordCollection", "resolveBarcode", "portalPayBalance", "portalExportMyData", "retryTaxInvoice", "decideSupportAccess", "revokeSupportAccess",
}


def op(method, path, op_id, tagname, summary, *, perm=None, scope=None, idem=False, reqs=(), body=None,
       resp=None, resp_code="200", aud="merchant", params=(), audited=False, conflict=False, approval=None,
       desc=None, list_of=None, page=False, examples=None, resp_example=None, rate=None, no_content=False):
    assert op_id not in OPIDS, op_id
    OPIDS.add(op_id)
    o = {"operationId": op_id, "tags": [tagname], "summary": summary}
    if desc:
        o["description"] = desc
    o["security"] = SEC[aud]
    plist = []
    for seg in path.split("/"):
        if seg.startswith("{"):
            n = seg[1:-1]
            plist.append({"name": n, "in": "path", "required": True,
                          "schema": {"type": "integer", "minimum": 1} if n == "lineNo" else
                          {"type": "string"} if n in ("token", "gateway", "secretPath", "inviteToken") else
                          {"type": "string", "format": "uuid"} if n.endswith("Id") or n == "id" else {"type": "string"}})
    if idem:
        plist.append({"$ref": "#/components/parameters/IdempotencyKey"})
    if page:
        plist += [{"$ref": "#/components/parameters/Cursor"}, {"$ref": "#/components/parameters/Limit"}]
    plist += list(params)
    if plist:
        o["parameters"] = plist
    if body:
        content = {"schema": {"$ref": f"#/components/schemas/{body}"}}
        if examples:
            content["examples"] = examples
        o["requestBody"] = {"required": True, "content": {"application/json": content}}
    responses = {}
    if no_content:
        responses["204"] = {"description": "No content"}
    else:
        if resp is None:
            responses[resp_code] = {"description": "OK"}
        else:
            if list_of:
                sch = {"type": "object", "additionalProperties": False, "required": ["items"],
                       "properties": {"items": {"type": "array", "items": {"$ref": f"#/components/schemas/{resp}"}}}}
                if page:
                    sch["properties"]["page"] = {"$ref": "#/components/schemas/PageInfo"}
                    sch["required"].append("page")
                if resp.endswith("Row"):
                    sch["properties"]["meta"] = {"$ref": "#/components/schemas/ReportMeta"}
            else:
                sch = {"$ref": f"#/components/schemas/{resp}"}
            c = {"schema": sch}
            if resp_example:
                c["examples"] = resp_example
            responses[resp_code] = {"description": "Success", "content": {"application/json": c}}
            if method in ("get",) and not list_of:
                responses[resp_code]["headers"] = {"ETag": {"schema": {"type": "string"}, "description": "Weak ETag of version"}}
    if body:
        responses["400"] = {"$ref": "#/components/responses/BadRequest"}
    if aud != "public":
        responses["401"] = {"$ref": "#/components/responses/Unauthorized"}
    if aud == "merchant" and not perm:
        perm = "tenant.member"   # any active membership in the session tenant (explicit, never implicit; D04 R7)
    if perm or aud in ("customer", "platform", "integration"):
        responses["403"] = {"$ref": "#/components/responses/Forbidden"}
    if any(p.get("in") == "path" for p in plist if "in" in p):
        responses["404"] = {"$ref": "#/components/responses/NotFound"}
    if conflict or idem:
        responses["409"] = {"$ref": "#/components/responses/Conflict"}
    if body or params:
        responses["422"] = {"$ref": "#/components/responses/Unprocessable"}
    responses["429"] = {"$ref": "#/components/responses/TooManyRequests"}
    responses["default"] = {"description": "Unexpected error",
                            "content": {"application/problem+json": {"schema": {"$ref": "#/components/schemas/Problem"}}}}
    o["responses"] = responses
    if perm:
        o["x-permission"] = perm
    if scope:
        o["x-scope"] = scope
    o["x-idempotent"] = bool(idem)
    if reqs:
        o["x-requirements"] = list(reqs)
    if audited:
        o["x-audited"] = True
    if op_id in READ_ONLY_ALLOWED:
        o["x-allowed-when-read-only"] = True
    if approval:
        o["x-approval"] = approval
    if rate:
        o["x-rate-limit-class"] = rate
    PATHS.setdefault(path, {})[method] = o


def ex(summary, value):
    return {"summary": summary, "value": value}


# ---------------- Platform & tenant
tag("Tenant", "ثبت‌نام، تنظیمات، شعب، صندوق و دستگاه (TEN)")
op("post", "/tenants", "registerTenant", "Tenant", "ثبت‌نام کسب‌وکار (Owner باید MFA فعال کرده باشد)", perm="authenticated", body="TenantRegistration",
   resp="Tenant", resp_code="201", reqs=["TEN-001", "TEN-001.1"], audited=True, conflict=True, idem=True,
   desc="Callable by a valid identity that is not yet a RetailCloud user (self-service sign-up, UC-10); requires acr=mfa "
        "(403 mfa-required). The tenant id is derived from the caller identity and the Idempotency-Key, so a retry or "
        "double submit returns the same tenant. Branch B1 and register R1 are created synchronously; owner membership, "
        "warehouse W1, chart of accounts and fiscal periods follow from platform.tenant-registered (poll getTenantSetup).",
   examples={"supermarket": ex("سوپرمارکت", {"legalName": "فروشگاه نمونه", "industryCode": "supermarket",
                                             "firstBranchName": "شعبه مرکزی"})})
op("get", "/tenants/me/setup", "getTenantSetup", "Tenant", "پیشرفت راه‌اندازی خودکار Tenant (ویزارد UC-10)", resp="TenantSetup", reqs=["TEN-001.1"])
op("get", "/tenants/me", "getMyTenant", "Tenant", "Tenant فعال", resp="Tenant", reqs=["TEN-001"])
op("patch", "/tenants/me", "updateMyTenant", "Tenant", "به‌روزرسانی تنظیمات", perm="tenant.settings.manage", scope="Tenant",
   body="TenantUpdate", resp="Tenant", reqs=["TEN-001", "BR-040", "BR-052"], audited=True, conflict=True)
op("get", "/settings/retention", "getRetention", "Tenant", "مدت نگهداری هر دسته داده و Legal Hold", perm="tenant.settings.manage", scope="Tenant",
   resp="StoreRetention", list_of=True, reqs=["TEN-005.1", "BR-087"])
op("put", "/settings/retention/{category}", "setRetention", "Tenant", "نگهداری بیشتر از پیش‌فرض پلتفرم", perm="tenant.settings.manage", scope="Tenant",
   body="RetentionDaysInput", resp="StoreRetention", reqs=["TEN-005.1", "BR-087"], audited=True,
   desc="Only longer: 422 below-retention-floor under the platform's minimum. The platform's nightly job deletes data past this period "
        "unless a legal hold is in force.")
op("delete", "/settings/retention/{category}", "clearRetention", "Tenant", "بازگشت به پیش‌فرض پلتفرم", perm="tenant.settings.manage", scope="Tenant",
   no_content=True, reqs=["TEN-005.1", "BR-087"], audited=True)
op("get", "/settings/retention/runs", "listRetentionRuns", "Tenant", "اجراهای اخیر Job نگهداری (حذف یا توقف با Legal Hold)", perm="tenant.settings.manage",
   scope="Tenant", resp="RetentionRun", list_of=True, reqs=["TEN-005.1"])
op("get", "/settings/notification-templates", "listNotificationTemplates", "Tenant", "متن پیامک‌ها و بخش‌های قابل تغییر فروشگاه", perm="tenant.settings.manage",
   scope="Tenant", resp="SmsTemplate", list_of=True, reqs=["NOT-009"])
op("put", "/settings/notification-templates/{code}", "updateNotificationTemplate", "Tenant", "نام فروشگاه و امضای پیامک (نسخه‌ی بعدی)",
   perm="tenant.settings.manage", scope="Tenant", body="SmsTemplateInput", resp="SmsTemplate", reqs=["NOT-009"], audited=True,
   desc="Only the name the store signs with (30 characters) and a closing signature (40); a single line, no links. The text itself is the "
        "platform's. Each change is the next version (409 version-conflict if two people save at once); the Workers use the newest.")
op("get", "/branches", "listBranches", "Tenant", "فهرست شعب مجاز کاربر", resp="Branch", list_of=True, reqs=["TEN-002"])
op("post", "/branches", "createBranch", "Tenant", "ایجاد شعبه", perm="tenant.settings.manage", scope="Tenant",
   body="BranchCreate", resp="Branch", resp_code="201", reqs=["TEN-002"], audited=True, conflict=True,
   desc="Creates register R1 in the same transaction; the branch's sellable warehouse W-<code> follows from "
        "platform.branch-created (Inventory). code: ^[A-Z0-9][A-Z0-9-]{0,9}$, unique per tenant (409 duplicate-code).")
op("post", "/branches/{branchId}/registers", "createRegister", "Tenant", "ایجاد صندوق", perm="tenant.settings.manage",
   scope="Tenant", body="RegisterCreate", resp="Register", resp_code="201", reqs=["TEN-002"], audited=True, conflict=True)
op("post", "/branches/{branchId}/pairing-codes", "createPairingCode", "Tenant", "صدور کد جفت‌سازی دستگاه (۱۰ دقیقه)",
   perm="terminals.manage", scope="Branch", resp="PairingCode", resp_code="201", reqs=["TEN-002.1"], audited=True,
   desc="8 characters of the Crockford alphabet, single use, 10 minutes; only its SHA-256 is stored.")
op("post", "/terminals/pair", "pairTerminal", "Tenant", "جفت‌سازی دستگاه با کد", body="TerminalPairRequest", resp="Terminal",
   resp_code="201", reqs=["TEN-002.1"], audited=True, conflict=True, rate="pairing",
   desc="Run on the device by a signed-in member of the active tenant. Returns deviceSecret once; the BFF keeps it in an "
        "HttpOnly cookie and sends it as X-RC-Device, from which the API recognises the terminal (ABAC-04). Unknown, "
        "used and expired codes all answer 404.")
op("get", "/terminals", "listTerminals", "Tenant", "دستگاه‌های شعب در محدوده کاربر", perm="terminals.manage", scope="Branch",
   resp="Terminal", list_of=True, reqs=["TEN-002.1"])
op("delete", "/terminals/{terminalId}", "revokeTerminal", "Tenant", "ابطال دستگاه", perm="terminals.manage", scope="Branch",
   no_content=True, reqs=["TEN-002.1"], audited=True)
op("post", "/exports", "createExport", "Tenant", "Export ناهمگام داده", perm="data.export", scope="Tenant", body="ExportCreate",
   resp="ExportJob", resp_code="202", reqs=["TEN-005", "RPT-003"], audited=True,
   desc="Requires MFA within 15 minutes (ABAC-05). includeFullPhone=true requires customers.pii.view (403) and is refused "
        "with validation 'not-yet-supported' until customer contact decryption ships. One export at a time (409 "
        "export-in-progress). ZIP of UTF-8 CSVs + manifest.json; formula cells escaped (AB-18). Raises DET-08 when large.")
op("get", "/exports/{exportId}", "getExport", "Tenant", "وضعیت Export و لینک Presigned (۵ دقیقه)", perm="data.export",
   scope="Tenant", resp="ExportJob", reqs=["TEN-005", "D-49"])

# ---------------- IAM
tag("Access", "کاربران، نقش‌ها، Approval و دسترسی پشتیبانی (IAM)")
op("get", "/me", "getMe", "Access", "کاربر جاری، عضویت‌ها و مجوزهای مؤثر", perm="authenticated", resp="Me", reqs=["IAM-001", "IAM-006"])
op("post", "/me/active-tenant", "selectActiveTenant", "Access", "انتخاب Tenant فعال (BFF Session را بازسازی می‌کند)",
   perm="authenticated", body="ActiveTenantSelect", no_content=True, reqs=["IAM-001", "BR-001"], audited=True)
op("get", "/users", "listUsers", "Access", "کاربران Tenant", perm="iam.users.manage", scope="Branch", resp="User", list_of=True, page=True, reqs=["IAM-001", "IAM-006"])
op("post", "/users/invitations", "inviteUser", "Access", "دعوت کاربر با نقش و Scope", perm="iam.users.manage", scope="Branch",
   body="InvitationCreate", resp="Invitation", resp_code="201", reqs=["IAM-001", "IAM-006"], audited=True,
   desc="Each requested role must be assignable by the caller at that scope: the caller manages users there and holds "
        "every permission of the role over the same reach (anti-escalation, D06 §6); otherwise 403 forbidden. "
        "inviteToken is returned once; share it as a link until SMS/e-mail delivery exists (X-03).")
op("post", "/invitations/{token}/accept", "acceptInvitation", "Access", "پذیرش دعوت (یک‌بار مصرف، ۷ روز)", perm="authenticated",
   resp="InvitationAccepted", reqs=["IAM-001"], audited=True,
   desc="Callable by any valid identity holding the link, also before it is a RetailCloud user (the user is created). "
        "Unknown, used, revoked or expired tokens all answer 404.")
op("patch", "/users/{userId}", "updateUser", "Access", "فعال/غیرفعال‌سازی کاربر (ابطال Session در ≤ ۶۰s)",
   perm="iam.users.manage", scope="Branch", body="UserUpdate", resp="User", reqs=["IAM-009"], audited=True, conflict=True,
   desc="Disabled suspends the membership in the active tenant only (role assignments are kept); the user's next request "
        "to this tenant is refused within 60 s. The caller must hold every role the user has at its scope; nobody disables "
        "themselves; the last active owner stays. displayName is not yet editable here (it belongs to the person).")
op("post", "/users/{userId}/role-assignments", "addRoleAssignment", "Access", "تخصیص نقش", perm="iam.users.manage",
   scope="Branch", body="RoleAssignmentInput", resp="RoleAssignment", resp_code="201", reqs=["IAM-006"], audited=True, conflict=True)
op("delete", "/users/{userId}/role-assignments/{assignmentId}", "removeRoleAssignment", "Access", "حذف تخصیص نقش",
   perm="iam.users.manage", scope="Branch", no_content=True, reqs=["IAM-006"], audited=True)
op("get", "/roles", "listRoles", "Access", "نقش‌های سیستمی و سفارشی", perm="iam.users.manage", scope="Tenant", resp="Role", list_of=True, reqs=["IAM-006"])
op("put", "/me/pin", "setMyPin", "Access", "تعیین PIN صندوق (تا ۱۰ دقیقه پس از ورود کامل)", body="PinInput", no_content=True,
   reqs=["IAM-004", "AUTH-08"], audited=True)
op("post", "/me/pin/verify", "verifyMyPin", "Access", "باز کردن قفل صندوق با PIN (فقط Terminal ثبت‌شده؛ ۵ خطا = قفل)", body="PinInput",
   no_content=True, reqs=["IAM-004", "AUTH-08"], audited=True)
op("get", "/approvals", "listApprovals", "Access", "درخواست‌های تأیید باز: قابل تأیید توسط من و درخواست‌های خودم", resp="ApprovalRequest",
   list_of=True, reqs=["IAM-007"])
op("get", "/approvals/{approvalId}", "getApproval", "Access", "وضعیت درخواست تأیید", resp="ApprovalRequest", reqs=["IAM-007"])
op("post", "/approvals/{approvalId}/decision", "decideApproval", "Access", "تصمیم مدیر (تأییدکننده ≠ درخواست‌کننده)",
   body="ApprovalDecision", resp="ApprovalRequest", reqs=["IAM-007", "SoD-01"], audited=True, conflict=True,
   desc="Approver needs the permission of the requested action at the resource scope plus fresh MFA or PIN on the same device.")
op("get", "/support-access-requests", "listSupportAccessRequests", "Access", "درخواست‌های دسترسی پشتیبانی",
   perm="tenant.settings.manage", scope="Tenant", resp="SupportAccessRequest", list_of=True, reqs=["IAM-008"])
op("post", "/support-access-requests/{requestId}/decision", "decideSupportAccess", "Access", "تأیید یا رد JIT پشتیبانی (فقط Owner)",
   perm="tenant.settings.manage", scope="Tenant", body="ApprovalDecision", resp="SupportAccessRequest",
   reqs=["IAM-008", "SoD-05"], audited=True, conflict=True)
op("delete", "/support-access-requests/{requestId}", "revokeSupportAccess", "Access", "پایان زودهنگام دسترسی پشتیبانی (فقط Owner)",
   perm="tenant.settings.manage", scope="Tenant", resp="SupportAccessRequest", reqs=["IAM-008", "SoD-05", "AB-17"], audited=True,
   desc="Only a TenantOwner with MFA in the last 15 minutes; only an Approved, unexpired grant. Allowed while read-only.")

# ---------------- Catalog & merchandising
tag("Catalog", "Resolve بارکد، کالا، Variant، قیمت و تخفیف (CAT)")
op("post", "/catalog/barcodes/resolve", "resolveBarcode", "Catalog", "تشخیص بارکد و پیشنهاد مشخصات",
   perm="catalog.product.manage", scope="Branch", body="BarcodeResolveRequest", resp="BarcodeResolveResponse",
   reqs=["CAT-003", "CAT-004", "CAT-004.1", "CAT-005", "CAT-007"], rate="catalog-resolve",
   examples={"gtin": ex("EAN-13", {"barcode": "6260000000017", "branchId": "a0000000-0000-0000-0000-000000000001",
                                   "preferredSources": ["tenant", "global", "licensed_providers"]})},
   resp_example={"candidates": ex("دو پیشنهاد متعارض", {
       "kind": "candidates", "normalized": {"gtin14": "06260000000017", "isInternal": False, "isWeighted": False},
       "candidates": [
           {"source": "global", "confidence": "0.95", "lastVerifiedAt": "2026-10-01T08:00:00Z", "license": None,
            "attribution": None, "catalogProductId": "c0000000-0000-0000-0000-000000000001",
            "fields": {"name": "شیر کم‌چرب ۱ لیتری", "brand": "نمونه"}, "conflictsWith": [1], "preselect": False},
           {"source": "open_food_facts", "confidence": "0.50", "lastVerifiedAt": "2026-09-12T10:00:00Z",
            "license": "ODbL-1.0", "attribution": "Open Food Facts contributors", "catalogProductId": None,
            "fields": {"name": "Low fat milk 1L"}, "conflictsWith": [0], "preselect": False}],
       "degraded": False})},
   desc="Sample barcode values are illustrative and not guaranteed real GTINs. Never returns another tenant's "
        "price/stock/ids (P0-8). Providers time out at 2s each, 4s total; degraded=true when skipped. "
        "A weighted label (CAT-007) is decoded first: its item code is looked up as the variant's internal code and "
        "`normalized.weighted.value` is kilograms (3 decimals) or rials; a wrong value check digit is 422 invalid-barcode. "
        "`unitPrice` is the default list's price now; with it a weighted label also gets `quantity` and `amount` "
        "(a price label's amount is the printed price, CAT-007.1). "
        "A public code the store does not know gets candidates from the global catalog (0.95) and licensed providers (at most "
        "0.7; Open Food Facts is off until its licence review, X-06); provider answers are cached 30 days.")
op("get", "/products", "listProducts", "Catalog", "جستجوی کالا (بارکد، SKU، نام نرمال‌شده فارسی)",
   params=[{"name": "q", "in": "query", "required": False, "schema": {"type": "string"}},
           {"name": "barcode", "in": "query", "required": False, "schema": {"type": "string"}},
           {"$ref": "#/components/parameters/BranchFilter"}],
   resp="Product", list_of=True, page=True, reqs=["CAT-011", "SAL-001"],
   desc="`q`: an exact SKU or barcode, or up to 5 terms each matching the start of a word of the Persian-normalised name "
        "(Arabic yeh/kaf, digits, half-spaces and diacritics folded), in any order. Merged products are not returned. "
        "Ordered by normalised name; keyset paging.")
op("post", "/products", "createProduct", "Catalog", "تعریف کالا با Variant و بارکد (و موجودی افتتاحیه اختیاری)",
   perm="catalog.product.manage", scope="Tenant", idem=True, body="ProductCreate", resp="Product", resp_code="201",
   reqs=["CAT-001", "CAT-002", "CAT-002.1", "CAT-004.1", "CAT-008", "UC-01"], audited=True,
   desc="With axes (CAT-008) every variant has a value for exactly those axes; the panel builds the product of the values and "
        "leaves out the combinations not sold. A variant's unitPrice becomes its first price in the default list from now "
        "(catalog.price.manage store-wide; first prices are not a change, so no BR-041 approval). openingStock needs one variant "
        "and inventory.receipt.create at the warehouse.")
op("get", "/products/{productId}", "getProduct", "Catalog", "جزئیات کالا", resp="Product", reqs=["CAT-001"])
op("patch", "/products/{productId}", "updateProduct", "Catalog", "ویرایش کالا", perm="catalog.product.manage", scope="Tenant",
   body="ProductUpdate", resp="Product", reqs=["CAT-001"], audited=True, conflict=True)
op("post", "/products/{productId}/variants", "addVariant", "Catalog", "افزودن Variant", perm="catalog.product.manage",
   scope="Tenant", body="VariantInput", resp="Variant", resp_code="201", reqs=["CAT-008"], audited=True, conflict=True)
op("patch", "/products/{productId}/variants/{variantId}", "updateVariant", "Catalog", "فعال/غیرفعال کردن یک ترکیب",
   perm="catalog.product.manage", scope="Tenant", body="VariantUpdate", resp="Product", reqs=["CAT-008"], audited=True, conflict=True,
   desc="An inactive variant is not resolved from its barcode and cannot be priced; its stock and history stay.")
op("post", "/products/{productId}/variants/{variantId}/barcodes", "addBarcode", "Catalog", "افزودن بارکد به Variant",
   perm="catalog.product.manage", scope="Tenant", body="BarcodeInput", resp="Barcode", resp_code="201",
   reqs=["CAT-002", "CAT-002.1", "CAT-006"], audited=True, conflict=True)
op("delete", "/products/{productId}/variants/{variantId}/barcodes/{barcodeId}", "removeBarcode", "Catalog",
   "غیرفعال‌سازی بارکد (تاریخچه حفظ می‌شود)", perm="catalog.product.manage", scope="Tenant", no_content=True,
   reqs=["CAT-002"], audited=True)
op("post", "/products/{productId}/merge", "mergeProduct", "Catalog", "Merge کالای تکراری", perm="catalog.product.manage",
   scope="Tenant", idem=True, body="ProductMerge", resp="Product", reqs=["CAT-006", "AT-028"], audited=True, conflict=True,
   desc="The path product (the duplicate) is merged into targetProductId; the response is the target. Same base unit only "
        "(409 base-unit-mismatch); every duplicate variant needs a target variant with the same axis values (409 merge-variant-mismatch); "
        "the duplicate must hold no stock or reservation (409 merge-source-has-stock). Its active barcodes move to the target, its "
        "variants go inactive and it becomes Merged (never deleted).")
op("post", "/products/{productId}/catalog-suggestions", "suggestToGlobalCatalog", "Catalog",
   "پیشنهاد کالا به کاتالوگ عمومی (Opt-in؛ فقط فیلدهای مجاز)", perm="catalog.product.manage", scope="Tenant",
   body="CatalogSuggestionCreate", resp="CatalogSuggestionReceipt", resp_code="202", reqs=["CAT-005.1", "AT-027"], audited=True,
   desc="Sends only name, brand, category and the public (GS1, not restricted) barcodes for moderation; never price, cost, stock, "
        "supplier or the store's identity (a one-way fingerprint counts a store once). 422 no-public-barcode otherwise.")
op("get", "/price-lists", "listPriceLists", "Catalog", "فهرست قیمت‌ها", resp="PriceList", list_of=True, reqs=["CAT-009"])
op("get", "/price-lists/{priceListId}/prices", "listPriceVersions", "Catalog", "تاریخچه قیمت (یک Variant یا کل فهرست)",
   perm="catalog.price.manage", scope="Tenant", resp="PriceVersion", list_of=True, reqs=["CAT-009", "AB-06"],
   params=[{"name": "variantId", "in": "query", "required": False, "schema": {"type": "string", "format": "uuid"}}],
   desc="Newest first, up to 100 versions, of one variant or (without variantId) of the whole list: the review of "
        "recent price changes (AB-06). A version is never rewritten once it applied (RC023).")
op("post", "/price-lists/{priceListId}/prices", "setPrices", "Catalog", "تعیین قیمت (نسخه‌دار با validFrom)",
   perm="catalog.price.manage", scope="Tenant", idem=True, body="PriceSet", resp_code="204", no_content=True,
   reqs=["CAT-009", "BR-041"], audited=True, approval="bulk (> 20 variants) or > ±30% change (BR-041)",
   params=[{"name": "X-Approval-Id", "in": "header", "required": False, "schema": {"type": "string", "format": "uuid"}}],
   desc="validFrom: now up to a year ahead (an instant up to 15 minutes old is moved to now, so a retry after an approval still works); each price applies from validFrom "
        "until the next scheduled version; a scheduled version not yet started is corrected in place. Changing the "
        "default list needs the permission store-wide. Above the BR-041 thresholds the answer is 403 approval-required; "
        "the approval binds this exact change (list, validFrom, every price) and is consumed by the retry with X-Approval-Id.")
op("get", "/discount-rules", "listDiscountRules", "Catalog", "قواعد تخفیف", resp="DiscountRule", list_of=True, reqs=["CAT-010"],
   params=[{"name": "includeEnded", "in": "query", "required": False, "schema": {"type": "boolean"}}],
   desc="Rules in force or scheduled, newest first, up to 200; includeEnded=true adds the ended ones.")
op("post", "/discount-rules", "createDiscountRule", "Catalog", "قاعده تخفیف خودکار", perm="catalog.price.manage",
   scope="Branch", body="DiscountRuleCreate", resp="DiscountRule", resp_code="201", reqs=["CAT-010", "D-18"], audited=True,
   desc="Target kind Product (Category comes with the category tree). Without branchIds the rule is store-wide and needs "
        "the permission store-wide; with branchIds, in each branch. When several rules apply only the largest counts.")
op("delete", "/discount-rules/{ruleId}", "endDiscountRule", "Catalog", "پایان قاعده تخفیف (تاریخچه حفظ می‌شود)",
   perm="catalog.price.manage", scope="Branch", no_content=True, reqs=["CAT-010"], audited=True,
   desc="Ends the rule now; a rule never changes or disappears (RC025). An ended rule is 404.")
op("get", "/discount-caps", "getDiscountCaps", "Catalog", "سقف تخفیف دستی نقش‌ها", resp="DiscountCaps", reqs=["BR-021"])
op("put", "/discount-caps", "setDiscountCaps", "Catalog", "تعیین سقف تخفیف دستی نقش‌ها", perm="tenant.settings.manage", scope="Tenant",
   body="DiscountCaps", resp_code="204", no_content=True, reqs=["BR-021", "AB-01"], audited=True,
   desc="A manual discount above the highest cap among the person's roles at the register needs a manager's approval (applyDiscount).")
op("get", "/weighted-barcode-profiles", "getWeightedBarcodeProfiles", "Catalog", "Profileهای بارکد وزنی",
   resp="WeightedBarcodeProfileSet", reqs=["CAT-007"])
op("put", "/weighted-barcode-profiles", "setWeightedBarcodeProfiles", "Catalog", "تعیین Profileهای بارکد وزنی (کل مجموعه)",
   perm="tenant.settings.manage", scope="Tenant", body="WeightedBarcodeProfileSetUpdate", resp="WeightedBarcodeProfileSet",
   reqs=["CAT-007", "DI-084"], audited=True, conflict=True,
   desc="Replaces the whole set (0-10 profiles). Prefixes must not overlap (equal, or one starting another): 400 validation. "
        "A 13-digit code starting with a profile's prefix is always read as a weighted label.")

# ---------------- Inventory & procurement
tag("Inventory", "انبار، رسید، تعدیل، انتقال و انبارگردانی (INV)")
op("get", "/warehouses", "listWarehouses", "Inventory", "انبارها", resp="Warehouse", list_of=True, reqs=["TEN-002", "INV-001"])
op("post", "/warehouses", "createWarehouse", "Inventory", "ایجاد انبار", perm="tenant.settings.manage", scope="Tenant",
   body="WarehouseCreate", resp="Warehouse", resp_code="201", reqs=["TEN-002"], audited=True, conflict=True)
op("post", "/inventory/receipts", "postGoodsReceipt", "Inventory", "ثبت رسید خرید (موجودی + WAC + سند در یک تراکنش)",
   perm="inventory.receipt.create", scope="Warehouse", idem=True, body="GoodsReceiptCreate", resp="GoodsReceipt",
   resp_code="201", reqs=["PRC-002", "INV-005", "INV-006", "BR-042", "UC-05"], audited=True)
op("post", "/inventory/adjustments", "postAdjustment", "Inventory", "تعدیل موجودی با دلیل", perm="inventory.adjust",
   scope="Warehouse", idem=True, body="AdjustmentCreate", resp="Adjustment", resp_code="201",
   reqs=["INV-002", "BR-033"], audited=True, approval="value > tenant threshold (BR-033)",
   params=[{"name": "X-Approval-Id", "in": "header", "required": False, "schema": {"type": "string", "format": "uuid"}}])
op("get", "/inventory/receipts", "listGoodsReceipts", "Inventory", "رسیدهای خرید اخیر", perm="reports.view", resp="GoodsReceipt",
   list_of=True, reqs=["PRC-002"])
op("get", "/inventory/transfers", "listTransfers", "Inventory", "انتقال‌های اخیر", perm="reports.view", resp="Transfer", list_of=True, reqs=["INV-008"])
op("get", "/inventory/adjustments", "listAdjustments", "Inventory", "تعدیل‌ها و ارزش آن‌ها (کشف AB-05)", perm="reports.view",
   resp="Adjustment", list_of=True, reqs=["INV-002", "BR-033", "AB-05"])
op("post", "/inventory/transfers", "postTransfer", "Inventory", "انتقال بین انبارها (تک‌مرحله‌ای MVP)", perm="inventory.transfer",
   scope="Warehouse", idem=True, body="TransferCreate", resp="Transfer", resp_code="201", reqs=["INV-008"], audited=True)
op("post", "/inventory/counts", "createCount", "Inventory", "شروع انبارگردانی (Snapshot)", perm="inventory.count",
   scope="Warehouse", idem=True, body="CountCreate", resp="Count", resp_code="201", reqs=["INV-009", "UC-09"], audited=True)
op("get", "/inventory/counts", "listCounts", "Inventory", "انبارگردانی‌ها", perm="inventory.count", resp="Count", list_of=True, reqs=["INV-009"],
   params=[{"name": "warehouseId", "in": "query", "required": False, "schema": {"type": "string", "format": "uuid"}}])
op("get", "/inventory/counts/{countId}", "getCount", "Inventory", "یک انبارگردانی", perm="inventory.count", scope="Warehouse", resp="Count", reqs=["INV-009"])
op("post", "/inventory/counts/{countId}/close", "closeCount", "Inventory", "پایان شمارش (موجودی سیستمی زیر قفل خوانده می‌شود)",
   perm="inventory.count", scope="Warehouse", body="VersionOnly", resp="Count", reqs=["INV-009", "UC-09"], audited=True, conflict=True)
op("post", "/inventory/counts/{countId}/entries", "addCountEntries", "Inventory", "ثبت شمارش (چند شمارنده هم‌زمان)",
   perm="inventory.count", scope="Warehouse", body="CountEntryInput", no_content=True, reqs=["INV-009"], conflict=True)
op("get", "/inventory/counts/{countId}/result", "getCountResult", "Inventory", "اختلاف شمارش با جبران فروش حین شمارش",
   perm="inventory.count", scope="Warehouse", resp="CountResult", reqs=["INV-009", "AT-280"])
op("post", "/inventory/counts/{countId}/confirm", "confirmCount", "Inventory", "تأیید و ثبت تعدیل انبارگردانی",
   perm="inventory.adjust", scope="Warehouse", idem=True, body="VersionOnly", resp="CountConfirmation",
   reqs=["INV-009", "SoD-03", "BR-033"], audited=True, approval="value > threshold or counter == confirmer (SoD-03)")
op("get", "/inventory/balances", "listStockBalances", "Inventory", "موجودی (ارزش فقط با مجوز گزارش)",
   params=[{"name": "warehouseId", "in": "query", "required": False, "schema": {"type": "string", "format": "uuid"}},
           {"name": "variantId", "in": "query", "required": False, "schema": {"type": "string", "format": "uuid"}},
           {"name": "belowReorderPoint", "in": "query", "required": False, "schema": {"type": "boolean"}}],
   resp="StockBalance", list_of=True, page=True, reqs=["INV-001", "INV-010", "RPT-001.1"],
   desc="wac and value only with reports.view for the warehouse's branch (TM-014), otherwise null. "
        "belowReorderPoint arrives with US-066 (the filter is refused until then). Up to 500 rows.")
op("get", "/inventory/movements", "listStockMovements", "Inventory", "کاردکس کالا", perm="reports.view", scope="Warehouse",
   params=[{"name": "variantId", "in": "query", "required": True, "schema": {"type": "string", "format": "uuid"}},
           {"name": "warehouseId", "in": "query", "required": False, "schema": {"type": "string", "format": "uuid"}},
           {"$ref": "#/components/parameters/From"}, {"$ref": "#/components/parameters/To"}],
   resp="StockMovement", list_of=True, page=True, reqs=["INV-001", "RPT-001.1"],
   desc="Newest first, up to 200; from/to on effectiveAt. Without warehouseId the permission is needed store-wide.")
op("put", "/inventory/reorder-points", "setReorderPoint", "Inventory", "نقطه سفارش یک کالا در یک انبار (null: حذف)",
   perm="inventory.receipt.create", scope="Warehouse", body="ReorderPointSet", no_content=True, reqs=["INV-010"], audited=True,
   desc="A posting that brings an item from above its reorder point to at or below it emits inventory.stock-below-reorder v1 (DE-INV-002).")
op("put", "/inventory/negative-stock-policies", "setNegativeStockPolicy", "Inventory", "Policy موجودی منفی (فروشگاه، شعبه یا کالا)",
   perm="tenant.settings.manage", scope="Tenant", body="NegativeStockPolicySet", resp_code="204", no_content=True,
   reqs=["BR-040", "INV-003"], audited=True,
   desc="The most specific level wins (Variant, then Branch, then Tenant); the default is Forbid. Under Forbid a document "
        "that would make available stock negative is refused with 409 insufficient-stock listing every shortage.")
tag("Procurement", "تأمین‌کننده، پرداخت و برگشت از خرید (PRC)")
op("get", "/suppliers", "listSuppliers", "Procurement", "تأمین‌کنندگان", resp="Supplier", list_of=True, page=True, reqs=["PRC-001"],
   params=[{"name": "q", "in": "query", "required": False, "schema": {"type": "string"}}])
op("post", "/suppliers", "createSupplier", "Procurement", "ایجاد تأمین‌کننده", perm="inventory.receipt.create", scope="Tenant",
   body="SupplierCreate", resp="Supplier", resp_code="201", reqs=["PRC-001"], audited=True)
op("post", "/suppliers/{supplierId}/payments", "paySupplier", "Procurement", "پرداخت به تأمین‌کننده",
   perm="accounting.journal.post", scope="Tenant", idem=True, body="SupplierPaymentCreate", resp="SupplierPayment", resp_code="201",
   reqs=["PRC-003"], audited=True)
op("post", "/purchase-returns", "postPurchaseReturn", "Procurement", "برگشت از خرید (خروج با WAC + سند در یک تراکنش)",
   perm="inventory.receipt.create", scope="Warehouse", idem=True, body="PurchaseReturnCreate", resp="PurchaseReturn", resp_code="201",
   reqs=["PRC-004", "INV-006"], audited=True)
op("get", "/purchase-returns", "listPurchaseReturns", "Procurement", "برگشت‌های از خرید اخیر", perm="reports.view", resp="PurchaseReturn",
   list_of=True, reqs=["PRC-004"])

# ---------------- POS & sales
tag("POS", "شیفت و سبد فروش (SAL)")
op("post", "/shifts", "openShift", "POS", "باز کردن شیفت", perm="shift.manage", scope="Register", idem=True, body="ShiftOpen",
   resp="Shift", resp_code="201", reqs=["SAL-009", "DI-008", "UC-06"], audited=True)
op("get", "/shifts/{shiftId}", "getShift", "POS", "شیفت (نقد مورد انتظار در Blind Close پنهان)", perm="shift.manage",
   scope="OwnShift", resp="Shift", reqs=["SAL-009", "D-24"])
op("post", "/shifts/{shiftId}/cash-movements", "recordCashMovement", "POS", "ورود/خروج نقد با دلیل", perm="shift.manage",
   scope="OwnShift", idem=True, body="CashMovementCreate", resp_code="201", reqs=["UC-06"], audited=True)
op("post", "/shifts/{shiftId}/close", "closeShift", "POS", "شمارش و بستن شیفت", perm="shift.manage", scope="OwnShift",
   idem=True, body="ShiftClose", resp="Shift", reqs=["SAL-009", "UC-06", "AB-04"], audited=True,
   approval="|variance| > threshold → shift.supervise (UC-06)")
op("post", "/shifts/{shiftId}/variance-approval", "approveShiftVariance", "POS", "تأیید مغایرت توسط سرپرست",
   perm="shift.supervise", scope="Branch", body="ApprovalDecision", resp="Shift", reqs=["UC-06", "SoD-01"], audited=True, conflict=True)
op("get", "/pos/context", "getPosContext", "POS", "دستگاه فعلی: شعبه، صندوق‌ها و شیفت جاری هر صندوق", perm="pos.sell",
   resp="PosContext", reqs=["SAL-010", "BR-050", "ABAC-04"],
   desc="Only on a registered device (403 terminal-not-registered otherwise).")
op("post", "/pos/sales", "createSale", "POS", "ایجاد سبد (Idempotent با clientDraftId برای Sync آفلاین)", perm="pos.sell",
   scope="Register", body="SaleCreate", resp="Sale", resp_code="201", reqs=["SAL-001", "SAL-013", "DI-010", "ABAC-04"],
   conflict=True, desc="Repeating the same clientDraftId returns the existing sale (200) instead of creating a new one; offline lines "
        "sent with it are not added again. A line the store cannot add is reported in rejectedLines, not fatal.")
op("get", "/pos/sales/{saleId}", "getSale", "POS", "سبد یا فاکتور", perm="pos.sell", scope="Register", resp="Sale", reqs=["SAL-001"])
op("post", "/pos/sales/{saleId}/lines", "addSaleLine", "POS", "افزودن قلم با اسکن یا انتخاب (قیمت پیش‌نمایش سمت سرور)",
   perm="pos.sell", scope="OwnShift", body="SaleLineInput", resp="Sale", reqs=["SAL-001", "SAL-005.1", "CAT-007", "BR-018"],
   conflict=True, examples={"scan": ex("اسکن", {"barcode": "6260000000017", "qty": "2"})})
op("patch", "/pos/sales/{saleId}/lines/{lineNo}", "updateSaleLine", "POS", "تغییر مقدار یا واحد", perm="pos.sell",
   scope="OwnShift", body="SaleLinePatch", resp="Sale", reqs=["SAL-001"], conflict=True)
op("delete", "/pos/sales/{saleId}/lines/{lineNo}", "removeSaleLine", "POS", "حذف قلم (ثبت برای کشف تقلب D-44)",
   perm="pos.sell", scope="OwnShift", params=[{"name": "expectedVersion", "in": "query", "required": True, "schema": {"type": "integer"}}],
   resp="Sale", reqs=["SAL-001", "AB-03"], conflict=True, audited=True)
op("post", "/pos/sales/{saleId}/discounts", "applyDiscount", "POS", "تخفیف دستی خطی یا سندی", perm="pos.discount.apply",
   scope="Register", body="SaleDiscountInput", resp="Sale", reqs=["CAT-010", "BR-021", "AB-01"], audited=True, conflict=True,
   approval="above role cap (BR-021)")
op("post", "/pos/sales/{saleId}/customer", "attachCustomer", "POS", "اتصال مشتری (QR / کد عمومی عضو / عضو انتخاب‌شده)",
   perm="customers.lookup", scope="Branch", body="AttachCustomer", resp="Sale", reqs=["SAL-003", "CUS-006", "BR-061", "TAX-004"],
   conflict=True, audited=True, rate="customer-lookup")
op("get", "/pos/held-sales", "listHeldSales", "POS", "سبدهای نگه‌داشته‌شده شعبه این دستگاه", perm="pos.sell", resp="HeldSale", list_of=True,
   reqs=["SAL-011"])
op("post", "/pos/sales/{saleId}/hold", "holdSale", "POS", "نگه‌داشتن سبد", perm="pos.sell", scope="OwnShift", body="HoldRequest",
   resp="Sale", reqs=["SAL-011"], conflict=True)
op("post", "/pos/sales/{saleId}/resume", "resumeSale", "POS", "ادامه سبد نگه‌داشته‌شده (همان شعبه)", perm="pos.sell",
   scope="Register", body="ResumeRequest", resp="Sale", reqs=["SAL-011"], conflict=True)
op("post", "/pos/sales/{saleId}/cancel", "cancelSale", "POS", "لغو سبد بدون پرداخت تأییدشده", perm="pos.sale.cancel",
   scope="OwnShift", body="CancelRequest", resp="Sale", reqs=["SAL-004", "AB-03"], audited=True, conflict=True)
op("post", "/pos/sales/{saleId}/tenders", "recordTender", "POS", "ثبت پرداخت نقد، کارتخوان مستقل، نسیه یا مانده بستانکار",
   perm="pos.sell", scope="OwnShift", idem=True, body="TenderInput", resp="Sale",
   reqs=["SAL-006", "SAL-006.1", "PAY-002", "PAY-002.1", "CRD-002"], audited=True,
   desc="CustomerCredit tender is only provisionally recorded; the credit limit is checked and charged inside the "
        "Finalize transaction (BR-082, D-28). sales.credit.sell is required for CustomerCredit.",
   examples={"cash": ex("نقد", {"method": "Cash", "amount": "1100000", "tendered": "1200000"}),
             "card": ex("کارتخوان مستقل", {"method": "ExternalCardTerminal", "amount": "1100000", "referenceNo": "123456789012",
                                           "terminalNo": "8899", "cardLast4": "1234"}),
             "credit": ex("نسیه", {"method": "CustomerCredit", "amount": "1100000"})})

op("post", "/pos/sales/{saleId}/tenders/{seq}/void", "voidTender", "POS", "باطل کردن پرداخت پیش از نهایی‌سازی",
   perm="pos.sale.cancel", scope="OwnShift", idem=True, body="TenderVoid", resp="Sale", reqs=["SAL-006", "AB-03"], audited=True, conflict=True,
   desc="The tender is kept, marked Voided with its reason; it no longer counts toward the bill, the drawer or the entry. A card "
        "slip's reference stays taken (AB-14). When no payment is left the lines can change again.")
tag("Sales", "نهایی‌سازی، فاکتور، برگشت و ارسال فاکتور (SAL)")
op("post", "/sales/{saleId}/finalize", "finalizeSale", "Sales",
   "نهایی‌سازی اتمی: شماره فاکتور، گردش انبار، سند، نسیه و Outbox در یک تراکنش", perm="pos.sell", scope="OwnShift",
   idem=True, body="FinalizeRequest", resp="Sale", reqs=["SAL-007", "SAL-005", "BR-013", "BR-056", "BR-082", "INV-004", "UC-02"],
   audited=True,
   desc="Server recomputes prices/taxes and compares with expectedTotal (409 price-changed). Replays with the same "
        "Idempotency-Key return the original success response (P0-3). Business errors are not stored and may be "
        "retried with the same key (D-34). Receipt delivery and tax submission happen after commit and never roll the sale back (BR-071).",
   examples={"finalize": ex("نمونه سند مرجع §15.3", {"expectedVersion": 3, "expectedTotal": "1100000"})},
   resp_example={"finalized": ex("فاکتور نهایی", {
       "id": "6bc3c5a2-6a33-4e0b-aaf9-a27e3c0a1151", "status": "Finalized", "returnStatus": "None",
       "branchId": "a0000000-0000-0000-0000-000000000001", "registerId": "a0000000-0000-0000-0000-000000000002",
       "shiftId": "a0000000-0000-0000-0000-000000000030", "invoiceNo": "B1-1405-000124", "customer": None,
       "lines": [{"lineNo": 1, "variantId": "a0000000-0000-0000-0000-000000000011", "name": "شیر کم‌چرب ۱ لیتری",
                  "sku": "MILK-1L", "qty": "2", "unit": "EA", "unitPrice": "500000", "discounts": [], "net": "1000000",
                  "taxRate": 1000, "tax": "100000", "lineTotal": "1100000", "returnedQty": "0"}],
       "saleDiscount": None,
       "tenders": [{"seq": 1, "method": "Cash", "amount": "1100000", "status": "Confirmed",
                    "paymentId": "d0f430e2-98c5-4fa6-86e8-e0107125beab", "reference": None}],
       "totals": {"gross": "1000000", "discount": "0", "net": "1000000", "tax": "100000", "total": "1100000",
                  "paid": "1100000", "remaining": "0", "change": "100000"},
       "finalizedAt": "2026-11-22T10:15:30Z", "receiptUrl": "https://example.invalid/r/7Qe…", "version": 4,
       "warnings": []})})
op("get", "/sales", "listSales", "Sales", "جستجوی فاکتورها (شماره، تاریخ، صندوق، مشتری عضو)", perm="pos.sell", scope="Branch",
   params=[{"name": "invoiceNo", "in": "query", "required": False, "schema": {"type": "string"}},
           {"name": "tenantCustomerId", "in": "query", "required": False, "schema": {"type": "string", "format": "uuid"}},
           {"$ref": "#/components/parameters/BranchFilter"}, {"$ref": "#/components/parameters/From"},
           {"$ref": "#/components/parameters/To"}],
   resp="Sale", list_of=True, page=True, reqs=["SAL-008", "CUS-006"])
op("post", "/sales/{saleId}/returns", "createReturn", "Sales", "برگشت کامل یا جزئی و بازپرداخت", perm="sales.return.create",
   scope="Branch", idem=True, body="ReturnCreate", resp="SaleReturn", resp_code="201",
   reqs=["SAL-008", "BR-052", "BR-053", "BR-054", "BR-055", "BR-085", "TAX-006", "UC-04", "DQ-04"], audited=True,
   approval="after return window, above role cap, or cash refund for card/online payment (BR-052/055)",
   params=[{"name": "X-Approval-Id", "in": "header", "required": False, "schema": {"type": "string", "format": "uuid"}}])
op("get", "/sales/{saleId}/returns", "listReturns", "Sales", "برگشت‌های یک فاکتور", perm="sales.return.create", scope="Branch",
   resp="SaleReturn", list_of=True, reqs=["SAL-008"])
op("post", "/sales/{saleId}/print", "printReceipt", "Sales", "چاپ فاکتور (۸۰mm یا A4) با QR فاکتور دیجیتال",
   perm="pos.sell", scope="Register", body="PrintRequest", resp="PrintedReceipt", conflict=True, reqs=["SAL-012", "BR-086", "CUS-009"],
   audited=True, desc="Only a Finalized sale (409 sale-not-finalized). Each call counts a print (the first is the original) and issues a fresh "
                      "receipt link for the QR; the template version and the store's parts are those snapshotted at Finalize.")
op("delete", "/sales/{saleId}/receipt-links", "revokeReceiptLinks", "Sales", "ابطال همه لینک‌های فاکتور", perm="pos.sell",
   scope="Register", resp="RevokedCount", reqs=["CUS-009"], audited=True)
op("post", "/sales/{saleId}/receipt-deliveries", "deliverReceipt", "Sales", "ارسال یا ارسال مجدد فاکتور دیجیتال",
   perm="pos.sell", scope="Branch", idem=True, body="DeliveryRequest", resp="Notification", resp_code="202",
   reqs=["NOT-002", "NOT-008", "CUS-011"], rate="receipt-delivery")
op("get", "/sales/{saleId}/notifications", "listSaleNotifications", "Sales", "وضعیت ارسال فاکتور", perm="pos.sell",
   scope="Branch", resp="Notification", list_of=True, reqs=["NOT-006"])

# ---------------- Payments
tag("Payments", "درگاه آنلاین، Callback، بازپرداخت و تسویه (PAY)")
op("post", "/payments/intents", "createPaymentIntent", "Payments", "شروع پرداخت آنلاین (Reservation ۱۵ دقیقه‌ای)",
   perm="pos.sell", scope="OwnShift", idem=True, body="PaymentIntentCreate", resp="PaymentIntent", resp_code="201",
   reqs=["PAY-003", "INV-004.1", "UC-08"], audited=True)
op("get", "/payments/intents/{intentId}", "getPaymentIntent", "Payments", "وضعیت پرداخت (Polling)", perm="pos.sell",
   scope="Register", resp="PaymentIntent", reqs=["PAY-003"])
op("post", "/callbacks/psp/{gateway}/{intentId}", "pspCallback", "Payments", "Callback درگاه (عمومی؛ فقط Verify اعتبار دارد)",
   aud="public", resp_code="200", reqs=["PAY-003.1", "PAY-003.2", "PAY-007", "TM-030"], rate="callback",
   desc="Accepts the gateway's native form/JSON body (max 64KB). Stored in Inbox; server-to-server Verify decides the "
        "outcome. Return URL data is never trusted. Always responds quickly with a redirect/HTML for the customer.")
PATHS["/callbacks/psp/{gateway}/{intentId}"]["post"]["requestBody"] = {
    "required": True, "content": {"application/x-www-form-urlencoded": {"schema": {"type": "object", "additionalProperties": True}},
                                  "application/json": {"schema": {"type": "object", "additionalProperties": True}}}}
op("get", "/payment-gateways", "listGatewayConfigs", "Payments", "پیکربندی درگاه‌ها (اعتبارنامه Masked)",
   perm="integrations.manage", scope="Tenant", resp="GatewayConfig", list_of=True, reqs=["PAY-004", "PAY-005"])
op("post", "/payment-gateways", "createGatewayConfig", "Payments", "افزودن درگاه (اعتبارنامه در OpenBao)",
   perm="integrations.manage", scope="Tenant", body="GatewayConfigInput", resp="GatewayConfig", resp_code="201",
   reqs=["PAY-005", "ADR-012"], audited=True)
op("post", "/refunds/{refundId}/manual-completion", "completeRefundManually", "Payments", "ثبت مرجع بازپرداخت دستی",
   perm="payments.refund", scope="Branch", idem=True, body="RefundManualCompletion", resp="Refund", reqs=["PAY-006"], audited=True)
op("post", "/settlements/imports", "importSettlement", "Payments", "Import فایل تسویه کارتخوان و تطبیق با پرداخت‌ها",
   perm="accounting.journal.post", scope="Tenant", idem=True, body="SettlementImportCreate", resp="SettlementImport", resp_code="201",
   reqs=["PAY-002.2", "AB-14", "AT-083"], audited=True, conflict=True,
   desc="Synchronous (MVP, US-105): each line settles the one open ConfirmedManual payment with its reference (same terminal when both "
        "name one, business date within a day) and amount, on a finalized sale; every other line is kept with why. The matched gross "
        "is posted in one entry (D08 J4: Bank and BankFees against CardInTransit). A file with any unreadable row imports nothing "
        "(400 invalid-settlement-file, context.rows); the same file again is 409 already-imported (context.importId). "
        "Online-gateway settlement follows with UC-08.")
op("get", "/settlements/imports", "listSettlementImports", "Payments", "فایل‌های تسویه اخیر", perm="accounting.read", scope="Tenant",
   resp="SettlementImport", list_of=True, reqs=["PAY-002.2"], desc="The 50 most recent imports, newest first, without their lines.")
op("get", "/settlements/imports/{importId}", "getSettlementImport", "Payments", "یک فایل تسویه با سطرهایش", perm="accounting.read",
   scope="Tenant", resp="SettlementImport", reqs=["PAY-002.2"])
op("get", "/settlements/unreconciled-payments", "listUnreconciledPayments", "Payments", "پرداخت دستی بدون تطبیق (به تفکیک صندوق‌دار)",
   perm="accounting.read", scope="Tenant", resp="UnreconciledReport", reqs=["PAY-002.2", "AB-14"],
   params=[{"name": "from", "in": "query", "required": False, "schema": {"type": "string", "format": "date"},
            "description": "Store-local business date; default 30 days before `to`"},
           {"name": "to", "in": "query", "required": False, "schema": {"type": "string", "format": "date"},
            "description": "Default the store's today; at most 92 days after `from`"}],
   desc="Card-terminal payments of finalized sales that no settlement file has matched, oldest first, with totals per cashier.")

# ---------------- Accounting
tag("Accounting", "شجره حساب، سند، دوره و گزارش مالی (ACC)")
op("get", "/accounting/accounts", "listAccounts", "Accounting", "شجره حساب", perm="accounting.read", scope="Tenant",
   resp="Account", list_of=True, reqs=["ACC-001"])
op("post", "/accounting/accounts", "createAccount", "Accounting", "ایجاد حساب", perm="accounting.journal.post", scope="Tenant",
   body="AccountCreate", resp="Account", resp_code="201", reqs=["ACC-001"], audited=True, conflict=True)
op("put", "/accounting/account-mappings", "setAccountMappings", "Accounting", "نگاشت نقش حساب به حساب Tenant",
   perm="accounting.journal.post", scope="Tenant", body="AccountMappingSet", no_content=True, reqs=["ACC-004", "D-30"], audited=True)
op("get", "/accounting/journals", "listJournals", "Accounting", "دفتر روزنامه", perm="accounting.read", scope="Tenant",
   params=[{"$ref": "#/components/parameters/From"}, {"$ref": "#/components/parameters/To"},
           {"name": "sourceType", "in": "query", "required": False, "schema": {"type": "string"}}],
   resp="Journal", list_of=True, page=True, reqs=["ACC-007"])
op("post", "/accounting/journals", "createJournalDraft", "Accounting", "سند دستی (Draft)", perm="accounting.journal.post",
   scope="Tenant", idem=True, body="JournalCreate", resp="Journal", resp_code="201", reqs=["ACC-006"], audited=True)
op("post", "/accounting/journals/{journalId}/post", "postJournal", "Accounting", "ثبت قطعی سند (تراز در Commit)",
   perm="accounting.journal.post", scope="Tenant", idem=True, resp="Journal", conflict=True,
   reqs=["ACC-002", "ACC-006", "DI-040", "DI-042"], audited=True,
   desc="No body. Idempotent by key; without the key's replay an entry still goes Draft -> Posted only once (409 journal-not-draft). "
        "Entries carry no version.")
op("delete", "/accounting/journals/{journalId}", "deleteJournalDraft", "Accounting", "حذف سند دستی Draft", perm="accounting.journal.post",
   scope="Tenant", no_content=True, conflict=True, reqs=["ACC-006"], audited=True,
   desc="Only a manual Draft is deleted, with its lines (D02 §6.3); a posted entry is corrected by reversal (409 journal-not-draft).")
op("post", "/accounting/journals/{journalId}/reverse", "reverseJournal", "Accounting", "سند معکوس", perm="accounting.journal.post",
   scope="Tenant", idem=True, body="JournalReverse", resp="Journal", resp_code="201", reqs=["ACC-003", "ACC-009", "DI-045"], audited=True,
   desc="The mirror entry is dated now in the open period (ACC-009); an entry is reversed once (409 journal-already-reversed).")
op("get", "/accounting/periods", "listPeriods", "Accounting", "دوره‌های مالی", perm="accounting.read", scope="Tenant",
   resp="Period", list_of=True, reqs=["ACC-001.1"])
op("post", "/accounting/periods/{periodId}/close", "closePeriod", "Accounting", "بستن دوره (بازگشایی در MVP ممکن نیست)",
   perm="accounting.period.close", scope="Tenant", idem=True, body="VersionOnly", resp="Period", conflict=True,
   reqs=["ACC-009", "D-45", "AB-19"], audited=True,
   desc="Only a period that has ended, after every earlier one, with no draft dated in it (409 period-not-ended, "
        "earlier-period-open, period-has-drafts with context.drafts; period-closed if already closed). Never reopened in the MVP. "
        "Emits accounting.period-closed v1.")
REPORT_RANGE = [{"$ref": "#/components/parameters/From"}, {"$ref": "#/components/parameters/To"}]
op("get", "/accounting/reports/trial-balance", "getTrialBalance", "Accounting", "تراز آزمایشی (۲ و ۴ ستونی)", perm="accounting.read",
   scope="Tenant", params=REPORT_RANGE + [{"name": "includeEmpty", "in": "query", "required": False, "schema": {"type": "boolean"},
                                          "description": "Also list accounts with no balance and no movement"}],
   resp="TrialBalance", reqs=["ACC-007", "AT-107"],
   desc="Posted entries only, over [from, to). Without `from`, the fiscal year that holds `to` (default now) so far; at most 800 days. "
        "Groups sum the accounts under them, in chart order.")
op("get", "/accounting/reports/ledger/{accountId}", "getLedger", "Accounting", "دفتر کل، معین و گردش تفصیلی",
   perm="accounting.read", scope="Tenant",
   params=[{"name": "detailId", "in": "query", "required": False, "schema": {"type": "string", "format": "uuid"},
            "description": "Only the lines of this detail (a customer, supplier or branch): گردش حساب تفصیلی"}] + REPORT_RANGE
          + [{"$ref": "#/components/parameters/Cursor"},
             {"name": "limit", "in": "query", "required": False, "schema": {"type": "integer", "minimum": 1, "maximum": 500, "default": 100}}],
   resp="Ledger", reqs=["ACC-007", "AT-107"],
   desc="The posted lines of the account, or of every account under a group (دفتر کل), oldest first, with the opening before "
        "`from` and the running balance. Range as getTrialBalance. Limit 1-500 (default 100).")

# ---------------- Receivables
tag("Receivables", "نسیه، دریافت، سنی مطالبات (CRD)")
op("post", "/credit-accounts", "createCreditAccount", "Receivables", "ایجاد حساب اعتباری برای مشتری عضو",
   perm="credit.account.manage", scope="Tenant", idem=True, body="CreditAccountCreate", resp="CreditAccount", resp_code="201",
   reqs=["CRD-001", "BR-080", "BR-081", "AB-07"], audited=True)
op("get", "/credit-accounts", "listCreditAccounts", "Receivables", "حساب‌های اعتباری", perm="credit.account.manage",
   scope="Tenant", resp="CreditAccount", list_of=True, page=True, reqs=["CRD-001"])
op("get", "/credit-accounts/{creditAccountId}", "getCreditAccount", "Receivables", "حساب اعتباری", perm="credit.account.manage",
   scope="Tenant", resp="CreditAccount", reqs=["CRD-001"])
op("patch", "/credit-accounts/{creditAccountId}", "updateCreditAccount", "Receivables", "تغییر سقف، مهلت یا وضعیت",
   perm="credit.account.manage", scope="Tenant", body="CreditAccountUpdate", resp="CreditAccount",
   reqs=["CRD-001", "CRD-008", "BR-081"], audited=True, conflict=True, approval="limit increase above threshold (BR-081)")
op("get", "/credit-accounts/{creditAccountId}/statement", "getStatement", "Receivables", "صورت‌حساب مشتری",
   perm="receivables.collect", scope="Branch", params=[{"$ref": "#/components/parameters/From"}, {"$ref": "#/components/parameters/To"}],
   resp="StatementLine", list_of=True, reqs=["CRD-006"])
op("post", "/receivables/collections", "recordCollection", "Receivables", "دریافت از مشتری و تخصیص (FIFO پیش‌فرض)",
   perm="receivables.collect", scope="Branch", idem=True, body="CollectionCreate", resp="Collection", resp_code="201",
   reqs=["CRD-004", "BR-084", "UC-11"], audited=True,
   examples={"fifo": ex("تخصیص FIFO", {"creditAccountId": "a0000000-0000-0000-0000-000000000401", "amount": "3000000",
                                       "method": "Cash", "shiftId": "a0000000-0000-0000-0000-000000000030"})})
op("post", "/receivables/write-offs", "writeOff", "Receivables", "سوخت مطالبات", perm="receivables.writeoff", scope="Tenant",
   idem=True, body="WriteOffCreate", resp_code="201", reqs=["CRD-010", "SoD-02"], audited=True, approval="always")
op("get", "/receivables/aging", "getAging", "Receivables", "سنی مطالبات", perm="accounting.read", scope="Tenant",
   resp="AgingRow", list_of=True, page=True, reqs=["CRD-006"])

# ---------------- Customers (merchant)
tag("Customers", "شناسایی مشتری در فروشگاه، عضویت و QR پیشخوان (CUS)")
op("post", "/customers/identify", "identifyCustomer", "Customers", "شناسایی با QR مشتری یا کد عمومی (فقط همین Tenant، Masked)",
   perm="customers.lookup", scope="Branch", body="IdentifyRequest", resp="IdentifyResponse",
   reqs=["SAL-003", "CUS-004", "CUS-005", "BR-061", "AB-09", "AB-10"], rate="customer-lookup", audited=True,
   examples={"qr": ex("QR مشتری", {"qrToken": "eyJhbGciOiJFUzI1NiIsImtpZCI6InFyLWN1c3QtMSJ9…"}),
             "code": ex("کد عمومی", {"publicCode": "CU-7P4K9X2Q"})},
   resp_example={"notMember": ex("عضو نیست", {"result": "not_member"})})
op("post", "/customers/otp-challenges", "startCustomerOtp", "Customers", "ارسال OTP به مشتری برای اتصال عضویت یا فاکتور Guest",
   perm="customers.lookup", scope="Branch", body="CustomerOtpChallengeCreate", resp="OtpChallenge", resp_code="201",
   reqs=["CUS-001", "CUS-004", "CUS-011", "TM-043"], rate="otp", audited=True)
op("post", "/customer-memberships/link", "linkMembership", "Customers", "ایجاد عضویت با OTP اعلام‌شده توسط مشتری",
   perm="customers.lookup", scope="Branch", idem=True, body="MembershipLinkRequest", resp="TenantCustomer", resp_code="201",
   reqs=["CUS-003", "CUS-004", "CUS-007"], audited=True)
op("get", "/customers/{tenantCustomerId}", "getTenantCustomer", "Customers", "مشتری عضو (شماره کامل فقط با customers.pii.view)",
   perm="customers.lookup", scope="Branch", resp="TenantCustomer", reqs=["BR-062", "CUS-006"], audited=True)
op("post", "/store-invites", "createStoreInvite", "Customers", "صدور QR پیشخوان امضاشده", perm="tenant.settings.manage",
   scope="Branch", body="StoreInviteCreate", resp="StoreInvite", resp_code="201", reqs=["CUS-005", "AB-11"], audited=True)

# ---------------- Customer portal
tag("Portal", "پورتال مشتری (Audience: customer-api)")
op("post", "/c/auth/otp", "portalStartOtp", "Portal", "شروع ورود با OTP", aud="public", body="PortalOtpStart", resp="OtpChallenge",
   resp_code="201", reqs=["CUS-001", "TM-043"], rate="otp")
op("post", "/c/auth/otp/verify", "portalVerifyOtp", "Portal", "تأیید OTP و ایجاد Session (BFF Cookie)", aud="public",
   body="PortalOtpVerify", resp="PortalSession", reqs=["CUS-001", "CUS-002"], rate="otp")
op("get", "/c/me", "portalGetMe", "Portal", "کد عمومی و وضعیت کانال‌ها", aud="customer", resp="CustomerMe", reqs=["CUS-010"])
op("post", "/c/me/public-code/rotate", "portalRotatePublicCode", "Portal", "باطل و تعویض کد عمومی", aud="customer",
   resp="CustomerMe", reqs=["CUS-002.1"], audited=True)
op("post", "/c/me/qr", "portalIssueQr", "Portal", "صدور QR مشتری (۱۲۰ ثانیه، یک‌بارمصرف)", aud="customer", resp="CustomerQr",
   resp_code="201", reqs=["CUS-005"], rate="qr-issue")
op("get", "/c/join/{inviteToken}", "portalGetInvite", "Portal", "نمایش فروشگاه QR پیشخوان (فقط نام و دامنه)", aud="public",
   resp_code="200", reqs=["CUS-005", "AB-11"])
op("post", "/c/join/{inviteToken}", "portalJoinStore", "Portal", "عضویت در فروشگاه با رضایت‌ها", aud="customer",
   body="JoinRequest", resp="Membership", resp_code="201", reqs=["CUS-003", "CUS-007", "UC-03"], audited=True, conflict=True)
op("post", "/c/join-requests/{joinRequestId}/accept", "portalAcceptJoinRequest", "Portal",
   "پذیرش درخواست عضویت ارسال‌شده از POS (پس از اسکن QR)", aud="customer", body="JoinRequest", resp="Membership",
   resp_code="201", reqs=["UC-03"], audited=True)
op("get", "/c/memberships", "portalListMemberships", "Portal", "فروشگاه‌های من", aud="customer", resp="Membership", list_of=True,
   reqs=["CUS-010"])
op("delete", "/c/memberships/{membershipId}", "portalLeaveStore", "Portal", "لغو عضویت", aud="customer", no_content=True,
   reqs=["CUS-010"], audited=True)
op("put", "/c/memberships/{membershipId}/consents", "portalUpdateConsents", "Portal", "تغییر رضایت‌ها (اثر فوری روی صف ارسال)",
   aud="customer", body="ConsentUpdate", resp="Membership", reqs=["CUS-007", "DI-065"], audited=True)
op("get", "/c/memberships/{membershipId}/access-history", "portalAccessHistory", "Portal", "تاریخچه دسترسی فروشگاه به پروفایل",
   aud="customer", resp="StoreAccessEvent", list_of=True, page=True, reqs=["D-47"])
op("get", "/c/receipts", "portalListReceipts", "Portal", "فاکتورهای من (فقط Membershipهای خودم)", aud="customer",
   resp="CustomerReceiptSummary", list_of=True, page=True, reqs=["CUS-010"])
op("get", "/c/memberships/{membershipId}/credit", "portalGetCredit", "Portal", "مانده و سررسید نسیه همین فروشگاه",
   aud="customer", resp="CustomerCreditSummary", reqs=["CRD-009", "AT-313"])
op("post", "/c/payments/intents", "portalPayBalance", "Portal", "پرداخت آنلاین مانده نسیه", aud="customer", idem=True,
   body="PortalPaymentCreate", resp="PaymentIntent", resp_code="201", reqs=["CRD-005"], audited=True)
op("post", "/c/bale/link-token", "portalBaleLinkToken", "Portal", "Deep Link اتصال بله (۱۰ دقیقه، یک‌بارمصرف)",
   aud="customer", resp="BaleLinkToken", resp_code="201", reqs=["NOT-003", "TM-032"])
op("post", "/c/data-export", "portalExportMyData", "Portal", "Export داده من", aud="customer", resp_code="202", reqs=["NFR-PRIV-003"], audited=True)
op("post", "/c/account/deletion", "portalRequestDeletion", "Portal", "درخواست حذف حساب (ناشناس‌سازی پس از ۳۰ روز)",
   aud="customer", resp_code="202", reqs=["NFR-PRIV-003", "BR-087"], audited=True)

# ---------------- Public receipt & webhooks
tag("Public", "لینک فاکتور و Webhookهای بیرونی")
op("get", "/r/{token}", "getPublicReceipt", "Public", "نمایش فاکتور با Receipt Link (بدون PII مشتری)", aud="public",
   resp="ReceiptPublic", reqs=["CUS-009", "NOT-002", "AB-12"], rate="receipt-link",
   desc="No customer data. Unknown, expired and revoked links are the same 404; each view is audited in the store; 30 a minute per address.")
op("post", "/integrations/bale/webhook/{secretPath}", "baleWebhook", "Public", "Webhook بات بله (Verification Required)",
   aud="public", resp_code="200", reqs=["NOT-003", "TM-032", "ADR-008"], rate="callback")
PATHS["/integrations/bale/webhook/{secretPath}"]["post"]["requestBody"] = {
    "required": True, "content": {"application/json": {"schema": {"type": "object", "additionalProperties": True}}}}

# ---------------- Tax
tag("Tax", "پروفایل مالیاتی، کلید، صورتحساب و داشبورد مؤدیان (TAX)")
op("get", "/tax/profile", "getTaxProfile", "Tax", "پروفایل مالیاتی", perm="tax.manage", scope="Tenant", resp="TaxProfile", reqs=["TAX-001"])
op("put", "/tax/profile", "updateTaxProfile", "Tax", "به‌روزرسانی پروفایل مالیاتی", perm="tax.manage", scope="Tenant",
   body="TaxProfileUpdate", resp="TaxProfile", reqs=["TAX-001"], audited=True, conflict=True)
op("post", "/tax/keys", "generateSigningKey", "Tax", "تولید کلید در KMS (غیرقابل استخراج)", perm="tax.manage", scope="Tenant",
   idem=True, body="SigningKeyCreate", resp="SigningKey", resp_code="201", reqs=["TAX-002", "ADR-011"], audited=True)
op("get", "/tax/keys/{keyId}/csr", "getSigningKeyCsr", "Tax", "دانلود CSR/کلید عمومی برای ثبت در کارپوشه", perm="tax.manage",
   scope="Tenant", resp="Csr", reqs=["TAX-002"], audited=True)
op("post", "/tax/keys/{keyId}/activate", "activateSigningKey", "Tax", "فعال‌سازی کلید پس از ثبت در کارپوشه", perm="tax.manage",
   scope="Tenant", body="VersionOnly", resp="SigningKey", reqs=["TAX-002", "DI-072"], audited=True, conflict=True)
op("post", "/tax/connection-test", "testTaxConnection", "Tax", "تست اتصال به سامانه مؤدیان", perm="tax.manage", scope="Tenant",
   resp="ConnectionTestResult", reqs=["TAX-001"], rate="tax-test")
op("get", "/tax/invoices", "listTaxInvoices", "Tax", "صورتحساب‌های مالیاتی", perm="tax.manage", scope="Tenant",
   params=[{"name": "status", "in": "query", "required": False, "schema": {"type": "string"}}],
   resp="TaxInvoice", list_of=True, page=True, reqs=["TAX-005", "TAX-007"])
op("get", "/tax/invoices/{taxInvoiceId}", "getTaxInvoice", "Tax", "جزئیات صورتحساب", perm="tax.manage", scope="Tenant",
   resp="TaxInvoice", reqs=["TAX-005"])
op("post", "/tax/invoices/{taxInvoiceId}/retry", "retryTaxInvoice", "Tax", "ارسال مجدد پس از اصلاح داده (صورتحساب جدید مرتبط)",
   perm="tax.manage", scope="Tenant", idem=True, resp="TaxInvoice", resp_code="202", reqs=["BR-074", "DI-071"], audited=True)
op("get", "/tax/dashboard", "getTaxDashboard", "Tax", "وضعیت صف و مهلت‌ها", perm="tax.manage", scope="Tenant",
   resp="TaxDashboard", reqs=["TAX-007", "BR-076"])

# ---------------- Templates, reports, audit, integrations
tag("Settings", "قالب فاکتور و کلیدهای یکپارچگی")
op("get", "/templates/receipt", "getReceiptTemplate", "Settings", "قالب فاکتور مؤثر (صنف + Override)", resp="ReceiptTemplate",
   reqs=["BR-086", "SAL-012"])
op("put", "/templates/receipt", "updateReceiptTemplate", "Settings", "تغییر بخش‌های مجاز قالب", perm="templates.receipt.manage",
   scope="Tenant", body="ReceiptTemplateUpdate", resp="ReceiptTemplate", reqs=["BR-086", "DI-085"], audited=True, conflict=True)
op("get", "/integrations/keys", "listIntegrationKeys", "Settings", "کلیدهای API فروشگاه", perm="integrations.manage", scope="Tenant",
   resp="IntegrationKey", list_of=True, reqs=["INT-001"])
op("post", "/integrations/keys", "createIntegrationKey", "Settings", "کلید API با Scope", perm="integrations.manage",
   scope="Tenant", body="IntegrationKeyCreate", resp="IntegrationKey", resp_code="201", reqs=["INT-001"], audited=True)
op("delete", "/integrations/keys/{keyId}", "revokeIntegrationKey", "Settings", "ابطال کلید API", perm="integrations.manage",
   scope="Tenant", no_content=True, reqs=["INT-001"], audited=True)
tag("Reports", "گزارش‌ها از Read Model (RPT)")
REPORT_COMMON = [{"$ref": "#/components/parameters/BranchFilter"}]
REPORT_RANGE_Q = [{"$ref": "#/components/parameters/From"}, {"$ref": "#/components/parameters/To"}]
REPORT_FORMAT = {"name": "format", "in": "query", "required": False, "schema": {"type": "string", "enum": ["json", "csv"]},
                 "description": "csv needs data.export, is audited (reports.exported) and answers text/csv with a BOM for Excel; xlsx and "
                                "an async job above 100,000 rows follow (RPT-003)"}
REPORT_DESC = ("Read in place with plain reads: no lock a POS write waits on (RPT-002); meta.dataAsOf is the time of the read. Only the branches "
               "the caller's reports.view reaches (a branch beyond it is 403); 422 report-too-large above 5,000 rows (100,000 as csv). "
               "Range [from, to), default the last 30 days, at most 366.")
op("get", "/reports/sales-daily", "reportSalesDaily", "Reports", "فروش روزانه به تفکیک شعبه، صندوق و صندوق‌دار", perm="reports.view", scope="Branch",
   params=REPORT_COMMON + REPORT_RANGE_Q + [REPORT_FORMAT, {"name": "groupBy", "in": "query", "required": False, "schema": {"type": "string"},
                                                          "description": "Comma list of `register`, `cashier` (day and branch always)"}],
   resp="SalesDailyRow", list_of=True, page=True, reqs=["RPT-001", "RPT-002", "RPT-003"], audited=True, desc=REPORT_DESC)
op("get", "/reports/stock", "reportStock", "Reports", "موجودی و ارزش به تفکیک انبار", perm="reports.view", scope="Branch",
   params=REPORT_COMMON + [REPORT_FORMAT], resp="StockReportRow", list_of=True, page=True, reqs=["RPT-001.1", "RPT-002", "RPT-003"], audited=True,
   desc="Current quantity and value per warehouse and variant. " + REPORT_DESC.split("Range")[0])
op("get", "/reports/returns", "reportReturns", "Reports", "برگشتی‌ها به تفکیک دلیل و صندوق‌دار", perm="reports.view", scope="Branch",
   params=REPORT_COMMON + REPORT_RANGE_Q + [REPORT_FORMAT], resp="ReturnsReportRow", list_of=True, page=True, reqs=["RPT-001.2", "RPT-002", "RPT-003"],
   audited=True, desc="Not yet available: it comes with sales returns (US-093).")
op("get", "/reports/gross-profit", "reportGrossProfit", "Reports", "سود ناخالص (مبنای WAC در لحظه فروش)", perm="reports.view", scope="Branch",
   params=REPORT_COMMON + REPORT_RANGE_Q + [REPORT_FORMAT, {"name": "groupBy", "in": "query", "required": False,
                                                          "schema": {"type": "string", "enum": ["day", "branch", "product"], "default": "day"}}],
   resp="GrossProfitRow", list_of=True, page=True, reqs=["RPT-001.3", "RPT-002", "RPT-003"], audited=True, desc=REPORT_DESC)
tag("Audit", "ممیزی")
op("get", "/audit", "listAudit", "Audit", "رکوردهای ممیزی Tenant", perm="audit.view", scope="Tenant",
   params=[{"$ref": "#/components/parameters/From"}, {"$ref": "#/components/parameters/To"},
           {"name": "action", "in": "query", "required": False, "schema": {"type": "string", "maxLength": 100},
            "description": "Exact action, or a prefix ending in '.' (e.g. `iam.`)"},
           {"name": "actorId", "in": "query", "required": False, "schema": {"type": "string", "format": "uuid"}},
           {"name": "resourceType", "in": "query", "required": False, "schema": {"type": "string", "maxLength": 100}},
           {"name": "resourceId", "in": "query", "required": False, "schema": {"type": "string", "maxLength": 100}}],
   resp="AuditRecord", list_of=True, page=True, reqs=["AUD-001", "AUD-001.1", "AUD-002"])

# ---------------- Integration API (API key)
tag("PartnerApi", "API عمومی با Integration Key (INT)")
op("get", "/partner/catalog/products", "partnerListProducts", "PartnerApi", "کاتالوگ Tenant (catalog.read)", aud="integration",
   resp="Product", list_of=True, page=True, reqs=["INT-001", "AT-180"], rate="partner",
   desc="The store's products (not merged), with variants, barcodes and current prices. Needs catalog.read.")
op("get", "/partner/inventory/balances", "partnerListBalances", "PartnerApi", "موجودی (inventory.read؛ بدون بها)", aud="integration",
   resp="PartnerBalance", list_of=True, page=True, reqs=["INT-001", "AT-180"], rate="partner",
   params=[{"name": "warehouseId", "in": "query", "required": False, "schema": {"type": "string", "format": "uuid"}},
           {"name": "variantId", "in": "query", "required": False, "schema": {"type": "string", "format": "uuid"}}],
   desc="Quantities only: never the cost (WAC) or the value. 401 invalid-api-key for an unknown, wrong, revoked or expired key; "
        "403 scope-missing without inventory.read; 120 calls a minute per key.")

# ---------------- Platform admin
tag("Platform", "Platform Admin (Audience: platform-api؛ بدون دسترسی به داده کسب‌وکار)")
op("get", "/platform/tenants", "platformListTenants", "Platform", "فهرست Tenantها (فراداده)", aud="platform",
   resp="PlatformTenant", list_of=True, page=True, reqs=["§18.2"])
op("post", "/platform/tenants/{tenantId}/status", "platformChangeTenantStatus", "Platform", "تعلیق/فعال‌سازی", aud="platform",
   body="PlatformTenantStatusChange", resp="PlatformTenant", reqs=["TEN-004"], audited=True)
op("post", "/platform/tenants/{tenantId}/support-access-requests", "platformRequestSupportAccess", "Platform",
   "درخواست JIT (تأیید با Owner)", aud="platform", body="SupportAccessRequestCreate", resp="SupportAccessRequest",
   resp_code="201", reqs=["IAM-008", "SoD-05"], audited=True)
op("get", "/platform/support/tenants/{tenantId}/sales", "platformSupportListSales", "Platform",
   "نمای فقط‌خواندنی و Masked فروش‌های اخیر در مدت دسترسی JIT", aud="platform", resp="SupportSale", list_of=True,
   reqs=["IAM-008", "AT-017"], audited=True,
   desc="PlatformSupport with MFA, only while the store owner's grant to this operator is in force (403 support-access-required "
        "otherwise). No customer data, payment references or card digits; every call is audited in the store with actor kind Support.")
op("get", "/platform/tax-submission-policies", "platformListTaxPolicies", "Platform", "Policyهای مهلت مؤدیان", aud="platform",
   resp="TaxSubmissionPolicy", list_of=True, reqs=["BR-075", "TAX-009"])
op("post", "/platform/tax-submission-policies", "platformCreateTaxPolicy", "Platform", "نسخه جدید Policy مهلت", aud="platform",
   body="TaxSubmissionPolicyCreate", resp="TaxSubmissionPolicy", resp_code="201", reqs=["BR-075", "TAX-009"], audited=True)
op("get", "/platform/retention-policies", "platformListRetention", "Platform", "Policyهای نگهداری", aud="platform",
   resp="RetentionPolicy", list_of=True, reqs=["BR-087"])
op("put", "/platform/retention-policies", "platformSetRetention", "Platform", "تعیین کف و پیش‌فرض نگهداری", aud="platform",
   body="RetentionPolicyInput", resp="RetentionPolicy", reqs=["BR-087", "TEN-005.1"], audited=True,
   desc="Publishes the next version of a category's policy (versions never change); validFrom defaults to now. defaultDays is not below "
        "minimumDays. A store may keep data longer than the default but never shorter than the minimum. 409 version-conflict if two "
        "operators publish at once.")
op("put", "/platform/tenants/{tenantId}/legal-holds/{category}", "platformSetLegalHold", "Platform", "Legal Hold روی یک دسته داده‌ی فروشگاه", aud="platform",
   body="LegalHoldInput", resp="LegalHold", reqs=["BR-087", "TEN-005.1"], audited=True,
   desc="While a hold is in force the retention job deletes nothing of the category for that store, and the database refuses any "
        "deletion (RC036). Replaces an earlier hold. 404 for an unknown store.")
op("delete", "/platform/tenants/{tenantId}/legal-holds/{category}", "platformReleaseLegalHold", "Platform", "رفع Legal Hold", aud="platform",
   no_content=True, reqs=["BR-087", "TEN-005.1"], audited=True)
op("get", "/platform/industry-profiles", "platformListIndustries", "Platform", "صنف‌ها و قالب فاکتور", aud="platform",
   resp="IndustryProfile", list_of=True, reqs=["BR-086"])
op("put", "/platform/industry-profiles/{code}", "platformUpdateIndustry", "Platform", "نسخه جدید قالب صنف", aud="platform",
   body="IndustryProfileUpdate", resp="IndustryProfile", reqs=["BR-086"], audited=True, conflict=True)
op("get", "/platform/catalog/suggestions", "platformListCatalogSuggestions", "Platform", "صف Moderation کاتالوگ", aud="platform",
   resp="CatalogSuggestion", list_of=True, page=True, reqs=["CAT-005.1", "US-047"],
   desc="CatalogModerator (MFA) only. Pending suggestions, oldest first; never which store sent one.")
op("post", "/platform/catalog/suggestions/{suggestionId}/decision", "platformDecideCatalogSuggestion", "Platform",
   "انتشار یا رد", aud="platform", body="CatalogReviewDecision", resp_code="204", no_content=True, reqs=["CAT-005", "DI-093", "US-047"],
   audited=True, conflict=True,
   desc="Publish copies only name, brand and category (edited if given) into the global catalog: a new product with its public "
        "barcode, or the product that already holds it, re-verified; every pending suggestion for that code is settled with it. "
        "Reject settles this one only. 409 already-decided.")

# ----------------------------------------------------------------------------- assemble
spec = {
    "openapi": "3.1.0",
    "info": {
        "title": "RetailCloud API",
        "version": "0.1.0",
        "summary": "MVP API — POS, catalog, inventory, accounting, credit, customer identity, tax (Moadian)",
        "description": (
            "D07 of the RetailCloud design package (status: Draft). Source of truth for contracts; generated by "
            "docs/04-api/tools/build_openapi.py.\n\n"
            "**Conventions**\n"
            "- Tenant is never accepted from the client; it comes from the session (BR-001). Other tenants' "
            "resources return 404 (BR-003).\n"
            "- Money = IRR integer as string (`Money`). Quantities = decimal strings (`Quantity`).\n"
            "- All write operations that move money/stock require `Idempotency-Key` (x-idempotent: true).\n"
            "- Errors: `application/problem+json` with stable `code` (see `x-error-catalog`).\n"
            "- `x-permission` / `x-scope` drive the generated authorization tests (D06 SEC-T-010/011/012).\n"
            "- `x-requirements` traces each operation to D02 requirement IDs.\n"
            "- Optimistic concurrency via `expectedVersion` in the body; GET returns ETag.\n"
            "- Timestamps are UTC ISO-8601; Jalali is display-only.\n"
            "- Browsers never call this API directly; the Web BFF does (ADR-003)."),
        "license": {"name": "Proprietary", "identifier": "LicenseRef-Proprietary"},
        "contact": {"name": "RetailCloud Architecture"},
    },
    "servers": [{"url": "https://api.example.invalid/api/v1", "description": "Placeholder; real domain decided in D11"}],
    "security": SEC["merchant"],
    "tags": TAGS,
    "paths": PATHS,
    "components": {
        "securitySchemes": {
            "merchantBearer": {"type": "http", "scheme": "bearer", "bearerFormat": "JWT",
                               "description": "Keycloak realm `merchant`, aud `merchant-api`. Sent by the BFF only."},
            "customerBearer": {"type": "http", "scheme": "bearer", "bearerFormat": "JWT",
                               "description": "Issued by RetailCloud CustomerIdentity after OTP (not Keycloak, ADR-003 §6 as amended); iss retailcloud-customer, aud customer-api; held by the portal BFF only."},
            "platformBearer": {"type": "http", "scheme": "bearer", "bearerFormat": "JWT",
                               "description": "Realm `platform`, aud `platform-api`; WebAuthn + IP allowlist (AUTH-13)."},
            "integrationKey": {"type": "apiKey", "in": "header", "name": "X-Api-Key",
                               "description": "Tenant integration key with scopes (INT-001)."},
        },
        "parameters": PARAMS,
        "responses": RESPONSES,
        "schemas": dict(sorted(SCHEMAS.items())),
    },
    "x-error-catalog": {k: {"status": v[0], "meaning": v[1]} for k, v in ERROR_CATALOG.items()},
}

if __name__ == "__main__":
    header = ("# GENERATED FILE — do not edit by hand. Source: docs/04-api/tools/build_openapi.py\n"
              "# RetailCloud D07 — OpenAPI 3.1 (Draft 0.1.0)\n")
    OUT.write_text(header + yaml.safe_dump(spec, allow_unicode=True, sort_keys=False, width=120), encoding="utf-8")
    # Manifest for the API's default-deny test (EndpointPolicyTests): operation -> method, path, permission.
    def body_props(o):
        ref = o.get("requestBody", {}).get("content", {}).get("application/json", {}).get("schema", {}).get("$ref")
        return sorted(SCHEMAS[ref.split("/")[-1]].get("properties", {}).keys()) if ref else None

    # US-010: the generated security tests (BOLA, scope, role, mass assignment) are driven by this manifest.
    manifest = [{"operationId": o["operationId"], "method": m.upper(), "path": p, "permission": o.get("x-permission"),
                 "allowedWhenReadOnly": bool(o.get("x-allowed-when-read-only")), "scope": o.get("x-scope"),
                 "pathParams": re.findall(r"\{(\w+)\}", p), "bodyProps": body_props(o),
                 "idempotent": any(x.get("$ref", "").endswith("/IdempotencyKey") for x in o.get("parameters", []))}
                for p, item in spec["paths"].items() for m, o in item.items()]
    (OUT.parent / "D07-operations.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    n_ops = sum(len(v) for v in PATHS.values())
    print(f"wrote {OUT} — {len(PATHS)} paths, {n_ops} operations, {len(SCHEMAS)} schemas")
