#!/usr/bin/env bash
# US-004 / SEC-T-061 (CI): the Migrator, run as a NON-superuser login, builds a schema identical to the reference DDL,
# passes the RLS fitness function, and a second run is a no-op.
# Usage: PGHOST=... PGPORT=... PGUSER=postgres [PGPASSWORD=...] scripts/check-migrations.sh   (needs `dotnet build -c Release` first)
set -euo pipefail
cd "$(dirname "$0")/.."
SQL=../../docs/05-data/sql
REF=rc_mig_ref; MIG=rc_mig_check; OUT=$(mktemp -d)
MIGRATOR="dotnet Hosts/RetailCloud.Migrator/bin/Release/net10.0/RetailCloud.Migrator.dll --sql-dir $SQL"
export PGOPTIONS="-c client_min_messages=warning"
bash scripts/setup-test-db.sh "$REF" >/dev/null
psql -v ON_ERROR_STOP=1 -q -d postgres -c "DROP DATABASE IF EXISTS $MIG" -c "CREATE DATABASE $MIG"
psql -v ON_ERROR_STOP=1 -q -d postgres <<'SQL'
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rc_migrate_ci') THEN
    CREATE ROLE rc_migrate_ci LOGIN PASSWORD 'rc_migrate_ci' NOSUPERUSER NOBYPASSRLS NOCREATEROLE NOCREATEDB IN ROLE rc_migrator, rc_tenant_directory;
  END IF;
END $$;
SQL
psql -v ON_ERROR_STOP=1 -q -d "$MIG" -f "$SQL/00_bootstrap_superuser.sql"     # superuser part, once per database
CS="Host=${PGHOST:-localhost};Port=${PGPORT:-5432};Database=$MIG;Username=rc_migrate_ci;Password=rc_migrate_ci"
ConnectionStrings__Migrator="$CS" $MIGRATOR
ConnectionStrings__Migrator="$CS" $MIGRATOR | tee "$OUT/second.txt"
grep -q "^0 applied now" "$OUT/second.txt" || { echo "second run was not a no-op"; exit 1; }
for d in "$REF" "$MIG"; do
  pg_dump --schema-only --no-comments --exclude-table='*.migration_history' -d "$d" | grep -v '^--\|^\\restrict\|^\\unrestrict' | sed '/^$/d' > "$OUT/$d.sql"
done
diff "$OUT/$REF.sql" "$OUT/$MIG.sql" || { echo "MIGRATED SCHEMA DIFFERS FROM THE REFERENCE DDL"; exit 1; }
psql -q -d postgres -c "DROP DATABASE $REF" -c "DROP DATABASE $MIG"
echo "migrations OK: non-superuser run, identical schema ($(wc -l < "$OUT/$MIG.sql") lines), fitness clean, idempotent"
