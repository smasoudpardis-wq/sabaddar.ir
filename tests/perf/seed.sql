-- US-191: the perf store. psql -v perf_user_sub=<keycloak subject of cashier@spike.test> -v registers=20 -v issuer=<realm url> -f seed.sql
-- One branch with N registers, each with a paired terminal whose device secret is perf-device-<n> (only its SHA-256 is stored),
-- and the perf user as BranchManager over the whole store (no second factor needed; it may create products and open shifts).
\set tenant '''0194f000-0000-7000-8000-0000000000f1'''
\set user '''0194f000-0000-7000-8000-0000000000f2'''
SET session_replication_role = replica;
DELETE FROM iam.tenant_membership WHERE tenant_id = :tenant;
DELETE FROM iam.role_assignment WHERE tenant_id = :tenant;
DELETE FROM iam.merchant_user WHERE id = :user;
SET session_replication_role = origin;
INSERT INTO platform.tenant (id, legal_name, industry_code, status) VALUES (:tenant, 'فروشگاه بار', 'supermarket', 'Active') ON CONFLICT (id) DO NOTHING;
INSERT INTO platform.branch (tenant_id, id, code, name) VALUES (:tenant, '0194f000-0000-7000-8000-0000000000f3', 'P1', 'شعبه بار') ON CONFLICT DO NOTHING;
INSERT INTO inv.warehouse (tenant_id, id, branch_id, code, kind) VALUES (:tenant, '0194f000-0000-7000-8000-0000000000f4', '0194f000-0000-7000-8000-0000000000f3', 'P1-MAIN', 'Sellable') ON CONFLICT DO NOTHING;
-- the plan caps (D-97) would refuse hundreds of registers: the perf store is on an unlimited plan
UPDATE platform.subscription SET plan_code = coalesce((SELECT code FROM platform.plan WHERE max_registers IS NULL AND NOT is_trial LIMIT 1), plan_code) WHERE tenant_id = :tenant;
INSERT INTO platform.register (tenant_id, id, branch_id, code)
  SELECT :tenant, ('0194f000-0000-7000-8000-' || lpad(to_hex(4096 + n), 12, '0'))::uuid, '0194f000-0000-7000-8000-0000000000f3', 'R' || n
    FROM generate_series(1, :registers) n ON CONFLICT DO NOTHING;
INSERT INTO platform.terminal (tenant_id, id, branch_id, device_name, secret_hash, paired_by)
  SELECT :tenant, gen_random_uuid(), '0194f000-0000-7000-8000-0000000000f3', 'perf-' || n, sha256(convert_to('perf-device-' || n, 'UTF8')), :user
    FROM generate_series(1, :registers) n
   WHERE NOT EXISTS (SELECT 1 FROM platform.terminal t WHERE t.tenant_id = :tenant AND t.device_name = 'perf-' || n);
INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES (:user, :'issuer', :'perf_user_sub', 'کاربر بار');
INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES (:tenant, :user);
INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES (:tenant, gen_random_uuid(), :user, 'BranchManager', 'Tenant');
