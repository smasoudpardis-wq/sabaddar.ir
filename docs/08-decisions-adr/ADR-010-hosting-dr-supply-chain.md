# ADR-010 — Hosting، DR و زنجیره تأمین نرم‌افزار

- **وضعیت:** Proposed · **ارجاع:** A-02، NFR-AVL-001..003، §8.7، §17، D04 §7

## زمینه
استقرار در ایران الزامی است. دسترسی به سرویس‌ها و Registryهای خارجی ناپایدار یا محدود است. RPO ≤ ۱۵ دقیقه و RTO ≤ ۴ ساعت هدف است (مشروط به هزینه).

## تصمیم
1. **Kubernetes** روی زیرساخت داخلی (Colocation یا ابر داخلی؛ AQ-01). اگر تیم عملیات آماده نباشد: VMها با Docker Compose و Systemd برای MVP، با همان Containerها (AQ-02).
2. **PostgreSQL HA:** Patroni (Primary + Sync Standby در همان منطقه) + Async Replica برای گزارش + **Async Standby در سایت DR** (منطقه دوم داخل ایران).
3. **Backup:** pgBackRest (Full هفتگی، Diff روزانه، WAL Archiving پیوسته) در Object Storage هر دو سایت، رمزنگاری‌شده. PITR. **Restore Drill ماهانه** (NFR-AVL-003).
4. **Object Storage:** MinIO با Replication بین سایت‌ها و Object Lock برای Audit Head و Backup.
5. **زنجیره تأمین:**
   - Harbor (Container) و Nexus/ProGet (NuGet، npm و apt Mirror).
   - Base Imageهای داخلی امضاشده (Cosign).
   - SBOM (Syft) و Scan (Trivy).
   - نسخه‌ها قفل (Central Package Management و `pnpm-lock`).
   - Build کاملاً با Mirror و بدون اینترنت.
6. **Observability** Self-hosted: Prometheus، Loki، Tempo، Grafana، Alertmanager. اعلان Alert با SMS داخلی و بله به تیم.
7. **DNS و TLS:** گواهی از CA قابل دسترس (یا ACME داخلی). Domainهای `.ir` و/یا بین‌المللی (Verification در D11).

## پیامدها
- ➕ استقلال از سرویس خارجی؛ Build تکرارپذیر.
- ➖ هزینه عملیات و نیاز به تیم DevOps؛ تأخیر Patch امنیتی تا Sync Mirror.

## Failure Modes
| حالت | کنترل |
|---|---|
| از دست رفتن سایت اصلی | Promote DR (Runbook در D11)؛ RPO با WAL Shipping |
| Backup خراب | Drill ماهانه + Checksum |
| Mirror قدیمی | Sync زمان‌بندی‌شده + Alert سن Mirror |
