# RetailCloud backend (.NET 10 LTS)

Implements the design in [`docs/`](../../docs/README.md). Structure follows D04 §5.1; dependency rules R1–R9 are enforced by
`Tests/Architecture.Tests`.

| Project | Purpose |
|---|---|
| `BuildingBlocks/BuildingBlocks.Domain` | Money (IRR, half-away-from-zero, largest-remainder allocation), Quantity, TaxRate, Percentage, strongly typed ids, Result, IClock, Gtin, PublicCustomerCode — no infrastructure dependencies |
| `BuildingBlocks/BuildingBlocks.Application` | `ITenantContext`, `IUnitOfWork` abstractions |
| `BuildingBlocks/BuildingBlocks.Infrastructure` | `PostgresUnitOfWork`: one connection + transaction across module DbContexts, transaction-local tenant context for RLS, guard against commands outside the transaction |
| `Tests/*` | unit + property (FsCheck), architecture (NetArchTest + custom), integration against PostgreSQL with the D05 schema |

## Build and test

```bash
dotnet build src/backend/RetailCloud.sln
dotnet test  src/backend/RetailCloud.sln        # integration tests skip locally unless RC_TEST_PG is set
```

Integration tests need PostgreSQL with the D05 reference schema and a non-superuser login (superusers bypass RLS):

```bash
PGHOST=localhost PGUSER=postgres PGPASSWORD=... src/backend/scripts/setup-test-db.sh rc_it
export RC_TEST_PG="Host=localhost;Port=5432;Database=rc_it;Username=rc_app_test;Password=rc_app_test"
dotnet test src/backend/RetailCloud.sln
```

CI (`.github/workflows/ci.yml`) always provides `RC_TEST_PG` and fails if any test is skipped.

## Local stack

```bash
docker compose -f src/backend/dev/compose.yaml up -d   # Postgres 17 (D05 schema applied), Valkey, MinIO, Keycloak, OpenBao (dev)
```

## Conventions
- Package versions only in `Directory.Packages.props` (central management); warnings are errors.
- Money is never `double`/`float` (architecture test); amounts cross the wire as integer strings.
- Only `BuildingBlocks.Infrastructure` may set `app.tenant_id` (architecture test R9).
- Cross-module references have no foreign keys (D05 §3); atomicity comes from the shared unit-of-work transaction.
