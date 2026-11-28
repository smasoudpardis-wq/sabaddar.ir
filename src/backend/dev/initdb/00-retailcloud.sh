#!/usr/bin/env bash
# Runs once when the dev Postgres volume is created: applies the D05 reference schema and a dev login role.
set -euo pipefail
export PGOPTIONS="-c client_min_messages=warning"
for f in 00_bootstrap_superuser.sql 01_core.sql 10_platform.sql 15_iam.sql 20_merch.sql 25_catalog.sql 30_inv.sql 40_sales.sql 45_pay.sql \
         50_acc.sql 55_ar.sql 60_cid.sql 70_tax.sql 80_audit.sql 91_oracles.sql; do
  psql -v ON_ERROR_STOP=1 -q -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f "/rc-sql/$f"
done
psql -v ON_ERROR_STOP=1 -q -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
  -c "CREATE ROLE rc_app_dev LOGIN PASSWORD 'rc_app_dev' NOSUPERUSER NOBYPASSRLS IN ROLE rc_app"
