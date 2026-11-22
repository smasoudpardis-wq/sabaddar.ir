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

GRANT USAGE ON SCHEMA platform TO rc_app, rc_worker, rc_reporting;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA platform TO rc_app;
GRANT SELECT ON ALL TABLES IN SCHEMA platform TO rc_worker, rc_reporting;
RESET ROLE;
