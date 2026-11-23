SET ROLE rc_migrator;

CREATE TABLE audit.chain_head (
  tenant_id uuid PRIMARY KEY,
  last_seq  bigint NOT NULL,
  last_hash bytea  NOT NULL
);
CALL core.enable_tenant_rls('audit.chain_head');

CREATE TABLE audit.audit_record (
  tenant_id     uuid NOT NULL,
  seq           bigint NOT NULL,
  id            uuid NOT NULL,
  occurred_at   timestamptz NOT NULL DEFAULT now(),
  actor_id      uuid,
  actor_kind    text NOT NULL CHECK (actor_kind IN ('User','Support','Worker','System')),
  actor_roles   text[] NOT NULL DEFAULT '{}',          -- AUD-001.1 who: roles in the tenant at that moment
  impersonated_by uuid,                                -- JIT support acting for a user (IAM-008)
  branch_id     uuid,                                  -- AUD-001.1 where
  terminal_id   uuid,
  client_ip     inet,
  action        text NOT NULL,
  resource_type text NOT NULL,
  resource_id   text,
  reason        text,
  details       jsonb NOT NULL DEFAULT '{}'::jsonb,   -- never OTP/token/PAN/full PII (AUD-003)
  prev_hash     bytea NOT NULL,
  hash          bytea NOT NULL,
  PRIMARY KEY (tenant_id, seq)
);
CALL core.enable_tenant_rls('audit.audit_record');

CREATE OR REPLACE FUNCTION audit.canonical(r audit.audit_record) RETURNS text
LANGUAGE sql IMMUTABLE AS $$
  SELECT concat_ws('|', r.tenant_id, r.seq, r.id,
                   to_char(r.occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
                   r.actor_id, r.actor_kind, array_to_string(r.actor_roles, ','), r.impersonated_by, r.branch_id, r.terminal_id,
                   host(r.client_ip), r.action, r.resource_type, r.resource_id, r.reason, r.details::text)
$$;

-- AUD-002: seq + hash chain per tenant, assigned by the database (callers cannot choose them).
-- SECURITY DEFINER is a documented exception (ADR-002): the app role has no privileges on chain_head.
CREATE OR REPLACE FUNCTION audit.chain_before_insert() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
DECLARE v_seq bigint; v_prev bytea;
BEGIN
  INSERT INTO audit.chain_head (tenant_id, last_seq, last_hash)
  VALUES (NEW.tenant_id, 0, '\x00'::bytea) ON CONFLICT (tenant_id) DO NOTHING;
  SELECT last_seq, last_hash INTO v_seq, v_prev FROM audit.chain_head WHERE tenant_id = NEW.tenant_id FOR UPDATE;
  NEW.seq := v_seq + 1;
  NEW.prev_hash := v_prev;
  NEW.hash := public.digest(v_prev || convert_to(audit.canonical(NEW), 'UTF8'), 'sha256');
  UPDATE audit.chain_head SET last_seq = NEW.seq, last_hash = NEW.hash WHERE tenant_id = NEW.tenant_id;
  RETURN NEW;
END $$;
CREATE TRIGGER tr_audit_chain BEFORE INSERT ON audit.audit_record
  FOR EACH ROW EXECUTE FUNCTION audit.chain_before_insert();
CREATE TRIGGER tr_audit_append_only BEFORE UPDATE OR DELETE ON audit.audit_record
  FOR EACH ROW EXECUTE FUNCTION core.forbid_update_delete();

-- Returns the first broken seq for the current tenant, or NULL when the chain is intact (PM-09).
CREATE OR REPLACE FUNCTION audit.verify_chain() RETURNS bigint LANGUAGE plpgsql STABLE AS $$
DECLARE r audit.audit_record; v_prev bytea := '\x00'::bytea; v_expected bigint := 1;
BEGIN
  FOR r IN SELECT * FROM audit.audit_record WHERE tenant_id = core.current_tenant() ORDER BY seq LOOP
    IF r.seq <> v_expected OR r.prev_hash <> v_prev
       OR r.hash <> public.digest(v_prev || convert_to(audit.canonical(r), 'UTF8'), 'sha256') THEN
      RETURN r.seq;
    END IF;
    v_prev := r.hash; v_expected := v_expected + 1;
  END LOOP;
  RETURN NULL;
END $$;

-- AUD-002: daily anchors of each tenant's chain head. Kept here until the WORM object store (MinIO Object Lock, X-07)
-- exists; insert-only for the worker, so a later rewrite of the chain contradicts an earlier anchor.
CREATE TABLE audit.chain_anchor (
  tenant_id   uuid NOT NULL,
  anchored_at timestamptz NOT NULL DEFAULT now(),
  last_seq    bigint NOT NULL,
  last_hash   bytea NOT NULL,
  intact      boolean NOT NULL,
  broken_seq  bigint,
  PRIMARY KEY (tenant_id, anchored_at),
  CHECK (intact = (broken_seq IS NULL))
);
CALL core.enable_tenant_rls('audit.chain_anchor');
CREATE TRIGGER tr_anchor_append_only BEFORE UPDATE OR DELETE ON audit.chain_anchor
  FOR EACH ROW EXECUTE FUNCTION core.forbid_update_delete();

GRANT USAGE ON SCHEMA audit TO rc_app, rc_worker, rc_reporting;
GRANT SELECT, INSERT ON audit.chain_anchor TO rc_worker;
GRANT SELECT ON audit.chain_anchor TO rc_reporting;
GRANT SELECT, INSERT ON audit.audit_record TO rc_app, rc_worker;
GRANT SELECT ON audit.audit_record TO rc_reporting;
GRANT EXECUTE ON FUNCTION audit.verify_chain() TO rc_worker;
RESET ROLE;
