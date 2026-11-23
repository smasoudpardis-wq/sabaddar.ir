#!/usr/bin/env bash
# Creates a database with the D05 reference schema plus login roles for integration tests.
# Usage: PGHOST=... PGPORT=... PGUSER=postgres scripts/setup-test-db.sh [dbname]
# Then:  export RC_TEST_PG="Host=$PGHOST;Port=$PGPORT;Database=<db>;Username=rc_app_test;Password=rc_app_test"
set -euo pipefail
DB=${1:-rc_it}
SQL="$(cd "$(dirname "$0")/../../../docs/05-data/sql" && pwd)"
export PGOPTIONS="-c client_min_messages=warning"
psql -v ON_ERROR_STOP=1 -q -d postgres -c "DROP DATABASE IF EXISTS $DB" -c "CREATE DATABASE $DB"
for f in 00_bootstrap_superuser.sql 01_core.sql 10_platform.sql 15_iam.sql 20_merch.sql 30_inv.sql 40_sales.sql \
         50_acc.sql 55_ar.sql 60_cid.sql 70_tax.sql 75_notif.sql 80_audit.sql 91_oracles.sql; do
  psql -v ON_ERROR_STOP=1 -q -d "$DB" -f "$SQL/$f"
done
psql -v ON_ERROR_STOP=1 -q -d postgres <<'SQL'
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rc_app_test') THEN
    CREATE ROLE rc_app_test LOGIN PASSWORD 'rc_app_test' NOSUPERUSER NOBYPASSRLS IN ROLE rc_app;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rc_worker_test') THEN
    CREATE ROLE rc_worker_test LOGIN PASSWORD 'rc_worker_test' NOSUPERUSER NOBYPASSRLS IN ROLE rc_worker;
  END IF;
END $$;
SQL
psql -v ON_ERROR_STOP=1 -q -d "$DB" -At -f "$SQL/90_fitness.sql" | { read -r line && { echo "FITNESS FAILED: $line"; exit 1; } || true; }
echo "test database '$DB' ready (fitness clean)"
