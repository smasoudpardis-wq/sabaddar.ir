SET ROLE rc_migrator;

CREATE TABLE acc.account (
  tenant_id       uuid NOT NULL,
  id              uuid NOT NULL,
  code            text NOT NULL,
  name            text NOT NULL,
  account_type    text NOT NULL CHECK (account_type IN ('Asset','Liability','Equity','Revenue','Expense')),
  parent_id       uuid,
  is_system       boolean NOT NULL DEFAULT false,
  requires_detail boolean NOT NULL DEFAULT false,
  is_postable     boolean NOT NULL DEFAULT true,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code),
  FOREIGN KEY (tenant_id, parent_id) REFERENCES acc.account (tenant_id, id),
  CHECK (length(btrim(name)) BETWEEN 1 AND 200),
  CHECK (code ~ '^[0-9]{1,12}$')
);
CALL core.enable_tenant_rls('acc.account');

-- ACC-001 / AT-100: system accounts can only be renamed; any account keeps its code and type once it has lines.
CREATE OR REPLACE FUNCTION acc.guard_account() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF OLD.is_system AND (to_jsonb(NEW) - 'name') <> (to_jsonb(OLD) - 'name') THEN
    RAISE EXCEPTION 'system account % can only be renamed', OLD.code USING ERRCODE = 'RC018';
  END IF;
  IF (NEW.code, NEW.account_type) IS DISTINCT FROM (OLD.code, OLD.account_type)
     AND EXISTS (SELECT 1 FROM acc.journal_line l WHERE l.tenant_id = OLD.tenant_id AND l.account_id = OLD.id) THEN
    RAISE EXCEPTION 'account % has postings: code and type are fixed', OLD.code USING ERRCODE = 'RC018';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER tr_account_guard BEFORE UPDATE ON acc.account FOR EACH ROW EXECUTE FUNCTION acc.guard_account();

-- Account roles (D-30, D-59): posting rules reference roles; each tenant maps a role to one of its accounts.
CREATE TABLE acc.account_role (
  tenant_id  uuid NOT NULL,
  role       text NOT NULL CHECK (role IN ('SalesRevenue','OutputVat','Inventory','COGS','CashOnHand','Bank',
               'GatewayInTransit','CardInTransit','TradeReceivables','CustomerAdvances','TradePayables','InputVat',
               'InventoryShrinkage','InventoryOverage','CashShortage','CashOverage','BadDebtExpense',
               'InventoryCostAdjustment','OpeningBalanceEquity','BankFees','PurchaseReturnVariance')),
  account_id uuid NOT NULL,
  PRIMARY KEY (tenant_id, role),
  FOREIGN KEY (tenant_id, account_id) REFERENCES acc.account (tenant_id, id)
);
CALL core.enable_tenant_rls('acc.account_role');

CREATE TABLE acc.fiscal_period (
  tenant_id   uuid NOT NULL,
  id          uuid NOT NULL,
  fiscal_year int  NOT NULL,        -- Jalali year, e.g. 1405
  period_no   int  NOT NULL CHECK (period_no BETWEEN 1 AND 12),
  span        tstzrange NOT NULL CHECK (NOT isempty(span) AND lower_inc(span) AND NOT upper_inc(span)),
  status      text NOT NULL DEFAULT 'Open' CHECK (status IN ('Open','Closed','Locked')),
  closed_at   timestamptz,                                       -- ACC-009 (US-113): set once, when the period closes
  closed_by   uuid,
  version     int  NOT NULL DEFAULT 1,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, fiscal_year, period_no),
  EXCLUDE USING gist (tenant_id WITH =, span WITH &&),          -- periods never overlap
  CHECK ((status = 'Open') = (closed_at IS NULL))
);
CALL core.enable_tenant_rls('acc.fiscal_period');

-- ACC-009 / D-45 / AB-19: a period only moves forward, Open → Closed → Locked; it is never reopened in the MVP and its
-- span, year and number never change. A period closed without a time (an operator, a test) is stamped now. The roles
-- have no DELETE on periods.
CREATE OR REPLACE FUNCTION acc.period_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'Open' AND NEW.closed_at IS NULL THEN NEW.closed_at := now(); END IF;
    RETURN NEW;
  END IF;
  IF (NEW.span, NEW.fiscal_year, NEW.period_no) IS DISTINCT FROM (OLD.span, OLD.fiscal_year, OLD.period_no)
     OR NOT ((OLD.status = NEW.status AND NEW.closed_at IS NOT DISTINCT FROM OLD.closed_at)
             OR (OLD.status = 'Open' AND NEW.status = 'Closed') OR (OLD.status = 'Closed' AND NEW.status = 'Locked'))
     OR (OLD.status <> 'Open' AND NEW.closed_at IS DISTINCT FROM OLD.closed_at) THEN
    RAISE EXCEPTION 'fiscal period % moves only Open -> Closed -> Locked (D-45)', OLD.id USING ERRCODE = 'RC033';
  END IF;
  IF OLD.status = 'Open' AND NEW.status = 'Closed' AND NEW.closed_at IS NULL THEN NEW.closed_at := now(); END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER tr_period_guard BEFORE INSERT OR UPDATE ON acc.fiscal_period
  FOR EACH ROW EXECUTE FUNCTION acc.period_guard();

CREATE TABLE acc.journal_entry (
  tenant_id    uuid NOT NULL,
  id           uuid NOT NULL,
  status       text NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft','Posted','Reversed')),
  effective_at timestamptz NOT NULL,
  period_id    uuid NOT NULL,
  description  text,
  source_type  text,
  source_id    uuid,
  purpose      text,                 -- e.g. 'revenue', 'cogs'
  rule_version int,
  reversal_of  uuid,
  reversed_by  uuid,
  posted_at    timestamptz,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, period_id)   REFERENCES acc.fiscal_period (tenant_id, id),
  FOREIGN KEY (tenant_id, reversal_of) REFERENCES acc.journal_entry (tenant_id, id),
  CHECK (status = 'Draft' OR posted_at IS NOT NULL),
  CHECK ((source_type IS NULL) = (source_id IS NULL))
);
CREATE UNIQUE INDEX ux_journal_source ON acc.journal_entry (tenant_id, source_type, source_id, purpose)
  WHERE source_type IS NOT NULL;                                     -- DI-044
CREATE UNIQUE INDEX ux_journal_one_reversal ON acc.journal_entry (tenant_id, reversal_of)
  WHERE reversal_of IS NOT NULL;                                     -- DI-045
CALL core.enable_tenant_rls('acc.journal_entry');

CREATE TABLE acc.journal_line (
  tenant_id   uuid NOT NULL,
  entry_id    uuid NOT NULL,
  line_no     int  NOT NULL,
  account_id  uuid NOT NULL,
  detail_type text,                  -- 'Customer' | 'Supplier' | 'Branch'
  detail_id   uuid,
  debit       bigint NOT NULL DEFAULT 0,
  credit      bigint NOT NULL DEFAULT 0,
  description text CHECK (length(description) <= 200),
  PRIMARY KEY (tenant_id, entry_id, line_no),
  FOREIGN KEY (tenant_id, entry_id)   REFERENCES acc.journal_entry (tenant_id, id),
  FOREIGN KEY (tenant_id, account_id) REFERENCES acc.account (tenant_id, id),   -- DI-046
  CONSTRAINT ck_one_side CHECK ((debit > 0 AND credit = 0) OR (credit > 0 AND debit = 0)),
  CHECK ((detail_type IS NULL) = (detail_id IS NULL))
);
CREATE INDEX ix_journal_line_account ON acc.journal_line (tenant_id, account_id);
CALL core.enable_tenant_rls('acc.journal_line');

-- Posting protocol: INSERT entry as Draft -> INSERT lines -> UPDATE status='Posted' (same tx).
-- DI-040: deferred check at COMMIT that every Posted entry is balanced with >= 2 lines
-- and that detail-required accounts carry a detail (DI-043) and the period is Open (DI-042).
CREATE OR REPLACE FUNCTION acc.check_entry() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  v_tenant uuid := COALESCE(NEW.tenant_id, OLD.tenant_id);
  -- fields read via jsonb because NEW/OLD have different shapes for the two tables
  v_row    jsonb := to_jsonb(COALESCE(NEW, OLD));
  v_entry  uuid := (CASE WHEN TG_TABLE_NAME = 'journal_entry' THEN v_row->>'id' ELSE v_row->>'entry_id' END)::uuid;
  v_status text; v_period_status text; v_eff timestamptz; v_span tstzrange;
  v_dr bigint; v_cr bigint; v_n int; v_missing_detail int;
BEGIN
  SELECT e.status, p.status, e.effective_at, p.span INTO v_status, v_period_status, v_eff, v_span
    FROM acc.journal_entry e JOIN acc.fiscal_period p ON p.tenant_id = e.tenant_id AND p.id = e.period_id
   WHERE e.tenant_id = v_tenant AND e.id = v_entry;
  IF v_status IS DISTINCT FROM 'Posted' THEN RETURN NULL; END IF;

  SELECT COALESCE(sum(debit),0), COALESCE(sum(credit),0), count(*),
         count(*) FILTER (WHERE a.requires_detail AND l.detail_id IS NULL)
    INTO v_dr, v_cr, v_n, v_missing_detail
    FROM acc.journal_line l JOIN acc.account a ON a.tenant_id = l.tenant_id AND a.id = l.account_id
   WHERE l.tenant_id = v_tenant AND l.entry_id = v_entry;

  IF v_n < 2 OR v_dr <> v_cr THEN
    RAISE EXCEPTION 'journal % unbalanced or too few lines (dr=%, cr=%, n=%)', v_entry, v_dr, v_cr, v_n
      USING ERRCODE = 'RC010';
  END IF;
  IF v_missing_detail > 0 THEN
    RAISE EXCEPTION 'journal % has lines without required detail account', v_entry USING ERRCODE = 'RC011';
  END IF;
  RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER tr_entry_balanced AFTER INSERT OR UPDATE ON acc.journal_entry
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION acc.check_entry();
CREATE CONSTRAINT TRIGGER tr_line_balanced AFTER INSERT OR UPDATE OR DELETE ON acc.journal_line
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION acc.check_entry();

-- DI-041/042: immutability of Posted entries; posting only into Open periods. SECURITY DEFINER: the period row is
-- locked FOR SHARE (so it cannot close mid-posting), which needs UPDATE rights the Workers must not have; the owner is
-- still bound by FORCE RLS, so only the context tenant's period is visible.
CREATE OR REPLACE FUNCTION acc.guard_entry() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE v_period_status text; v_span tstzrange;
BEGIN
  IF TG_OP = 'DELETE' THEN
    IF OLD.status <> 'Draft' THEN RAISE EXCEPTION 'posted journal cannot be deleted' USING ERRCODE = 'RC012'; END IF;
    RETURN OLD;
  END IF;
  IF TG_OP = 'UPDATE' AND OLD.status IN ('Posted','Reversed') THEN
    IF NOT (OLD.status = 'Posted' AND NEW.status = 'Reversed' AND OLD.reversed_by IS NULL AND NEW.reversed_by IS NOT NULL
            AND (to_jsonb(NEW) - ARRAY['status','reversed_by']) = (to_jsonb(OLD) - ARRAY['status','reversed_by'])) THEN
      RAISE EXCEPTION 'posted journal % is immutable (use a reversal)', OLD.id USING ERRCODE = 'RC012';
    END IF;
    RETURN NEW;
  END IF;
  IF NEW.status = 'Posted' THEN
    SELECT status, span INTO v_period_status, v_span FROM acc.fiscal_period
     WHERE tenant_id = NEW.tenant_id AND id = NEW.period_id FOR SHARE;
    IF v_period_status <> 'Open' THEN
      RAISE EXCEPTION 'period is % — posting not allowed', v_period_status USING ERRCODE = 'RC013';
    END IF;
    IF NOT (NEW.effective_at <@ v_span) THEN
      RAISE EXCEPTION 'effective_at outside the period span' USING ERRCODE = 'RC014';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER tr_entry_guard BEFORE INSERT OR UPDATE OR DELETE ON acc.journal_entry
  FOR EACH ROW EXECUTE FUNCTION acc.guard_entry();

CREATE OR REPLACE FUNCTION acc.guard_line() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_status text;
BEGIN
  SELECT status INTO v_status FROM acc.journal_entry
   WHERE tenant_id = COALESCE(NEW.tenant_id, OLD.tenant_id) AND id = COALESCE(NEW.entry_id, OLD.entry_id);
  IF v_status IN ('Posted','Reversed') THEN
    RAISE EXCEPTION 'lines of a posted journal are immutable' USING ERRCODE = 'RC012';
  END IF;
  RETURN COALESCE(NEW, OLD);
END $$;
CREATE TRIGGER tr_line_guard BEFORE INSERT OR UPDATE OR DELETE ON acc.journal_line
  FOR EACH ROW EXECUTE FUNCTION acc.guard_line();

GRANT USAGE ON SCHEMA acc TO rc_app, rc_worker, rc_reporting;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA acc TO rc_app;
GRANT DELETE ON acc.journal_entry, acc.journal_line TO rc_app;   -- drafts only (guarded)
GRANT SELECT ON ALL TABLES IN SCHEMA acc TO rc_worker, rc_reporting;
-- Tenant provisioning consumer (US-011): default chart, role mapping and fiscal periods.
GRANT INSERT ON acc.account, acc.account_role, acc.fiscal_period TO rc_worker;
-- Automatic posting (ACC-004, PostingRule consumers in the Workers): draft -> lines -> Posted, never edits or deletes.
GRANT INSERT ON acc.journal_entry, acc.journal_line TO rc_worker;
GRANT UPDATE (status, posted_at) ON acc.journal_entry TO rc_worker;
RESET ROLE;
