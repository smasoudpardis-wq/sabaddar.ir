#!/usr/bin/env bash
# Usage: PGHOST=... PGPORT=... PGUSER=postgres ./run_all.sh   (creates and drops database rc_d05_test)
set -euo pipefail
cd "$(dirname "$0")/.."
DB=rc_d05_test
echo "--- RC code registry (01_core.sql): one meaning per code, every raised code registered ---"
REG=$(grep -oE '^--   RC[0-9]{3} ' 01_core.sql | awk '{print $2}' | sort)
DUP=$(uniq -d <<<"$REG")
UNREG=$(comm -23 <(grep -ohE "ERRCODE = 'RC[0-9]{3}'" ./*.sql | grep -oE 'RC[0-9]{3}' | sort -u) <(uniq <<<"$REG"))
if [ -n "$DUP$UNREG" ]; then echo "REGISTRY FAILED: listed twice: ${DUP:-none}; raised but not listed: ${UNREG:-none}"; exit 1; fi
echo "REGISTRY PASS ($(wc -l <<<"$REG") codes)"
PGOPTIONS="-c client_min_messages=warning" psql -v ON_ERROR_STOP=1 -q -d postgres -c "DROP DATABASE IF EXISTS $DB" -c "CREATE DATABASE $DB"
for f in 00_bootstrap_superuser.sql 01_core.sql 10_platform.sql 15_iam.sql 20_merch.sql 30_inv.sql 40_sales.sql \
         50_acc.sql 55_ar.sql 60_cid.sql 70_tax.sql 80_audit.sql; do
  PGOPTIONS="-c client_min_messages=warning" psql -v ON_ERROR_STOP=1 -q -d $DB -f "$f"
done
psql -v ON_ERROR_STOP=1 -q -d $DB -f tests/tests.sql
PGOPTIONS="-c client_min_messages=warning" psql -v ON_ERROR_STOP=1 -q -d $DB -f 91_oracles.sql
psql -v ON_ERROR_STOP=1 -q -d $DB -f tests/oracles_test.sql
echo "--- fitness (must be empty) ---"
OUT=$(psql -v ON_ERROR_STOP=1 -At -d $DB -f 90_fitness.sql)
if [ -n "$OUT" ]; then echo "$OUT"; echo "FITNESS FAILED"; exit 1; fi
echo "FITNESS PASS"
echo "--- fitness self-test: a table without RLS must be reported ---"
psql -v ON_ERROR_STOP=1 -q -d $DB -c "SET ROLE rc_migrator; CREATE TABLE sales.bad_example (tenant_id uuid, x int);"
OUT=$(psql -v ON_ERROR_STOP=1 -At -d $DB -f 90_fitness.sql)
psql -v ON_ERROR_STOP=1 -q -d $DB -c "DROP TABLE sales.bad_example;"
if [[ "$OUT" == *"missing-rls|sales.bad_example"* ]]; then echo "FITNESS SELF-TEST PASS"; else echo "FITNESS SELF-TEST FAILED: $OUT"; exit 1; fi
