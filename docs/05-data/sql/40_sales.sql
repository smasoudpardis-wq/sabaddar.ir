SET ROLE rc_migrator;

CREATE TABLE sales.shift (
  tenant_id    uuid NOT NULL,
  id           uuid NOT NULL,
  register_id  uuid NOT NULL,
  branch_id    uuid NOT NULL,                       -- the register's branch (set by sales.shift_register), for scope checks
  cashier_id   uuid NOT NULL,
  status       text NOT NULL DEFAULT 'Open' CHECK (status IN ('Open','Closing','Closed')),
  opening_cash bigint NOT NULL CHECK (opening_cash >= 0),
  expected_cash bigint NOT NULL DEFAULT 0,
  counted_cash bigint CHECK (counted_cash >= 0),
  variance     bigint,                                -- counted − expected (UC-06): negative = shortage
  variance_reason text CHECK (length(variance_reason) <= 500),
  variance_status text NOT NULL DEFAULT 'None' CHECK (variance_status IN ('None','PendingApproval','Approved','Rejected')),
  variance_decided_by uuid,
  variance_decided_at timestamptz,
  opened_at    timestamptz NOT NULL DEFAULT now(),
  closed_at    timestamptz,
  closed_by    uuid,
  version      int NOT NULL DEFAULT 1,
  PRIMARY KEY (tenant_id, id),
  CHECK (status <> 'Closed' OR (counted_cash IS NOT NULL AND closed_at IS NOT NULL AND closed_by IS NOT NULL
                                AND variance = counted_cash - expected_cash)),
  CHECK (variance_status = 'None' OR status = 'Closed'),
  CHECK ((variance_status IN ('Approved','Rejected')) = (variance_decided_by IS NOT NULL AND variance_decided_at IS NOT NULL)),
  CHECK (variance_decided_by IS NULL OR variance_decided_by <> cashier_id)               -- SoD-01: not one's own shortage
);

-- The register decides the branch; a register of another tenant (or none) cannot be used.
CREATE OR REPLACE FUNCTION sales.shift_register() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  SELECT r.branch_id INTO NEW.branch_id FROM platform.register r WHERE r.tenant_id = NEW.tenant_id AND r.id = NEW.register_id;
  IF NEW.branch_id IS NULL THEN
    RAISE EXCEPTION 'register % not found', NEW.register_id USING ERRCODE = '23503';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER shift_register BEFORE INSERT ON sales.shift FOR EACH ROW EXECUTE FUNCTION sales.shift_register();

-- UC-06 / §6.6: Open → Closed (Closing is reserved for an asynchronous count); after closing only the variance
-- decision may change, once. Who opened it, the opening cash and the count are facts.
CREATE OR REPLACE FUNCTION sales.shift_transition() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF (NEW.tenant_id, NEW.register_id, NEW.branch_id, NEW.cashier_id, NEW.opening_cash, NEW.opened_at)
     IS DISTINCT FROM (OLD.tenant_id, OLD.register_id, OLD.branch_id, OLD.cashier_id, OLD.opening_cash, OLD.opened_at) THEN
    RAISE EXCEPTION 'shift % facts are immutable', OLD.id USING ERRCODE = 'RC017';
  END IF;
  IF OLD.status = 'Closed' AND (
       (NEW.status, NEW.expected_cash, NEW.counted_cash, NEW.variance, NEW.variance_reason, NEW.closed_at, NEW.closed_by)
       IS DISTINCT FROM (OLD.status, OLD.expected_cash, OLD.counted_cash, OLD.variance, OLD.variance_reason, OLD.closed_at, OLD.closed_by)
       OR NOT (NEW.variance_status = OLD.variance_status OR (OLD.variance_status = 'PendingApproval' AND NEW.variance_status IN ('Approved','Rejected')))
       OR (OLD.variance_status <> 'PendingApproval' AND (NEW.variance_decided_by, NEW.variance_decided_at) IS DISTINCT FROM (OLD.variance_decided_by, OLD.variance_decided_at))) THEN
    RAISE EXCEPTION 'closed shift % can only receive its variance decision', OLD.id USING ERRCODE = 'RC017';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER shift_transition BEFORE UPDATE ON sales.shift FOR EACH ROW EXECUTE FUNCTION sales.shift_transition();
-- DI-008: at most one non-closed shift per register.
CREATE UNIQUE INDEX ux_shift_one_open ON sales.shift (tenant_id, register_id) WHERE status <> 'Closed';
CALL core.enable_tenant_rls('sales.shift');

-- UC-06: cash put into or taken out of the drawer during a shift, with a reason; append-only, only while the shift is open.
CREATE TABLE sales.cash_movement (
  tenant_id   uuid NOT NULL,
  id          uuid NOT NULL,
  shift_id    uuid NOT NULL,
  direction   text NOT NULL CHECK (direction IN ('In','Out')),
  amount      bigint NOT NULL CHECK (amount > 0),
  reason      text NOT NULL CHECK (length(btrim(reason)) BETWEEN 3 AND 200),
  recorded_by uuid NOT NULL,
  recorded_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, shift_id) REFERENCES sales.shift (tenant_id, id)
);
CALL core.enable_tenant_rls('sales.cash_movement');

CREATE OR REPLACE FUNCTION sales.cash_movement_open_shift() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  PERFORM 1 FROM sales.shift WHERE tenant_id = NEW.tenant_id AND id = NEW.shift_id AND status = 'Open' FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'shift % is not open', NEW.shift_id USING ERRCODE = 'RC017';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER cash_movement_open_shift BEFORE INSERT ON sales.cash_movement FOR EACH ROW EXECUTE FUNCTION sales.cash_movement_open_shift();


CREATE TABLE sales.invoice_sequence (
  tenant_id   uuid NOT NULL,
  branch_id   uuid NOT NULL,
  fiscal_year int  NOT NULL,
  next_value  bigint NOT NULL DEFAULT 1 CHECK (next_value >= 1),
  PRIMARY KEY (tenant_id, branch_id, fiscal_year)
);
CALL core.enable_tenant_rls('sales.invoice_sequence');

CREATE TABLE sales.sale (
  tenant_id       uuid NOT NULL,
  id              uuid NOT NULL,
  branch_id       uuid NOT NULL,
  register_id     uuid NOT NULL,
  shift_id        uuid NOT NULL,
  cashier_id      uuid NOT NULL,
  terminal_id     uuid NOT NULL,
  client_draft_id text NOT NULL,
  status          text NOT NULL DEFAULT 'Draft'
                  CHECK (status IN ('Draft','Held','PendingPayment','Finalized','Cancelled')),
  return_status   text NOT NULL DEFAULT 'None' CHECK (return_status IN ('None','Partial','Full')),
  pricing_mode    text NOT NULL CHECK (pricing_mode IN ('TaxExclusive','TaxInclusive')),
  tenant_customer_id uuid,                -- TenantCustomerRef only (DI-064)
  fiscal_year     int,
  invoice_no      text,
  total           bigint NOT NULL DEFAULT 0,
  finalized_at    timestamptz,
  last_activity_at timestamptz NOT NULL DEFAULT now(),
  version         int NOT NULL DEFAULT 1,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, client_draft_id),                                  -- DI-010
  FOREIGN KEY (tenant_id, shift_id) REFERENCES sales.shift (tenant_id, id),
  CHECK (status <> 'Finalized' OR (invoice_no IS NOT NULL AND finalized_at IS NOT NULL AND fiscal_year IS NOT NULL)),
  CHECK (return_status = 'None' OR status = 'Finalized')
);
CREATE UNIQUE INDEX ux_sale_invoice_no ON sales.sale (tenant_id, branch_id, fiscal_year, invoice_no)
  WHERE invoice_no IS NOT NULL;                                         -- DI-009
CREATE INDEX ix_sale_open ON sales.sale (tenant_id, status, last_activity_at)
  WHERE status IN ('Draft','Held','PendingPayment');                    -- PM-06
CREATE INDEX ix_sale_customer ON sales.sale (tenant_id, tenant_customer_id, finalized_at)
  WHERE tenant_customer_id IS NOT NULL;
CALL core.enable_tenant_rls('sales.sale');

CREATE TABLE sales.sale_line (
  tenant_id    uuid NOT NULL,
  sale_id      uuid NOT NULL,
  line_no      int  NOT NULL CHECK (line_no > 0),
  variant_id   uuid NOT NULL,
  qty          numeric(18,6) NOT NULL CHECK (qty > 0),
  returned_qty numeric(18,6) NOT NULL DEFAULT 0,
  unit_price   bigint NOT NULL CHECK (unit_price >= 0),
  discount     bigint NOT NULL DEFAULT 0 CHECK (discount >= 0),
  net          bigint NOT NULL CHECK (net >= 0),
  tax_rate_bp  int    NOT NULL CHECK (tax_rate_bp BETWEEN 0 AND 10000),
  tax          bigint NOT NULL CHECK (tax >= 0),
  line_total   bigint NOT NULL,
  cost_at_sale bigint,                     -- set at Finalize (WAC × qty)
  snapshot     jsonb,                      -- SaleLineSnapshot (SAL-005), set at Finalize
  PRIMARY KEY (tenant_id, sale_id, line_no),
  FOREIGN KEY (tenant_id, sale_id) REFERENCES sales.sale (tenant_id, id),
  CONSTRAINT ck_line_total CHECK (line_total = net + tax),                       -- DI-001 (line level)
  CONSTRAINT ck_returned_qty CHECK (returned_qty >= 0 AND returned_qty <= qty)   -- DI-005
);
CALL core.enable_tenant_rls('sales.sale_line');

-- DI-003: a Finalized sale is immutable except return bookkeeping.
CREATE OR REPLACE FUNCTION sales.guard_finalized_sale() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    IF OLD.status = 'Finalized' THEN RAISE EXCEPTION 'finalized sale cannot be deleted' USING ERRCODE = 'RC002'; END IF;
    RETURN OLD;
  END IF;
  IF OLD.status = 'Finalized' THEN
    IF (to_jsonb(NEW) - ARRAY['return_status','version','last_activity_at'])
       IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['return_status','version','last_activity_at']) THEN
      RAISE EXCEPTION 'finalized sale % is immutable', OLD.id USING ERRCODE = 'RC002';
    END IF;
  ELSIF OLD.status = 'Cancelled' AND NEW IS DISTINCT FROM OLD THEN
    RAISE EXCEPTION 'cancelled sale % is immutable', OLD.id USING ERRCODE = 'RC002';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER tr_sale_guard BEFORE UPDATE OR DELETE ON sales.sale
  FOR EACH ROW EXECUTE FUNCTION sales.guard_finalized_sale();

CREATE OR REPLACE FUNCTION sales.guard_finalized_line() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_status text;
BEGIN
  SELECT status INTO v_status FROM sales.sale
   WHERE tenant_id = COALESCE(OLD.tenant_id, NEW.tenant_id) AND id = COALESCE(OLD.sale_id, NEW.sale_id);
  IF v_status = 'Finalized' THEN
    IF TG_OP <> 'UPDATE'
       OR (to_jsonb(NEW) - 'returned_qty') IS DISTINCT FROM (to_jsonb(OLD) - 'returned_qty')
       OR NEW.returned_qty < OLD.returned_qty THEN
      RAISE EXCEPTION 'lines of finalized sale are immutable (only returned_qty may increase)' USING ERRCODE = 'RC002';
    END IF;
  END IF;
  RETURN COALESCE(NEW, OLD);
END $$;
CREATE TRIGGER tr_sale_line_guard BEFORE INSERT OR UPDATE OR DELETE ON sales.sale_line
  FOR EACH ROW EXECUTE FUNCTION sales.guard_finalized_line();

CREATE TABLE sales.sale_tender (
  tenant_id  uuid NOT NULL,
  sale_id    uuid NOT NULL,
  seq        int  NOT NULL,
  method     text NOT NULL CHECK (method IN ('Cash','ExternalCardTerminal','OnlineGateway','CustomerCredit','StoreCredit')),
  amount     bigint NOT NULL CHECK (amount > 0),
  payment_id uuid,                       -- Payments ref; NULL for CustomerCredit (D-28)
  PRIMARY KEY (tenant_id, sale_id, seq),
  FOREIGN KEY (tenant_id, sale_id) REFERENCES sales.sale (tenant_id, id),
  CHECK ((method = 'CustomerCredit') = (payment_id IS NULL) OR method = 'StoreCredit')
);
CALL core.enable_tenant_rls('sales.sale_tender');

-- Idempotency records live in core.idempotency_record (shared by every module, US-006).

-- Integration events go to core.outbox (shared transactional outbox, US-005 / ADR-013).

-- UC-06: expected cash = opening + cash tendered on finalised sales of the shift + cash in − cash out (cash refunds join
-- here with SaleReturn). Computed in the database at close, so the cashier cannot supply it.
CREATE OR REPLACE FUNCTION sales.shift_expected_cash(p_shift uuid) RETURNS bigint LANGUAGE sql STABLE SET search_path = pg_catalog AS $$
  SELECT sh.opening_cash
       + coalesce((SELECT sum(t.amount) FROM sales.sale s JOIN sales.sale_tender t ON t.tenant_id = s.tenant_id AND t.sale_id = s.id
                   WHERE s.tenant_id = sh.tenant_id AND s.shift_id = sh.id AND s.status = 'Finalized' AND t.method = 'Cash'), 0)
       + coalesce((SELECT sum(CASE m.direction WHEN 'In' THEN m.amount ELSE -m.amount END) FROM sales.cash_movement m
                   WHERE m.tenant_id = sh.tenant_id AND m.shift_id = sh.id), 0)
  FROM sales.shift sh WHERE sh.id = p_shift AND sh.tenant_id = core.current_tenant()
$$;

GRANT USAGE ON SCHEMA sales TO rc_app, rc_worker, rc_reporting;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA sales TO rc_app;
GRANT DELETE ON sales.sale_line TO rc_app;            -- draft line removal (guard blocks finalized)
GRANT SELECT ON sales.sale, sales.sale_line, sales.sale_tender, sales.shift TO rc_worker, rc_reporting;   -- shift: data export (TEN-005)
GRANT SELECT ON sales.cash_movement TO rc_worker, rc_reporting;
REVOKE UPDATE ON sales.cash_movement FROM rc_app;          -- append-only (UC-06)
GRANT EXECUTE ON FUNCTION sales.shift_expected_cash(uuid) TO rc_app, rc_worker;
RESET ROLE;
