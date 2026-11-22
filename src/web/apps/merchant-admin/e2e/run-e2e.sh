#!/usr/bin/env bash
# US-021 + US-022 end-to-end proof: Keycloak (realm-as-code) + RetailCloud.Api + merchant-admin BFF + Valkey + browser.
# Prerequisites: Keycloak on 127.0.0.1:8080 (src/backend/dev/keycloak), Valkey on VALKEY_URL (default 127.0.0.1:6379),
# a test DB prepared by src/backend/scripts/setup-test-db.sh, PGHOST/PGPORT/PGUSER (superuser, seeding only) and
# RC_TEST_PG (app role), the BFF built (npm run build), and NODE_PATH pointing at a node_modules with playwright.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; APP="$(cd "$HERE/.." && pwd)"; BACKEND="$(cd "$APP/../../../backend" && pwd)"
KC=${KC:-http://127.0.0.1:8080}; DB=${DB:-rc_it}; LOGS=${LOGS:-$(mktemp -d)}
export VALKEY_URL=${VALKEY_URL:-redis://127.0.0.1:6379}
ISSUER="$KC/realms/merchant"

# 1. Keycloak: subject of owner@spike.test; remove any TOTP from earlier runs so enrolment is exercised every time.
ADMIN=$(curl -sf -d grant_type=password -d client_id=admin-cli -d username=admin -d password=admin \
  "$KC/realms/master/protocol/openid-connect/token" | python3 -c 'import json,sys;print(json.load(sys.stdin)["access_token"])')
AUTH="authorization: Bearer $ADMIN"
SUB=$(curl -sf -H "$AUTH" "$KC/admin/realms/merchant/users?username=owner@spike.test&exact=true" | python3 -c 'import json,sys;print(json.load(sys.stdin)[0]["id"])')
for c in $(curl -sf -H "$AUTH" "$KC/admin/realms/merchant/users/$SUB/credentials" | python3 -c 'import json,sys;print(" ".join(c["id"] for c in json.load(sys.stdin) if c["type"]=="otp"))'); do
  curl -sf -X DELETE -H "$AUTH" "$KC/admin/realms/merchant/users/$SUB/credentials/$c"
done
curl -sf -X DELETE -H "$AUTH" "$KC/admin/realms/merchant/users/$SUB/sessions" 2>/dev/null || true

# 2. Seed: the owner is TenantOwner of store A (MFA required, IAM-003), Cashier of store B (no MFA), and a foreign store.
T1=0194f000-0000-7000-8000-0000000000a1; T2=0194f000-0000-7000-8000-0000000000a2; TF=0194f000-0000-7000-8000-0000000000af
U=0194f000-0000-7000-8000-0000000000b1
psql -v ON_ERROR_STOP=1 -q -d "$DB" <<SQL
DELETE FROM iam.tenant_membership WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM iam.merchant_user WHERE id = '$U' OR (idp_issuer = '$ISSUER' AND idp_subject = '$SUB');
DELETE FROM platform.tenant WHERE id IN ('$T1','$T2','$TF');
INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES
  ('$T1','فروشگاه الف','supermarket'), ('$T2','فروشگاه ب','supermarket'), ('$TF','فروشگاه غریبه','supermarket');
INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ('$U','$ISSUER','$SUB','مالک آزمایشی');
INSERT INTO iam.tenant_membership (tenant_id, user_id, roles) VALUES ('$T1','$U','{TenantOwner}'), ('$T2','$U','{Cashier}');
SQL

# 3. API, and the BFF with the Valkey session store. setsid: each server gets its own process group so the whole tree
#    (dotnet run → app, npx → next-server) is stopped. restart-bff.sh lets the browser test restart the BFF mid-session.
export SESSION_ENC_KEY=${SESSION_ENC_KEY:-$(head -c 32 /dev/urandom | base64)}
cat > "$LOGS/start-bff.sh" <<SH
#!/usr/bin/env bash
cd "$APP" && KC_ISSUER="$ISSUER" KC_CLIENT_ID=rc-bff-merchant KC_CLIENT_SECRET=dev-only-secret-change-me \
  APP_URL=http://127.0.0.1:3000 API_URL=http://127.0.0.1:5080 COOKIE_SECURE=false NEXT_TELEMETRY_DISABLED=1 \
  SESSION_STORE=valkey VALKEY_URL="$VALKEY_URL" SESSION_ENC_KEY="$SESSION_ENC_KEY" \
  setsid npx next start -H 127.0.0.1 -p 3000 >>"$LOGS/bff.log" 2>&1 &
echo \$! > "$LOGS/bff.pid"
for i in \$(seq 1 60); do curl -s --max-time 2 -o /dev/null http://127.0.0.1:3000/ && exit 0; sleep 0.5; done; exit 1
SH
cat > "$LOGS/restart-bff.sh" <<SH
#!/usr/bin/env bash
kill -- -\$(cat "$LOGS/bff.pid") 2>/dev/null || true
for i in \$(seq 1 40); do curl -s --max-time 1 -o /dev/null http://127.0.0.1:3000/ || break; sleep 0.25; done
exec "$LOGS/start-bff.sh"
SH
chmod +x "$LOGS/start-bff.sh" "$LOGS/restart-bff.sh"
cleanup() { kill -- -"${API_PID:-0}" 2>/dev/null || true; [ -f "$LOGS/bff.pid" ] && kill -- -"$(cat "$LOGS/bff.pid")" 2>/dev/null || true; }
trap cleanup EXIT
( cd "$BACKEND" && exec setsid env ASPNETCORE_URLS=http://127.0.0.1:5080 ASPNETCORE_ENVIRONMENT=Development \
  Auth__Merchant__Authority="$ISSUER" Auth__Merchant__RequireHttpsMetadata=false ConnectionStrings__Main="$RC_TEST_PG" \
  dotnet run --no-launch-profile --project Hosts/RetailCloud.Api >"$LOGS/api.log" 2>&1 ) & API_PID=$!
"$LOGS/start-bff.sh" || { echo "BFF did not start"; tail -30 "$LOGS/bff.log"; exit 1; }
for i in $(seq 1 180); do curl -sf http://127.0.0.1:5080/health >/dev/null && break; sleep 1; done
curl -sf http://127.0.0.1:5080/health >/dev/null || { echo "API did not start"; tail -30 "$LOGS/api.log"; exit 1; }

# 4. Browser checks.
T1=$T1 T2=$T2 TF=$TF APP_DIR="$APP" RESTART_BFF="$LOGS/restart-bff.sh" node "$HERE/bff.e2e.mjs" \
  || { echo "--- api.log"; tail -30 "$LOGS/api.log"; echo "--- bff.log"; tail -30 "$LOGS/bff.log"; exit 1; }
