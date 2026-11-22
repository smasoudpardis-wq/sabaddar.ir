# RetailCloud — بسته اسناد طراحی

مرجع الزام‌آور: [`RetailCloud_Claude_Master_Spec_FA (1).md`](../RetailCloud_Claude_Master_Spec_FA%20(1).md) — نسخه 1.0

| سند | مسیر | وضعیت |
|---|---|---|
| خلاصه تصمیم معماری + Bounded Contexts + ریسک‌ها + برنامه | [01-architecture/00-Architecture-Decision-Summary.md](01-architecture/00-Architecture-Decision-Summary.md) | Draft 0.1 |
| D01 Vision / PRD / MVP | [00-product/D01-Vision-PRD-MVP.md](00-product/D01-Vision-PRD-MVP.md) | Draft 0.1 |
| D02 SRS | [00-product/D02-SRS-Functional-NonFunctional.md](00-product/D02-SRS-Functional-NonFunctional.md) | Draft 0.1 |
| D03 DDD Context Map | [02-domain-model/D03-DDD-Context-Map.md](02-domain-model/D03-DDD-Context-Map.md) | Draft 0.1 |
| D04 Architecture C4 + ADRs | [01-architecture/D04-Architecture-C4.md](01-architecture/D04-Architecture-C4.md) · [ADRها](08-decisions-adr/README.md) | Draft 0.1 |
| D05 Database ERD & Migrations | [05-data/D05-Database-ERD-and-Migrations.md](05-data/D05-Database-ERD-and-Migrations.md) · [SQL + tests](05-data/sql/) | Draft 0.1 — SQL tested (52/52) |
| D06 Security Threat Model | [03-security/D06-Security-Threat-Model.md](03-security/D06-Security-Threat-Model.md) | Draft 0.1 |
| D07 OpenAPI | [04-api/D07-API-OpenAPI.yaml](04-api/D07-API-OpenAPI.yaml) · [راهنما](04-api/README.md) | Draft 0.1 — valid 3.1، lint 0 خطا، contract check PASS |
| D08 Flows | [02-domain-model/D08-Flows-BPMN-Sequence.md](02-domain-model/D08-Flows-BPMN-Sequence.md) | Draft 0.1 — 22 نمودار، همه Render شده |
| D09 UX Wireframes | [06-ux/D09-UX-Wireframe-Spec.md](06-ux/D09-UX-Wireframe-Spec.md) | Draft 0.1 |
| D10 Test Plan | [09-testing/D10-Test-Plan-Acceptance.md](09-testing/D10-Test-Plan-Acceptance.md) · [کاتالوگ ۲۶۵ تست](09-testing/test-catalog.csv) | Draft 0.1 |
| D11 DevOps Runbook | [07-operations/D11-DevOps-Runbook-SLO.md](07-operations/D11-DevOps-Runbook-SLO.md) · [Alert rules](07-operations/alerts/retailcloud-rules.yaml) | Draft 0.1 |
| D12 Backlog & Roadmap | [00-product/D12-Implementation-Backlog-Roadmap.md](00-product/D12-Implementation-Backlog-Roadmap.md) · [CSV](00-product/D12-backlog.csv) · [Stories](00-product/D12-backlog-table.md) | Draft 0.1 |
| **Gap Analysis و آمادگی پیاده‌سازی** | [01-architecture/Gap-Analysis-Readiness.md](01-architecture/Gap-Analysis-Readiness.md) | Draft 0.1 — برای تصمیم Gate A |

قرارداد برچسب‌ها: `[REQ]` الزام · `[ASM]` فرض · `[DEC]` تصمیم پیشنهادی · `[REC]` توصیه · `[OQ]` پرسش باز.

## اعتبارسنجی خودکار بسته

| ابزار | بررسی |
|---|---|
| `05-data/sql/tests/run_all.sh` | DDL + ۵۲ بررسی DB + ۵ Oracle + Fitness (PostgreSQL) |
| `04-api/tools/check_contract.py` + Redocly | OpenAPI 3.1 و سازگاری با D02 و D06 |
| `09-testing/tools/build_test_catalog.py` | کاتالوگ تست و ارجاع‌های بی‌تعریف |
| `00-product/tools/build_backlog.py` | پوشش نیازمندی‌ها، وابستگی‌ها و تست‌های Backlog |
| `07-operations/alerts/check_rules.py` | Alertها و Runbookها |
| `tools/check_mermaid.js` | Render همه نمودارها |
| `tools/build_oq_register.py` | ثبت یکپارچه پرسش‌های باز |
