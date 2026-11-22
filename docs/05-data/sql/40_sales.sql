SET ROLE rc_migrator;

CREATE TABLE sales.shift (
  tenant_id    uuid NOT NULL,
  id           uuid NOT NULL,
  register_id  uuid NOT NULL,
  cashier_id   uuid NOT NULL,
  status       text NOT NULL DEFAULT 'Open' CHECK (status IN ('Open','Closing','Closed')),
  opening_cash bigint NOT NULL CHECK (opening_cash >= 0),
  expected_cash bigint NOT NULL DEFAULT 0,
  counted_cash bigint,
  opened_at    timestamptz NOT NULL DEFAULT now(),
  closed_at    timestamptz,
  version      int NOT NULL DEFAULT 1,
  PRIMARY KEY (tenant_id, id),
  CHECK (status <> 'Closed' OR (counted_cash IS NOT NULL AND closed_at IS NOT NULL))
);
-- DI-008: at most one non-closed shift per register.
CREATE UNIQUE INDEX ux_shift_one_open ON sales.shift (tenant_id, register_id) WHERE status <> 'Closed';
CALL core.enable_tenant_rls('sales.shift');

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

CREATE TABLE sales.idempotency_record (
  tenant_id    uuid NOT NULL,
  key          text NOT NULL CHECK (length(key) BETWEEN 16 AND 64),
  operation    text NOT NULL,
  payload_hash bytea NOT NULL,
  status       text NOT NULL CHECK (status IN ('InProgress','Completed')),
  response     jsonb,
  created_at   timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, key)
);
CALL core.enable_tenant_rls('sales.idempotency_record');

CREATE TABLE sales.outbox (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  event_type    text NOT NULL,
  event_version int  NOT NULL,
  aggregate_id  uuid NOT NULL,
  payload       jsonb NOT NULL,
  occurred_at   timestamptz NOT NULL DEFAULT now(),
  published_at  timestamptz,
  attempts      int NOT NULL DEFAULT 0,
  next_attempt_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id)
);
CREATE INDEX ix_outbox_pending ON sales.outbox (next_attempt_at, id) WHERE published_at IS NULL;
CALL core.enable_tenant_rls('sales.outbox');

GRANT USAGE ON SCHEMA sales TO rc_app, rc_worker, rc_reporting;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA sales TO rc_app;
GRANT DELETE ON sales.sale_line TO rc_app;            -- draft line removal (guard blocks finalized)
GRANT SELECT, UPDATE ON sales.outbox TO rc_worker;
GRANT SELECT ON sales.sale, sales.sale_line, sales.sale_tender TO rc_worker, rc_reporting;
RESET ROLE;
