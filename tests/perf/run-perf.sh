#!/usr/bin/env bash
# US-191: run one load scenario against a local stack and check every oracle afterwards (D10 §7: PASS without healthy Oracles means nothing).
#   SCENARIO=SMOKE|L1|L2|L3|L5|L6 [REGISTERS=20] tests/perf/run-perf.sh
# Needs: k6 on PATH, Keycloak (src/backend/dev/keycloak) on :8080, a database from src/backend/scripts/setup-test-db.sh (DB, default rc_it),
# PGHOST/PGUSER/PGPASSWORD of a superuser (seeding and oracles only), RC_TEST_PG (app role). The load goes through the real API.
# This is a development-size run: the SLO verdict of D10 needs the Perf environment (Prod-sized hardware, D11).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; BACKEND="$(cd "$HERE/../../src/backend" && pwd)"
KC=${KC:-http://127.0.0.1:8080}; DB=${DB:-rc_it}; LOGS=${LOGS:-$(mktemp -d)}; REGISTERS=${REGISTERS:-20}; SCENARIO=${SCENARIO:-SMOKE}
ISSUER="$KC/realms/merchant"
command -v k6 >/dev/null || { echo "k6 is not installed (https://grafana.com/docs/k6/latest/set-up/install-k6/)"; exit 2; }

# 1. A dev-only direct-grant client for the load generator (the BFF client has none): created once, with the audience scope the API expects.
ADMIN=$(curl -sf -d grant_type=password -d client_id=admin-cli -d username=admin -d password=admin "$KC/realms/master/protocol/openid-connect/token" \
  | python3 -c 'import json,sys;print(json.load(sys.stdin)["access_token"])')
AUTH="authorization: Bearer $ADMIN"
if [ "$(curl -sf -H "$AUTH" "$KC/admin/realms/merchant/clients?clientId=rc-perf" | python3 -c 'import json,sys;print(len(json.load(sys.stdin)))')" = 0 ]; then
  curl -sf -X POST -H "$AUTH" -H 'content-type: application/json' "$KC/admin/realms/merchant/clients" \
    -d '{"clientId":"rc-perf","publicClient":true,"directAccessGrantsEnabled":true,"standardFlowEnabled":false,"defaultClientScopes":["basic","acr","profile","merchant-api-audience"]}'
fi
SUB=$(curl -sf -H "$AUTH" "$KC/admin/realms/merchant/users?username=cashier@spike.test&exact=true" | python3 -c 'import json,sys;print(json.load(sys.stdin)[0]["id"])')

# 2. The perf store, accounting set up, and the API.
psql -v ON_ERROR_STOP=1 -q -d "$DB" -v perf_user_sub="$SUB" -v issuer="$ISSUER" -v registers="$REGISTERS" -f "$HERE/seed.sql"
WORKER_PG=$(printf '%s' "$RC_TEST_PG" | sed 's/Username=rc_app_test;Password=rc_app_test/Username=rc_worker_test;Password=rc_worker_test/')
( cd "$BACKEND" && dotnet build Hosts/RetailCloud.Api -c Release -v q -nologo >"$LOGS/build.log" 2>&1 && dotnet build Hosts/RetailCloud.Workers -c Release -v q -nologo >>"$LOGS/build.log" 2>&1 ) || { tail -30 "$LOGS/build.log"; exit 1; }
( cd "$BACKEND" && env DOTNET_ENVIRONMENT=Development ConnectionStrings__Main="$WORKER_PG" dotnet run -c Release --no-build --no-launch-profile --project Hosts/RetailCloud.Workers -- setup-accounting 0194f000-0000-7000-8000-0000000000f1 >"$LOGS/setup-accounting.log" 2>&1 )
cleanup() { kill -- -"${API_PID:-0}" 2>/dev/null || true; kill -- -"${WORKERS_PID:-0}" 2>/dev/null || true; }
trap cleanup EXIT
( cd "$BACKEND" && exec setsid env ASPNETCORE_URLS=http://127.0.0.1:5080 ASPNETCORE_ENVIRONMENT=Development Auth__Merchant__Authority="$ISSUER" \
  Auth__Merchant__RequireHttpsMetadata=false ConnectionStrings__Main="$RC_TEST_PG" dotnet run -c Release --no-build --no-launch-profile --project Hosts/RetailCloud.Api >"$LOGS/api.log" 2>&1 ) & API_PID=$!
# the Workers relay the outbox (stock and journal consumers): the load is not the whole system without them
( cd "$BACKEND" && exec setsid env DOTNET_ENVIRONMENT=Development ConnectionStrings__Main="$WORKER_PG" dotnet run -c Release --no-build --no-launch-profile --project Hosts/RetailCloud.Workers >"$LOGS/workers.log" 2>&1 ) & WORKERS_PID=$!
for i in $(seq 1 120); do curl -sf http://127.0.0.1:5080/health >/dev/null && break; sleep 1; done
curl -sf http://127.0.0.1:5080/health >/dev/null || { tail -30 "$LOGS/api.log"; exit 1; }

# 3. The scenario (L6 also runs the report readers), then the oracles.
STATUS=0
if [ "$SCENARIO" = L6 ]; then
  k6 run --summary-export "$LOGS/readers.json" -e DURATION="${DURATION:-15m}" "$HERE/k6/reports-under-load.js" & READERS=$!
fi
SCENARIO=$SCENARIO REGISTERS=$REGISTERS k6 run --summary-export "$LOGS/summary.json" -e SCENARIO="$SCENARIO" -e REGISTERS="$REGISTERS" ${DURATION:+-e DURATION="$DURATION"} "$HERE/k6/pos.js" || STATUS=$?
[ -n "${READERS:-}" ] && { wait "$READERS" || STATUS=$?; }
echo "--- oracles (must be empty)"
sleep 20   # let the Workers drain the outbox (O-2/O-3 compare the ledger with the sales)
OUT=$(psql -At -d "$DB" -c "SELECT oracle || ' tenant=' || tenant_id || ' ' || detail FROM core.run_oracles() WHERE tenant_id = '0194f000-0000-7000-8000-0000000000f1'")
if [ -n "$OUT" ]; then echo "$OUT"; echo "ORACLES FAILED"; STATUS=1; else echo "ORACLES PASS"; fi
echo "summary: $LOGS/summary.json"
exit $STATUS
