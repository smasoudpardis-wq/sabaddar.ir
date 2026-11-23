SET ROLE rc_migrator;

CREATE TABLE platform.tenant (
  id             uuid PRIMARY KEY,
  legal_name     text NOT NULL,
  industry_code  text NOT NULL,
  time_zone      text NOT NULL DEFAULT 'Asia/Tehran',
  base_currency  char(3) NOT NULL DEFAULT 'IRR' CHECK (base_currency = 'IRR'),
  pricing_mode   text NOT NULL DEFAULT 'TaxExclusive' CHECK (pricing_mode IN ('TaxExclusive','TaxInclusive')),
  status         text NOT NULL DEFAULT 'Trial' CHECK (status IN ('Trial','Active','Suspended','Closed')),
  created_at     timestamptz NOT NULL DEFAULT now(),
  version        int NOT NULL DEFAULT 1
);
CALL core.enable_tenant_rls('platform.tenant', 'id');

CREATE TABLE platform.branch (
  tenant_id  uuid NOT NULL REFERENCES platform.tenant(id),
  id         uuid NOT NULL,
  code       text NOT NULL,
  name       text NOT NULL,
  status     text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Inactive')),
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code)
);
CALL core.enable_tenant_rls('platform.branch');

CREATE TABLE platform.register (
  tenant_id  uuid NOT NULL,
  id         uuid NOT NULL,
  branch_id  uuid NOT NULL,
  code       text NOT NULL,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, branch_id, code),
  FOREIGN KEY (tenant_id, branch_id) REFERENCES platform.branch (tenant_id, id)  -- composite: cross-tenant impossible
);
CALL core.enable_tenant_rls('platform.register');

-- POS devices (TEN-002.1, US-013). A branch-scoped manager issues a 10-minute single-use pairing code; the device
-- redeems it and receives a secret (only hashes are stored). ABAC-04: sales only from an Active terminal (D-46).
CREATE TABLE platform.pairing_code (
  tenant_id  uuid NOT NULL,
  id         uuid NOT NULL,
  branch_id  uuid NOT NULL,
  code_hash  bytea NOT NULL CHECK (length(code_hash) = 32),
  issued_by  uuid NOT NULL,
  expires_at timestamptz NOT NULL,
  used_at    timestamptz,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code_hash),
  FOREIGN KEY (tenant_id, branch_id) REFERENCES platform.branch (tenant_id, id)
);
CALL core.enable_tenant_rls('platform.pairing_code');

CREATE TABLE platform.terminal (
  tenant_id   uuid NOT NULL,
  id          uuid NOT NULL,
  branch_id   uuid NOT NULL,
  device_name text NOT NULL CHECK (length(device_name) BETWEEN 1 AND 100),
  secret_hash bytea NOT NULL UNIQUE CHECK (length(secret_hash) = 32),
  paired_by   uuid NOT NULL,
  paired_at   timestamptz NOT NULL DEFAULT now(),
  status      text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Revoked')),
  revoked_by  uuid,
  revoked_at  timestamptz,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, branch_id) REFERENCES platform.branch (tenant_id, id),
  CHECK ((status = 'Revoked') = (revoked_at IS NOT NULL))
);
CALL core.enable_tenant_rls('platform.terminal');

-- A revoked terminal stays revoked (re-pair with a new code instead).
CREATE OR REPLACE FUNCTION platform.terminal_no_unrevoke() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status = 'Revoked' THEN
    RAISE EXCEPTION 'a revoked terminal cannot change' USING ERRCODE = 'RC011';
  END IF;
  IF NEW.secret_hash <> OLD.secret_hash OR NEW.branch_id <> OLD.branch_id THEN
    RAISE EXCEPTION 'terminal identity is immutable' USING ERRCODE = 'RC011';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER terminal_no_unrevoke BEFORE UPDATE ON platform.terminal FOR EACH ROW EXECUTE FUNCTION platform.terminal_no_unrevoke();

-- Plans and entitlements (TEN-003, US-014, [DEC D-97]). A plan is global reference data; NULL limit = unlimited.
-- The numbers below were approved by the client on 2026-11-23; operators change them by migration.
CREATE TABLE platform.plan (
  code          text PRIMARY KEY CHECK (code ~ '^[A-Za-z]{2,30}$'),
  name          text NOT NULL,
  max_branches  int CHECK (max_branches > 0),
  max_registers int CHECK (max_registers > 0),
  max_users     int CHECK (max_users > 0),
  trial_days    int CHECK (trial_days > 0),           -- set only on the trial plan
  is_trial      boolean NOT NULL DEFAULT false,
  CHECK (is_trial = (trial_days IS NOT NULL))
);
CREATE UNIQUE INDEX ux_plan_single_trial ON platform.plan (is_trial) WHERE is_trial;
INSERT INTO platform.plan (code, name, max_branches, max_registers, max_users, trial_days, is_trial) VALUES
  ('Trial',      'آزمایشی',  2,    4,    5,    14,   true),
  ('Basic',      'پایه',     1,    2,    3,    NULL, false),
  ('Pro',        'حرفه‌ای',   5,    15,   25,   NULL, false),
  ('Enterprise', 'سازمانی',  NULL, NULL, NULL, NULL, false);

-- One subscription per tenant, created with the tenant (trial). Changing the plan is a platform operation (billing is P1):
-- the application roles can read it but never write it.
CREATE TABLE platform.subscription (
  tenant_id     uuid PRIMARY KEY REFERENCES platform.tenant(id),
  plan_code     text NOT NULL REFERENCES platform.plan(code),
  started_at    timestamptz NOT NULL DEFAULT now(),
  trial_ends_at timestamptz,
  updated_at    timestamptz NOT NULL DEFAULT now()
);
CALL core.enable_tenant_rls('platform.subscription');

-- For the application the tenant row already passed RLS, so the context IS the new tenant; an operator inserting a
-- tenant without a context (seeds, support) gets the context for this one insert only, then the previous value back.
CREATE OR REPLACE FUNCTION platform.start_trial() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE prev text := current_setting('app.tenant_id', true);
BEGIN
  PERFORM set_config('app.tenant_id', NEW.id::text, true);
  INSERT INTO platform.subscription (tenant_id, plan_code, trial_ends_at)
  SELECT NEW.id, p.code, now() + make_interval(days => p.trial_days) FROM platform.plan p WHERE p.is_trial;
  PERFORM set_config('app.tenant_id', coalesce(prev, ''), true);
  RETURN NEW;
END $$;
CREATE TRIGGER tenant_start_trial AFTER INSERT ON platform.tenant FOR EACH ROW EXECUTE FUNCTION platform.start_trial();

-- The plan's cap for one resource of the context tenant: NULL = unlimited, 0 = no subscription (fail closed).
CREATE OR REPLACE FUNCTION platform.entitlement_cap(p_resource text) RETURNS int
LANGUAGE sql STABLE SET search_path = pg_catalog AS $$
  SELECT CASE WHEN s.tenant_id IS NULL THEN 0
              ELSE CASE p_resource WHEN 'branches' THEN p.max_branches WHEN 'registers' THEN p.max_registers
                                   WHEN 'users' THEN p.max_users END END
  FROM (SELECT 1) one
  LEFT JOIN platform.subscription s ON s.tenant_id = core.current_tenant()
  LEFT JOIN platform.plan p ON p.code = s.plan_code
$$;

-- The cap is checked in the database (AFTER row triggers, so the row's own errors such as a duplicate code come
-- first), serialised per tenant and resource, so concurrent requests cannot both take the
-- last slot and no code path can skip it. RC030 'entitlement-exceeded:<resource>:<cap>' -> API 403 entitlement-exceeded.
CREATE OR REPLACE FUNCTION platform.enforce_entitlement() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  resource text := TG_ARGV[0];
  cap int;
  used bigint;
BEGIN
  IF NEW.tenant_id IS DISTINCT FROM core.current_tenant() THEN
    RETURN NEW;                                   -- not this tenant's row: RLS rejects it (42501), not the plan
  END IF;
  IF resource <> 'registers' THEN                -- registers have no status column (and are never updated)
    IF NEW.status <> 'Active' THEN
      RETURN NEW;                                 -- an inactive row takes no slot
    END IF;
    IF TG_OP = 'UPDATE' THEN
      IF OLD.status = 'Active' THEN
        RETURN NEW;                               -- already counted
      END IF;
    END IF;
  END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended('rc-entitlement:' || NEW.tenant_id || ':' || resource, 0));
  cap := platform.entitlement_cap(resource);
  IF cap IS NULL THEN
    RETURN NEW;
  END IF;
  IF resource = 'branches' THEN
    SELECT count(*) INTO used FROM platform.branch WHERE tenant_id = NEW.tenant_id AND status = 'Active';
  ELSIF resource = 'registers' THEN
    SELECT count(*) INTO used FROM platform.register WHERE tenant_id = NEW.tenant_id;
  ELSE
    SELECT count(*) INTO used FROM iam.tenant_membership WHERE tenant_id = NEW.tenant_id AND status = 'Active';
  END IF;
  IF used > cap THEN                              -- AFTER trigger: the new row is already counted
    -- the resource and cap travel in the message: drivers redact DETAIL by default
    RAISE EXCEPTION 'entitlement-exceeded:%:%', resource, cap USING ERRCODE = 'RC030';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER branch_entitlement AFTER INSERT OR UPDATE OF status ON platform.branch
  FOR EACH ROW EXECUTE FUNCTION platform.enforce_entitlement('branches');
CREATE TRIGGER register_entitlement AFTER INSERT ON platform.register
  FOR EACH ROW EXECUTE FUNCTION platform.enforce_entitlement('registers');

-- TEN-005 / US-016: asynchronous data export. The API records the request (and an outbox event), the Workers build a
-- ZIP of CSVs under tenants/<tenant>/exports/<random>.zip and complete the row; links are presigned per request (D-49).
CREATE TABLE platform.export_job (
  tenant_id     uuid NOT NULL REFERENCES platform.tenant(id),
  id            uuid NOT NULL,
  requested_by  uuid NOT NULL,
  categories    text[] NOT NULL CHECK (cardinality(categories) BETWEEN 1 AND 7 AND categories <@
                  ARRAY['Products','Customers','Sales','Inventory','Journals','Receivables','TaxInvoices']),
  period_from   timestamptz,
  period_to     timestamptz,
  status        text NOT NULL DEFAULT 'Queued' CHECK (status IN ('Queued','Running','Completed','Failed')),
  object_key    text CHECK (object_key ~ '^tenants/[0-9a-f-]{36}/exports/[A-Za-z0-9_-]{32,}\.zip$'),
  row_count     bigint CHECK (row_count >= 0),
  sha256        bytea CHECK (length(sha256) = 32),
  error         text,
  created_at    timestamptz NOT NULL DEFAULT now(),
  completed_at  timestamptz,
  expires_at    timestamptz,
  purged_at     timestamptz,                 -- the file was deleted from storage after expires_at
  PRIMARY KEY (tenant_id, id),
  CHECK (purged_at IS NULL OR purged_at >= expires_at),
  CHECK (period_from IS NULL OR period_to IS NULL OR period_from < period_to),
  CHECK ((status = 'Completed') = (object_key IS NOT NULL AND completed_at IS NOT NULL AND expires_at IS NOT NULL)),
  CHECK (object_key IS NULL OR object_key LIKE 'tenants/' || tenant_id || '/%')    -- a job can only point at its own tenant
);
CALL core.enable_tenant_rls('platform.export_job');

-- TEN-004 / US-015: why the context tenant is read-only, or NULL when it is not. Suspended and closed tenants, and a
-- trial whose end has passed, keep every row and can still read; the API refuses their writes (403 tenant-read-only).
CREATE OR REPLACE FUNCTION platform.read_only_reason() RETURNS text
LANGUAGE sql STABLE SET search_path = pg_catalog AS $$
  SELECT CASE WHEN t.status = 'Suspended' THEN 'suspended'
              WHEN t.status = 'Closed' THEN 'closed'
              WHEN p.is_trial AND s.trial_ends_at <= now() THEN 'trial-expired' END
  FROM platform.tenant t
  LEFT JOIN platform.subscription s ON s.tenant_id = t.id
  LEFT JOIN platform.plan p ON p.code = s.plan_code
  WHERE t.id = core.current_tenant()
$$;

GRANT USAGE ON SCHEMA platform TO rc_app, rc_worker, rc_reporting;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA platform TO rc_app;
GRANT SELECT ON ALL TABLES IN SCHEMA platform TO rc_worker, rc_reporting;
REVOKE INSERT, UPDATE ON platform.plan, platform.subscription FROM rc_app;   -- read-only for the application
-- TEN-004: suspension is a platform decision, so the application may edit only the settings of TenantUpdate (D07),
-- never status (a store could otherwise lift its own suspension).
REVOKE UPDATE ON platform.tenant FROM rc_app;
GRANT UPDATE (legal_name, time_zone, pricing_mode, version) ON platform.tenant TO rc_app;
GRANT EXECUTE ON FUNCTION platform.entitlement_cap(text), platform.read_only_reason() TO rc_app, rc_worker;
REVOKE UPDATE ON platform.export_job FROM rc_app;            -- the API only requests; the Workers run and complete
GRANT UPDATE (status, object_key, row_count, sha256, error, completed_at, expires_at, purged_at) ON platform.export_job TO rc_worker;
RESET ROLE;
