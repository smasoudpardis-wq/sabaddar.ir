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

GRANT USAGE ON SCHEMA platform TO rc_app, rc_worker, rc_reporting;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA platform TO rc_app;
GRANT SELECT ON ALL TABLES IN SCHEMA platform TO rc_worker, rc_reporting;
RESET ROLE;
