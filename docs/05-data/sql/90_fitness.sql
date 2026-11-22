-- NFR-SEC-003 fitness function. CI fails if any query below returns rows.
-- (1) Tenant-owned tables (have tenant_id, or are platform.tenant) without ENABLE+FORCE RLS or without a policy.
SELECT 'missing-rls' AS violation, n.nspname || '.' || c.relname AS object
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r','p') AND NOT c.relispartition
  AND n.nspname IN ('platform','merch','inv','sales','acc','ar','cid','tax','audit')
  AND (EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped)
       OR (n.nspname, c.relname) = ('platform','tenant'))
  AND (NOT c.relrowsecurity OR NOT c.relforcerowsecurity
       OR NOT EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid))
UNION ALL
-- (2) Partitions must not be directly accessible by application roles (partitions do not inherit policies).
SELECT 'partition-privilege', n.nspname || '.' || c.relname || ' -> ' || r.rolname
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
CROSS JOIN (VALUES ('rc_app'),('rc_worker'),('rc_reporting'),('rc_tax_signer')) AS r(rolname)
WHERE c.relispartition AND has_table_privilege(r.rolname, c.oid, 'SELECT,INSERT,UPDATE,DELETE')
UNION ALL
-- (3) Application roles must not bypass RLS, be superuser, or own tables.
SELECT 'role-too-powerful', rolname FROM pg_roles
WHERE rolname IN ('rc_app','rc_worker','rc_reporting','rc_tax_signer') AND (rolbypassrls OR rolsuper)
UNION ALL
SELECT 'app-role-owns-table', n.nspname || '.' || c.relname
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace JOIN pg_roles o ON o.oid = c.relowner
WHERE o.rolname IN ('rc_app','rc_worker','rc_reporting','rc_tax_signer')
UNION ALL
-- (4) Global customer data must not be readable by reporting / tax signer.
SELECT 'cid-global-exposed', c.relname || ' -> ' || r.rolname
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
CROSS JOIN (VALUES ('rc_reporting'),('rc_tax_signer')) AS r(rolname)
WHERE n.nspname = 'cid' AND c.relkind = 'r' AND has_table_privilege(r.rolname, c.oid, 'SELECT');
