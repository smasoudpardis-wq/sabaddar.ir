#!/usr/bin/env bash
# US-021 end-to-end proof: Keycloak (realm-as-code) + RetailCloud.Api + merchant-admin BFF + real browser.
# Prerequisites: Keycloak on 127.0.0.1:8080 (src/backend/dev/keycloak), a test DB prepared by
# src/backend/scripts/setup-test-db.sh, PGHOST/PGPORT/PGUSER (superuser, seeding only) and RC_TEST_PG (app role),
# the BFF built (npm run build), and NODE_PATH pointing at a node_modules with playwright.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; APP="$HERE/.."; BACKEND="$(cd "$APP/../../../backend" && pwd)"
KC=${KC:-http://127.0.0.1:8080}; DB=${DB:-rc_it}; LOGS=${LOGS:-$(mktemp -d)}
ISSUER="$KC/realms/merchant"

# 1. Keycloak user id of owner@spike.test (the subject the API sees in the token).
ADMIN=$(curl -sf -d grant_type=password -d client_id=admin-cli -d username=admin -d password=admin \
  "$KC/realms/master/protocol/openid-connect/token" | python3 -c 'import json,sys;print(json.load(sys.stdin)["access_token"])')
SUB=$(curl -sf -H "authorization: Bearer $ADMIN" "$KC/admin/realms/merchant/users?username=owner@spike.test&exact=true" \
  | python3 -c 'import json,sys;print(json.load(sys.stdin)[0]["id"])')

# 2. Seed: two stores the owner belongs to, one foreign store.
T1=0194f000-0000-7000-8000-0000000000a1; T2=0194f000-0000-7000-8000-0000000000a2; TF=0194f000-0000-7000-8000-0000000000af
U=0194f000-0000-7000-8000-0000000000b1
psql -v ON_ERROR_STOP=1 -q -d "$DB" <<SQL
DELETE FROM iam.tenant_membership WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM iam.merchant_user WHERE id = '$U' OR (idp_issuer = '$ISSUER' AND idp_subject = '$SUB');
DELETE FROM platform.tenant WHERE id IN ('$T1','$T2','$TF');
INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES
  ('$T1','فروشگاه الف','supermarket'), ('$T2','فروشگاه ب','supermarket'), ('$TF','فروشگاه غریبه','supermarket');
INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ('$U','$ISSUER','$SUB','مالک آزمایشی');
INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ('$T1','$U'), ('$T2','$U');
SQL

# 3. API and BFF.
# setsid: each server runs in its own process group so the whole tree (dotnet run → app, npx → next-server) is stopped.
cleanup() { for p in ${API_PID:-} ${BFF_PID:-}; do kill -- -"$p" 2>/dev/null || true; done; }
trap cleanup EXIT
( cd "$BACKEND" && exec setsid env ASPNETCORE_URLS=http://127.0.0.1:5080 ASPNETCORE_ENVIRONMENT=Development \
  Auth__Merchant__Authority="$ISSUER" Auth__Merchant__RequireHttpsMetadata=false ConnectionStrings__Main="$RC_TEST_PG" \
  dotnet run --no-launch-profile --project Hosts/RetailCloud.Api >"$LOGS/api.log" 2>&1 ) & API_PID=$!
( cd "$APP" && exec setsid env KC_ISSUER="$ISSUER" KC_CLIENT_ID=rc-bff-merchant KC_CLIENT_SECRET=dev-only-secret-change-me \
  APP_URL=http://127.0.0.1:3000 API_URL=http://127.0.0.1:5080 COOKIE_SECURE=false NEXT_TELEMETRY_DISABLED=1 \
  npx next start -H 127.0.0.1 -p 3000 >"$LOGS/bff.log" 2>&1 ) & BFF_PID=$!
for i in $(seq 1 180); do curl -sf http://127.0.0.1:5080/health >/dev/null && curl -sf -o /dev/null http://127.0.0.1:3000/ && break; sleep 1; done
curl -sf http://127.0.0.1:5080/health >/dev/null || { echo "API did not start"; tail -30 "$LOGS/api.log"; exit 1; }

# 4. Browser checks.
T1=$T1 T2=$T2 TF=$TF node "$HERE/bff.e2e.mjs" || { echo "--- api.log"; tail -30 "$LOGS/api.log"; echo "--- bff.log"; tail -30 "$LOGS/bff.log"; exit 1; }
