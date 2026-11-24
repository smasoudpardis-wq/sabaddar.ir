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
CASHIER_SUB=$(curl -sf -H "$AUTH" "$KC/admin/realms/merchant/users?username=cashier@spike.test&exact=true" | python3 -c 'import json,sys;print(json.load(sys.stdin)[0]["id"])')
# newowner@spike.test signs up from scratch (UC-10): no TOTP, no RetailCloud user.
NEW_SUB=$(curl -sf -H "$AUTH" "$KC/admin/realms/merchant/users?username=newowner@spike.test&exact=true" | python3 -c 'import json,sys;print(json.load(sys.stdin)[0]["id"])')
for c in $(curl -sf -H "$AUTH" "$KC/admin/realms/merchant/users/$NEW_SUB/credentials" | python3 -c 'import json,sys;print(" ".join(c["id"] for c in json.load(sys.stdin) if c["type"]=="otp"))'); do
  curl -sf -X DELETE -H "$AUTH" "$KC/admin/realms/merchant/users/$NEW_SUB/credentials/$c"
done

# 2. Seed: the owner is TenantOwner of store A (MFA required, IAM-003), Cashier of store B (no MFA), and a foreign store.
T1=0194f000-0000-7000-8000-0000000000a1; T2=0194f000-0000-7000-8000-0000000000a2; TF=0194f000-0000-7000-8000-0000000000af
U=0194f000-0000-7000-8000-0000000000b1; APPROVAL=0194f000-0000-7000-8000-0000000000c1; SUPPORT=0194f000-0000-7000-8000-0000000000d1
psql -v ON_ERROR_STOP=1 -q -d "$DB" <<SQL
-- Clean-up of earlier runs: as superuser with triggers off (the last-owner rule would refuse it; FK order irrelevant).
SET session_replication_role = replica;
DELETE FROM iam.user_pin WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM iam.role_assignment WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM iam.tenant_membership WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM iam.merchant_user WHERE id = '$U' OR (idp_issuer = '$ISSUER' AND idp_subject = '$SUB');
DELETE FROM iam.invitation WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM iam.approval_request WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM iam.support_access_request WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM iam.role_assignment WHERE user_id IN (SELECT id FROM iam.merchant_user WHERE idp_issuer = '$ISSUER' AND idp_subject = '$CASHIER_SUB');
DELETE FROM iam.tenant_membership WHERE user_id IN (SELECT id FROM iam.merchant_user WHERE idp_issuer = '$ISSUER' AND idp_subject = '$CASHIER_SUB');
DELETE FROM iam.merchant_user WHERE idp_issuer = '$ISSUER' AND idp_subject = '$CASHIER_SUB';
DELETE FROM inv.goods_receipt_line WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM inv.goods_receipt WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM inv.supplier WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM acc.journal_line WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM acc.journal_entry WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM acc.account_role WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM acc.account WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM acc.fiscal_period WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM inv.stock_adjustment WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM inv.stock_movement WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM inv.posted_source WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM inv.stock_balance WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM inv.warehouse WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM platform.terminal WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM platform.pairing_code WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM platform.register WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM platform.branch WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM merch.tenant_product_barcode WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM merch.tenant_variant WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM merch.tenant_product WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM platform.subscription WHERE tenant_id IN ('$T1','$T2','$TF');
DELETE FROM platform.tenant WHERE id IN ('$T1','$T2','$TF');
DELETE FROM iam.role_assignment WHERE user_id IN (SELECT id FROM iam.merchant_user WHERE idp_issuer = '$ISSUER' AND idp_subject = '$NEW_SUB');
DELETE FROM iam.tenant_membership WHERE user_id IN (SELECT id FROM iam.merchant_user WHERE idp_issuer = '$ISSUER' AND idp_subject = '$NEW_SUB');
DELETE FROM iam.merchant_user WHERE idp_issuer = '$ISSUER' AND idp_subject = '$NEW_SUB';
SET session_replication_role = origin;
INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES
  ('$T1','فروشگاه الف','supermarket'), ('$T2','فروشگاه ب','supermarket'), ('$TF','فروشگاه غریبه','supermarket');
UPDATE platform.tenant SET status = 'Suspended' WHERE id = '$T2';   -- US-015: store B is read-only (the E2E user is only a cashier there)
INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ('$T1', gen_random_uuid(), 'B1', 'مرکزی');
INSERT INTO inv.warehouse (tenant_id, id, branch_id, code) SELECT '$T1', gen_random_uuid(), id, 'B1-MAIN' FROM platform.branch WHERE tenant_id = '$T1';
INSERT INTO iam.approval_request (tenant_id, id, action, resource_type, resource_id, scope_type, condition, value, limit_value, reason, binding_hash, requested_by, expires_at)
  VALUES ('$T1', '$APPROVAL', 'pos.discount.apply', 'SaleLine', 'sale-e2e/1', 'Tenant', 'amount-limit', '20', '0', 'مشتری ثابت', sha256('e2e'), gen_random_uuid(), now() + interval '30 minutes');
INSERT INTO iam.support_access_request (tenant_id, id, requested_by, reason, duration_minutes)
  VALUES ('$T1', '$SUPPORT', 'https://platform.test|support-e2e', 'بررسی خطای گزارش فروش', 60);
INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ('$U','$ISSUER','$SUB','مالک آزمایشی');
INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ('$T1','$U'), ('$T2','$U');
INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES
  ('$T1', gen_random_uuid(), '$U', 'TenantOwner', 'Tenant'), ('$T2', gen_random_uuid(), '$U', 'Cashier', 'Tenant');
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
# next-server can leave its process group: also kill whatever still listens on :3000 and wait until the port is free
if command -v fuser >/dev/null; then fuser -k -n tcp 3000 >/dev/null 2>&1 || true; else pkill -f 'next-server' || true; fi
for i in \$(seq 1 40); do curl -s --max-time 1 -o /dev/null http://127.0.0.1:3000/ || break; sleep 0.25; done
exec "$LOGS/start-bff.sh"
SH
chmod +x "$LOGS/start-bff.sh" "$LOGS/restart-bff.sh"
WORKER_PG=$(printf '%s' "$RC_TEST_PG" | sed 's/Username=rc_app_test;Password=rc_app_test/Username=rc_worker_test;Password=rc_worker_test/')
# next-server can outlive its process group after a restart; free the ports too so the next run starts clean.
free_ports() { for p in 3000 5080; do pid=$( (command -v fuser >/dev/null && fuser -n tcp "$p" 2>/dev/null) || true); [ -n "$pid" ] && kill $pid 2>/dev/null || true; done; }
cleanup() { kill -- -"${API_PID:-0}" 2>/dev/null || true; kill -- -"${WORKERS_PID:-0}" 2>/dev/null || true; free_ports; [ -f "$LOGS/bff.pid" ] && kill -- -"$(cat "$LOGS/bff.pid")" 2>/dev/null || true; }
trap cleanup EXIT
# Build once, sequentially: two concurrent `dotnet run` builds race on the shared BuildingBlocks outputs
# (CI saw "deps.json is being used by another process" and the Workers never started).
( cd "$BACKEND" && dotnet build Hosts/RetailCloud.Api -v q -nologo >"$LOGS/build.log" 2>&1 && dotnet build Hosts/RetailCloud.Workers -v q -nologo >>"$LOGS/build.log" 2>&1 ) \
  || { echo "backend build failed"; tail -30 "$LOGS/build.log"; exit 1; }
# store A keeps books: receipts and adjustments post their entries in the same transaction (TEN-001.1)
( cd "$BACKEND" && env DOTNET_ENVIRONMENT=Development ConnectionStrings__Main="$WORKER_PG" \
  dotnet run --no-build --no-launch-profile --project Hosts/RetailCloud.Workers -- setup-accounting "$T1" >"$LOGS/setup-accounting.log" 2>&1 ) \
  || { echo "accounting setup failed"; tail -30 "$LOGS/setup-accounting.log"; exit 1; }
( cd "$BACKEND" && exec setsid env ASPNETCORE_URLS=http://127.0.0.1:5080 ASPNETCORE_ENVIRONMENT=Development \
  Auth__Merchant__Authority="$ISSUER" Auth__Merchant__RequireHttpsMetadata=false ConnectionStrings__Main="$RC_TEST_PG" \
  dotnet run --no-build --no-launch-profile --project Hosts/RetailCloud.Api >"$LOGS/api.log" 2>&1 ) & API_PID=$!
( cd "$BACKEND" && exec setsid env DOTNET_ENVIRONMENT=Development ConnectionStrings__Main="$WORKER_PG" \
  dotnet run --no-build --no-launch-profile --project Hosts/RetailCloud.Workers >"$LOGS/workers.log" 2>&1 ) & WORKERS_PID=$!
"$LOGS/start-bff.sh" || { echo "BFF did not start"; tail -30 "$LOGS/bff.log"; exit 1; }
for i in $(seq 1 180); do curl -sf http://127.0.0.1:5080/health >/dev/null && break; sleep 1; done
curl -sf http://127.0.0.1:5080/health >/dev/null || { echo "API did not start"; tail -30 "$LOGS/api.log"; exit 1; }

# 4. Browser checks.
T1=$T1 T2=$T2 TF=$TF APPROVAL_ID=$APPROVAL SUPPORT_ID=$SUPPORT APP_DIR="$APP" RESTART_BFF="$LOGS/restart-bff.sh" node "$HERE/bff.e2e.mjs" \
  || { echo "--- api.log"; tail -30 "$LOGS/api.log"; echo "--- workers.log"; tail -30 "$LOGS/workers.log"; echo "--- bff.log"; tail -30 "$LOGS/bff.log"; exit 1; }
