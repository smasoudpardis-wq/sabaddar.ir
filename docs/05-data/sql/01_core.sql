-- Application SQLSTATE codes (class RC). RC001-RC029 are LOCAL: their meaning depends on the table (e.g. RC012 is
-- "approval transition" in iam and "posted journal immutable" in acc), so code may only catch them around the one
-- statement it issued (RC015 = support access transition/SoD-05, 15_iam.sql; RC016 = barcode history, 20_merch.sql; RC017 = shift transition/cash movement, 40_sales.sql). RC030+ are GLOBAL, unique across the schema, and may be mapped centrally:
--   RC030  entitlement-exceeded:<resource>:<cap>  (platform.enforce_entitlement; PostgresUnitOfWork -> 403, TEN-003)
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

-- Transactional outbox (ADR-013 amended by US-005: one table for all producers). Written by the producer in the
-- same transaction as its change; the Workers relay dispatches per tenant with that tenant's context (TM-042).
-- Envelope = D03 §7.1. event_type must be prefixed by the producing module (module ownership).
CREATE TABLE core.outbox (
  tenant_id       uuid NOT NULL,
  id              uuid NOT NULL,                       -- UUIDv7 = envelope eventId (ms precision only)
  seq             bigint GENERATED ALWAYS AS IDENTITY, -- emission order: UUIDv7s of the same millisecond are not ordered
  producer        text NOT NULL CHECK (producer ~ '^[A-Z][A-Za-z]+$'),
  event_type      text NOT NULL,
  event_version   int  NOT NULL CHECK (event_version >= 1),
  aggregate_id    uuid NOT NULL,
  payload         jsonb NOT NULL,
  correlation_id  text,
  causation_id    text,
  occurred_at     timestamptz NOT NULL DEFAULT now(),
  published_at    timestamptz,
  attempts        int NOT NULL DEFAULT 0,
  next_attempt_at timestamptz NOT NULL DEFAULT now(),
  last_error      text,
  dead_at         timestamptz,                         -- gave up after max attempts: alert, manual replay
  PRIMARY KEY (tenant_id, id),
  CHECK (event_type LIKE lower(producer) || '.%'),
  CHECK (published_at IS NULL OR dead_at IS NULL)
);
CREATE INDEX ix_outbox_pending ON core.outbox (tenant_id, seq) WHERE published_at IS NULL AND dead_at IS NULL;
CALL core.enable_tenant_rls('core.outbox');

-- Consumer-side dedup (D03 §7.1 rule 4): inserted in the same transaction as the handler's effects.
CREATE TABLE core.inbox (
  tenant_id    uuid NOT NULL,
  consumer     text NOT NULL,
  event_id     uuid NOT NULL,
  processed_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, consumer, event_id)
);
CALL core.enable_tenant_rls('core.inbox');

-- Wake the relay at COMMIT (NOTIFY is transactional); polling stays as the safety net.
CREATE OR REPLACE FUNCTION core.outbox_notify() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM pg_notify('rc_outbox', '');
  RETURN NULL;
END $$;
CREATE TRIGGER outbox_notify AFTER INSERT ON core.outbox FOR EACH STATEMENT EXECUTE FUNCTION core.outbox_notify();

GRANT INSERT ON core.outbox TO rc_app;
GRANT SELECT, INSERT, UPDATE ON core.outbox TO rc_worker;   -- handlers may emit follow-up events
GRANT SELECT, INSERT ON core.inbox TO rc_worker;
GRANT DELETE ON core.outbox, core.inbox TO rc_worker;       -- retention purge (7 days, ADR-013)

GRANT USAGE ON SCHEMA core TO rc_app, rc_worker, rc_reporting, rc_tax_signer;
GRANT EXECUTE ON FUNCTION core.current_tenant() TO rc_app, rc_worker, rc_reporting, rc_tax_signer;
RESET ROLE;

-- Which tenants have due outbox messages? The relay cannot see core.outbox without a tenant context (FORCE RLS), so
-- this narrow function (owner rc_tenant_directory, column grants only; fitness rule 5) returns tenant ids only.
GRANT USAGE ON SCHEMA core TO rc_tenant_directory;
GRANT SELECT (tenant_id, published_at, dead_at, next_attempt_at, producer, occurred_at) ON core.outbox TO rc_tenant_directory;
CREATE OR REPLACE FUNCTION core.outbox_due_tenants(p_limit int DEFAULT 500) RETURNS SETOF uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
  SELECT DISTINCT tenant_id FROM core.outbox
  WHERE published_at IS NULL AND dead_at IS NULL AND next_attempt_at <= now()
  LIMIT p_limit
$$;
GRANT CREATE ON SCHEMA core TO rc_tenant_directory;  -- a new owner needs CREATE on the schema; revoked at once (fitness 6)
ALTER FUNCTION core.outbox_due_tenants(int) OWNER TO rc_tenant_directory;
REVOKE CREATE ON SCHEMA core FROM rc_tenant_directory;
REVOKE ALL ON FUNCTION core.outbox_due_tenants(int) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION core.outbox_due_tenants(int) TO rc_worker;

-- Metrics for D11 (rc_outbox_pending, rc_outbox_oldest_pending_seconds, rc_outbox_dead): aggregates only, no tenant ids.
CREATE OR REPLACE FUNCTION core.outbox_stats()
RETURNS TABLE (producer text, pending bigint, oldest_pending_seconds double precision, dead bigint)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
  SELECT o.producer,
         count(*) FILTER (WHERE o.published_at IS NULL AND o.dead_at IS NULL),
         coalesce(extract(epoch FROM now() - min(o.occurred_at) FILTER (WHERE o.published_at IS NULL AND o.dead_at IS NULL)), 0)::float8,
         count(*) FILTER (WHERE o.dead_at IS NOT NULL)
  FROM core.outbox o
  WHERE o.published_at IS NULL
  GROUP BY o.producer
$$;
GRANT CREATE ON SCHEMA core TO rc_tenant_directory;  -- a new owner needs CREATE on the schema; revoked at once (fitness 6)
ALTER FUNCTION core.outbox_stats() OWNER TO rc_tenant_directory;
REVOKE CREATE ON SCHEMA core FROM rc_tenant_directory;
REVOKE ALL ON FUNCTION core.outbox_stats() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION core.outbox_stats() TO rc_worker;
