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

-- Idempotency (BR-030/031, D03 §6.3, D-34) for every module: written in the SAME transaction as the business change.
-- A concurrent request with the same key blocks on the primary key until the first one commits (then replays) or
-- rolls back (then executes). Only successful responses are stored. Retention ≥ 7 days (D-14); purge by Workers.
CREATE TABLE core.idempotency_record (
  tenant_id       uuid NOT NULL,
  key             text NOT NULL CHECK (key ~ '^[A-Za-z0-9_-]{16,64}$'),
  operation       text NOT NULL,
  payload_hash    bytea NOT NULL CHECK (length(payload_hash) = 32),
  status          text NOT NULL CHECK (status IN ('InProgress','Completed')),
  response_status int CHECK (response_status BETWEEN 200 AND 299),
  response        text,          -- exact bytes of the first response (jsonb would reorder keys; replays must be identical)
  created_at      timestamptz NOT NULL DEFAULT now(),
  completed_at    timestamptz,
  PRIMARY KEY (tenant_id, key),
  CHECK ((status = 'Completed') = (response_status IS NOT NULL AND completed_at IS NOT NULL))
);
CALL core.enable_tenant_rls('core.idempotency_record');
GRANT SELECT, INSERT, UPDATE ON core.idempotency_record TO rc_app;
GRANT SELECT, DELETE ON core.idempotency_record TO rc_worker;

GRANT USAGE ON SCHEMA core TO rc_app, rc_worker, rc_reporting, rc_tax_signer;
GRANT EXECUTE ON FUNCTION core.current_tenant() TO rc_app, rc_worker, rc_reporting, rc_tax_signer;
RESET ROLE;
