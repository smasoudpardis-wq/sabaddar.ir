-- Run as rc_migrator.  Shared helpers used by every module schema.
SET ROLE rc_migrator;

-- Tenant context: set ONLY by BuildingBlocks UoW via set_config('app.tenant_id', $1, true) (= SET LOCAL).
CREATE OR REPLACE FUNCTION core.current_tenant() RETURNS uuid
LANGUAGE sql STABLE PARALLEL SAFE AS
$$ SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid $$;

-- Standard tenant isolation: ENABLE + FORCE RLS + one policy for all commands.
CREATE OR REPLACE PROCEDURE core.enable_tenant_rls(p_table regclass, p_column name DEFAULT 'tenant_id')
LANGUAGE plpgsql AS $$
BEGIN
  EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', p_table);
  EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', p_table);
  EXECUTE format('DROP POLICY IF EXISTS tenant_isolation ON %s', p_table);
  EXECUTE format(
    'CREATE POLICY tenant_isolation ON %s USING (%I = core.current_tenant()) WITH CHECK (%I = core.current_tenant())',
    p_table, p_column, p_column);
END $$;

-- Generic guard for append-only tables (ledgers, movements, audit).
CREATE OR REPLACE FUNCTION core.forbid_update_delete() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'append-only table %.%: % is not allowed', TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP
    USING ERRCODE = 'RC001';
END $$;

GRANT USAGE ON SCHEMA core TO rc_app, rc_worker, rc_reporting, rc_tax_signer;
GRANT EXECUTE ON FUNCTION core.current_tenant() TO rc_app, rc_worker, rc_reporting, rc_tax_signer;
RESET ROLE;
