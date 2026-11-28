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

-- BR-086 / SAL-012 / D-32 (US-172): each industry's receipt template, defined by the platform and versioned. A version is
-- never changed: a new text is a new version, with who made it and why (the history is the record). Global reference
-- data: no tenant_id; the merchant API only reads it (the platform API writes new versions). The legal text of v1 is a
-- placeholder until the tax adviser's text arrives (X-05, G-05).
CREATE TABLE platform.industry_profile (
  code text PRIMARY KEY CHECK (code ~ '^[a-z]{2,30}$'),
  name text NOT NULL CHECK (char_length(btrim(name)) BETWEEN 1 AND 100)
);
INSERT INTO platform.industry_profile (code, name) VALUES ('supermarket', 'سوپرمارکت'), ('apparel', 'پوشاک'), ('general', 'عمومی');

CREATE TABLE platform.receipt_template_industry (
  industry_code text NOT NULL REFERENCES platform.industry_profile (code),
  version       int  NOT NULL CHECK (version >= 1),
  legal_text    text NOT NULL CHECK (char_length(btrim(legal_text)) BETWEEN 1 AND 2000),
  -- the fields every receipt of the industry must show; a store cannot hide them (DI-085)
  locked_fields text[] NOT NULL CHECK (locked_fields <@ ARRAY['legalName','economicCode','nationalId','businessLicenseNo','branchAddress',
                 'invoiceNo','issuedAt','cashier','lines','totals','tax','payments','qrCode']::text[] AND cardinality(locked_fields) >= 1),
  reason        text NOT NULL CHECK (char_length(btrim(reason)) BETWEEN 3 AND 500),
  created_by    text NOT NULL,                     -- the platform operator (issuer|subject); not a merchant user
  created_at    timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (industry_code, version)
);
CREATE TRIGGER tr_receipt_template_industry_append_only BEFORE UPDATE OR DELETE ON platform.receipt_template_industry
  FOR EACH ROW EXECUTE FUNCTION core.forbid_update_delete();
INSERT INTO platform.receipt_template_industry (industry_code, version, legal_text, locked_fields, reason, created_by)
SELECT code, 1, 'این فاکتور بر اساس قوانین مالیات بر ارزش افزوده صادر شده است. (متن نهایی پس از تأیید مشاور مالیاتی)',
       ARRAY['legalName','economicCode','invoiceNo','issuedAt','lines','totals','tax','qrCode'], 'نسخه اولیه (پیش‌نویس، X-05)', 'migration'
  FROM platform.industry_profile;

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

-- INT-001 (US-162): a partner's key to read this store's catalog or stock. The key is shown once; only the SHA-256 of its
-- secret is kept. Scopes are the MVP's read scopes; a key lasts at most a year and can be revoked at once.
CREATE TABLE platform.integration_key (
  tenant_id    uuid NOT NULL REFERENCES platform.tenant (id),
  id           uuid NOT NULL,
  prefix       text NOT NULL CHECK (prefix ~ '^[a-z0-9]{10}$'),        -- the public part, to find the key
  secret_hash  bytea NOT NULL CHECK (octet_length(secret_hash) = 32),
  name         text NOT NULL CHECK (char_length(btrim(name)) BETWEEN 1 AND 100),
  scopes       text[] NOT NULL CHECK (cardinality(scopes) >= 1 AND scopes <@ ARRAY['catalog.read','inventory.read']::text[]),
  created_by   uuid NOT NULL,
  created_at   timestamptz NOT NULL DEFAULT now(),
  expires_at   timestamptz NOT NULL,
  revoked_at   timestamptz,
  revoked_by   uuid,
  last_used_at timestamptz,
  PRIMARY KEY (tenant_id, id),
  CHECK (expires_at > created_at AND expires_at <= created_at + interval '366 days'),
  CHECK ((revoked_at IS NULL) = (revoked_by IS NULL))
);
CREATE UNIQUE INDEX ux_integration_key_prefix ON platform.integration_key (prefix);
CALL core.enable_tenant_rls('platform.integration_key');


-- Retention (BR-087, TEN-005.1, US-173). The platform sets a floor (minimum, the legal part) and a default per data
-- category; a policy version never changes (a new row is the next version, valid from a date). A store may keep data
-- LONGER than the default, never shorter than the floor (RC036). A legal hold stops deletion of a category for a store
-- until it ends. The figures seeded here are drafts until the tax adviser confirms the floors (X-05).
CREATE TABLE platform.retention_policy (
  category     text NOT NULL CHECK (category IN ('FinancialDocuments','TaxInvoices','InventoryLedger','Audit','CustomerCrm',
                                                  'CustomerIdentity','Notifications','TechnicalLogs')),
  version      int  NOT NULL CHECK (version >= 1),
  minimum_days int  NOT NULL CHECK (minimum_days BETWEEN 1 AND 36500),
  default_days int  NOT NULL CHECK (default_days BETWEEN 1 AND 36500),
  valid_from   timestamptz NOT NULL,
  reason       text NOT NULL CHECK (char_length(btrim(reason)) BETWEEN 3 AND 500),
  created_by   text NOT NULL,                      -- the platform operator (issuer|subject)
  created_at   timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (category, version),
  CHECK (default_days >= minimum_days)
);
CREATE TRIGGER tr_retention_policy_append_only BEFORE UPDATE OR DELETE ON platform.retention_policy
  FOR EACH ROW EXECUTE FUNCTION core.forbid_update_delete();
INSERT INTO platform.retention_policy (category, version, minimum_days, default_days, valid_from, reason, created_by) VALUES
  ('FinancialDocuments', 1, 3650, 3650, '2020-01-01', 'پیش‌فرض اولیه D05 §9 (کف قانونی منتظر X-05)', 'migration'),
  ('TaxInvoices',        1, 3650, 3650, '2020-01-01', 'پیش‌فرض اولیه D05 §9 (کف قانونی منتظر X-05)', 'migration'),
  ('InventoryLedger',    1, 3650, 3650, '2020-01-01', 'پیش‌فرض اولیه D05 §9 (کف قانونی منتظر X-05)', 'migration'),
  ('Audit',              1, 3650, 3650, '2020-01-01', 'پیش‌فرض اولیه D05 §9 (کف قانونی منتظر X-05)', 'migration'),
  ('CustomerCrm',        1,  730,  730, '2020-01-01', 'پیش‌فرض اولیه D05 §9: دو سال پس از آخرین فعالیت', 'migration'),
  ('CustomerIdentity',   1,   30,   30, '2020-01-01', 'پیش‌فرض اولیه D05 §9: درخواست حذف به‌علاوه ۳۰ روز', 'migration'),
  ('Notifications',      1,   90,  180, '2020-01-01', 'پیش‌فرض اولیه D05 §9: ۶ ماه', 'migration'),
  ('TechnicalLogs',      1,   30,   90, '2020-01-01', 'پیش‌فرض اولیه D05 §9: ۹۰ روز', 'migration');

-- the floor and default in force now for a category
CREATE FUNCTION platform.retention_floor(p_category text) RETURNS int LANGUAGE sql STABLE SET search_path = pg_catalog AS $$
  SELECT minimum_days FROM platform.retention_policy WHERE category = p_category AND valid_from <= now()
   ORDER BY valid_from DESC, version DESC LIMIT 1
$$;

-- A store's own choice: only longer than the floor. The row is the current choice; clearing it returns to the default.
CREATE TABLE platform.tenant_retention (
  tenant_id uuid NOT NULL REFERENCES platform.tenant (id),
  category  text NOT NULL CHECK (category IN ('FinancialDocuments','TaxInvoices','InventoryLedger','Audit','CustomerCrm',
                                               'CustomerIdentity','Notifications','TechnicalLogs')),
  days      int  NOT NULL CHECK (days BETWEEN 1 AND 36500),
  set_by    uuid NOT NULL,
  set_at    timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, category)
);
CALL core.enable_tenant_rls('platform.tenant_retention');
CREATE FUNCTION platform.tenant_retention_guard() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NEW.days < platform.retention_floor(NEW.category) THEN
    RAISE EXCEPTION 'retention of % cannot be shorter than the legal floor of % days', NEW.category, platform.retention_floor(NEW.category)
      USING ERRCODE = 'RC036';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER tr_tenant_retention_guard BEFORE INSERT OR UPDATE ON platform.tenant_retention
  FOR EACH ROW EXECUTE FUNCTION platform.tenant_retention_guard();

-- Legal hold: set and lifted by a platform operator (definer functions below), visible to the store it concerns.
CREATE TABLE platform.legal_hold (
  tenant_id  uuid NOT NULL REFERENCES platform.tenant (id),
  category   text NOT NULL CHECK (category IN ('FinancialDocuments','TaxInvoices','InventoryLedger','Audit','CustomerCrm',
                                                'CustomerIdentity','Notifications','TechnicalLogs')),
  reason     text NOT NULL CHECK (char_length(btrim(reason)) BETWEEN 3 AND 500),
  until      timestamptz,                          -- NULL: until an operator lifts it
  created_by text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, category)
);
CALL core.enable_tenant_rls('platform.legal_hold');

-- days to keep a category for the current store: its own longer choice, else the default; never below the floor
CREATE FUNCTION platform.effective_retention_days(p_category text) RETURNS int LANGUAGE sql STABLE SET search_path = pg_catalog AS $$
  SELECT greatest(platform.retention_floor(p_category),
         coalesce((SELECT days FROM platform.tenant_retention WHERE category = p_category),
                  (SELECT default_days FROM platform.retention_policy WHERE category = p_category AND valid_from <= now()
                    ORDER BY valid_from DESC, version DESC LIMIT 1)))
$$;
CREATE FUNCTION platform.legal_hold_active(p_category text) RETURNS boolean LANGUAGE sql STABLE SET search_path = pg_catalog AS $$
  SELECT EXISTS (SELECT 1 FROM platform.legal_hold WHERE category = p_category AND (until IS NULL OR until > now()))
$$;

CREATE FUNCTION platform.set_legal_hold(p_tenant uuid, p_category text, p_reason text, p_until timestamptz, p_by text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE v_prev text := coalesce(current_setting('app.tenant_id', true), '');
BEGIN
  PERFORM set_config('app.tenant_id', p_tenant::text, true);
  INSERT INTO platform.legal_hold (tenant_id, category, reason, until, created_by) VALUES (p_tenant, p_category, p_reason, p_until, p_by)
  ON CONFLICT (tenant_id, category) DO UPDATE SET reason = EXCLUDED.reason, until = EXCLUDED.until, created_by = EXCLUDED.created_by, created_at = now();
  PERFORM set_config('app.tenant_id', v_prev, true);
END $$;
CREATE FUNCTION platform.release_legal_hold(p_tenant uuid, p_category text) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE v_prev text := coalesce(current_setting('app.tenant_id', true), ''); v_n int;
BEGIN
  PERFORM set_config('app.tenant_id', p_tenant::text, true);
  DELETE FROM platform.legal_hold WHERE tenant_id = p_tenant AND category = p_category;
  GET DIAGNOSTICS v_n = ROW_COUNT;
  PERFORM set_config('app.tenant_id', v_prev, true);
  RETURN v_n > 0;
END $$;
REVOKE ALL ON FUNCTION platform.set_legal_hold(uuid, text, text, timestamptz, text), platform.release_legal_hold(uuid, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION platform.set_legal_hold(uuid, text, text, timestamptz, text), platform.release_legal_hold(uuid, text) TO rc_app;

-- The deleting side: a row of a category is deleted only after the store's effective retention and while no hold is in
-- force, whoever deletes it (RC036). Used as BEFORE DELETE trigger: (category, timestamp column).
CREATE FUNCTION platform.retention_delete_guard() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE v_at timestamptz := (to_jsonb(OLD) ->> TG_ARGV[1])::timestamptz;
BEGIN
  IF platform.legal_hold_active(TG_ARGV[0]) THEN
    RAISE EXCEPTION 'a legal hold on % is in force', TG_ARGV[0] USING ERRCODE = 'RC036';
  END IF;
  IF v_at > now() - make_interval(days => platform.effective_retention_days(TG_ARGV[0])) THEN
    RAISE EXCEPTION 'data of % is kept for % days', TG_ARGV[0], platform.effective_retention_days(TG_ARGV[0]) USING ERRCODE = 'RC036';
  END IF;
  RETURN OLD;
END $$;

-- what the nightly job did for a store: the evidence of a deletion (append-only)
CREATE TABLE platform.retention_run (
  tenant_id    uuid NOT NULL REFERENCES platform.tenant (id),
  id           uuid NOT NULL,
  category     text NOT NULL,
  outcome      text NOT NULL CHECK (outcome IN ('Purged','Held')),
  cutoff       timestamptz NOT NULL,               -- rows created before this were eligible
  deleted_rows int  NOT NULL CHECK (deleted_rows >= 0),
  ran_at       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  CHECK (outcome <> 'Held' OR deleted_rows = 0)
);
CREATE INDEX ix_retention_run_recent ON platform.retention_run (tenant_id, ran_at DESC);
CALL core.enable_tenant_rls('platform.retention_run');
CREATE TRIGGER tr_retention_run_append_only BEFORE UPDATE OR DELETE ON platform.retention_run
  FOR EACH ROW EXECUTE FUNCTION core.forbid_update_delete();

-- TEN-004 / US-171: a platform operator suspends, reactivates or closes a store. A store cannot change its own status (the
-- application has no UPDATE on it); the operator's call goes through this function, which keeps the history (who, why, from,
-- to). Closed is final. Returns 'ok', 'not-found' or 'invalid-transition'.
CREATE TABLE platform.tenant_status_change (
  tenant_id   uuid NOT NULL REFERENCES platform.tenant (id),
  id          uuid NOT NULL,
  from_status text NOT NULL,
  to_status   text NOT NULL CHECK (to_status IN ('Active','Suspended','Closed')),
  reason      text NOT NULL CHECK (char_length(btrim(reason)) BETWEEN 3 AND 500),
  changed_by  text NOT NULL,                       -- the platform operator (issuer|subject)
  changed_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  CHECK (from_status <> to_status)
);
CALL core.enable_tenant_rls('platform.tenant_status_change');
CREATE TRIGGER tr_tenant_status_change_append_only BEFORE UPDATE OR DELETE ON platform.tenant_status_change
  FOR EACH ROW EXECUTE FUNCTION core.forbid_update_delete();

CREATE FUNCTION platform.set_tenant_status(p_tenant uuid, p_status text, p_reason text, p_by text) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE v_prev text := coalesce(current_setting('app.tenant_id', true), ''); v_from text;
BEGIN
  PERFORM set_config('app.tenant_id', p_tenant::text, true);
  SELECT status INTO v_from FROM platform.tenant WHERE id = p_tenant FOR UPDATE;
  IF v_from IS NULL THEN
    PERFORM set_config('app.tenant_id', v_prev, true);
    RETURN 'not-found';
  END IF;
  IF v_from = 'Closed' OR v_from = p_status OR p_status NOT IN ('Active','Suspended','Closed') THEN
    PERFORM set_config('app.tenant_id', v_prev, true);
    RETURN 'invalid-transition';
  END IF;
  UPDATE platform.tenant SET status = p_status, version = version + 1 WHERE id = p_tenant;
  INSERT INTO platform.tenant_status_change (tenant_id, id, from_status, to_status, reason, changed_by)
    VALUES (p_tenant, gen_random_uuid(), v_from, p_status, p_reason, p_by);
  PERFORM set_config('app.tenant_id', v_prev, true);
  RETURN 'ok';
END $$;
REVOKE ALL ON FUNCTION platform.set_tenant_status(uuid, text, text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION platform.set_tenant_status(uuid, text, text, text) TO rc_app;

GRANT USAGE ON SCHEMA platform TO rc_app, rc_worker, rc_reporting;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA platform TO rc_app;
REVOKE INSERT, UPDATE ON platform.industry_profile FROM rc_app;   -- reference data: changed by migration only
REVOKE UPDATE ON platform.receipt_template_industry FROM rc_app;
REVOKE UPDATE ON platform.retention_policy FROM rc_app;            -- a policy version is only added
GRANT DELETE ON platform.tenant_retention TO rc_app;               -- a store returns to the default by clearing its choice
REVOKE INSERT, UPDATE ON platform.legal_hold FROM rc_app;          -- operators use set_legal_hold / release_legal_hold
REVOKE INSERT, UPDATE ON platform.retention_run FROM rc_app;       -- only the Workers record a run
REVOKE INSERT, UPDATE ON platform.tenant_status_change FROM rc_app;   -- operators use set_tenant_status
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

-- a partner request carries the key, not the store: this returns the store, scopes and secret hash of a key in force for
-- its public prefix — nothing else (the pattern of iam.invitation_tenant); the API compares the hash in constant time
CREATE OR REPLACE FUNCTION platform.integration_key_target(p_prefix text)
RETURNS TABLE (tenant_id uuid, key_id uuid, secret_hash bytea, scopes text[])
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
  SELECT k.tenant_id, k.id, k.secret_hash, k.scopes FROM platform.integration_key k
   WHERE k.prefix = p_prefix AND k.revoked_at IS NULL AND k.expires_at > now()
$$;
GRANT SELECT (tenant_id, id, prefix, secret_hash, scopes, expires_at, revoked_at) ON platform.integration_key TO rc_tenant_directory;
GRANT CREATE ON SCHEMA platform TO rc_tenant_directory;  -- a new owner needs CREATE on the schema; revoked at once (fitness 6)
ALTER FUNCTION platform.integration_key_target(text) OWNER TO rc_tenant_directory;
REVOKE CREATE ON SCHEMA platform FROM rc_tenant_directory;
REVOKE ALL ON FUNCTION platform.integration_key_target(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION platform.integration_key_target(text) TO rc_app;

-- US-171: the operator's list of stores (metadata only: no store data). Across stores, so it is owned by the tenant
-- directory with column grants only (fitness rule 5); optional p_only returns one store.
CREATE OR REPLACE FUNCTION platform.list_tenants(p_after uuid, p_limit int, p_only uuid DEFAULT NULL)
RETURNS TABLE (id uuid, legal_name text, status text, plan_code text, created_at timestamptz, branches bigint)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
  SELECT t.id, t.legal_name, t.status, s.plan_code, t.created_at,
         (SELECT count(*) FROM platform.branch b WHERE b.tenant_id = t.id AND b.status = 'Active')
    FROM platform.tenant t LEFT JOIN platform.subscription s ON s.tenant_id = t.id
   WHERE (p_only IS NULL OR t.id = p_only) AND (p_after IS NULL OR t.id > p_after)
   ORDER BY t.id LIMIT greatest(least(p_limit, 200), 1)
$$;
GRANT SELECT (id, status, legal_name, created_at) ON platform.tenant TO rc_tenant_directory;
GRANT SELECT (tenant_id, plan_code) ON platform.subscription TO rc_tenant_directory;
GRANT SELECT (tenant_id, status) ON platform.branch TO rc_tenant_directory;
GRANT CREATE ON SCHEMA platform TO rc_tenant_directory;  -- a new owner needs CREATE on the schema; revoked at once (fitness 6)
ALTER FUNCTION platform.list_tenants(uuid, int, uuid) OWNER TO rc_tenant_directory;
REVOKE CREATE ON SCHEMA platform FROM rc_tenant_directory;
REVOKE ALL ON FUNCTION platform.list_tenants(uuid, int, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION platform.list_tenants(uuid, int, uuid) TO rc_app;
