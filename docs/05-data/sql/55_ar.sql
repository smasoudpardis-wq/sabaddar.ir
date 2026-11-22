SET ROLE rc_migrator;

CREATE TABLE ar.credit_account (
  tenant_id          uuid NOT NULL,
  id                 uuid NOT NULL,
  tenant_customer_id uuid NOT NULL,
  credit_identity_enc bytea NOT NULL,            -- national id / economic code, envelope-encrypted (BR-080)
  credit_limit       bigint NOT NULL CHECK (credit_limit >= 0),
  payment_term_days  int    NOT NULL DEFAULT 30 CHECK (payment_term_days BETWEEN 0 AND 365),
  block_on_overdue   boolean NOT NULL DEFAULT true,
  status             text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Suspended','Closed')),
  outstanding        bigint NOT NULL DEFAULT 0,  -- projection of ar.receivable_entry (DI-050)
  version            bigint NOT NULL DEFAULT 0,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, tenant_customer_id)
);
CALL core.enable_tenant_rls('ar.credit_account');

CREATE TABLE ar.receivable_entry (
  tenant_id   uuid NOT NULL,
  id          uuid NOT NULL,
  account_id  uuid NOT NULL,
  entry_type  text NOT NULL CHECK (entry_type IN ('Charge','Collection','CreditNote','WriteOff','Adjustment')),
  amount      bigint NOT NULL CHECK (amount > 0),
  sign        smallint NOT NULL CHECK (sign IN (-1, 1)),   -- +1 increases outstanding
  due_date    date,
  open_amount bigint,                                      -- Charge only
  source_type text NOT NULL,
  source_id   uuid NOT NULL,
  recorded_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, source_type, source_id, entry_type),  -- BR-032
  FOREIGN KEY (tenant_id, account_id) REFERENCES ar.credit_account (tenant_id, id),
  CHECK (entry_type <> 'Charge' OR (sign = 1 AND due_date IS NOT NULL AND open_amount BETWEEN 0 AND amount)), -- DI-053
  CHECK (entry_type = 'Charge' OR open_amount IS NULL)
);
CREATE INDEX ix_ar_open_charges ON ar.receivable_entry (tenant_id, account_id, due_date)
  WHERE entry_type = 'Charge' AND open_amount > 0;
CALL core.enable_tenant_rls('ar.receivable_entry');

-- Append-only except decreasing open_amount of a Charge (allocation).
CREATE OR REPLACE FUNCTION ar.guard_entry() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE'
     OR (to_jsonb(NEW) - 'open_amount') IS DISTINCT FROM (to_jsonb(OLD) - 'open_amount')
     OR NEW.open_amount > OLD.open_amount THEN
    RAISE EXCEPTION 'receivable entries are append-only (only open_amount may decrease)' USING ERRCODE = 'RC001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER tr_ar_guard BEFORE UPDATE OR DELETE ON ar.receivable_entry
  FOR EACH ROW EXECUTE FUNCTION ar.guard_entry();

GRANT USAGE ON SCHEMA ar TO rc_app, rc_worker, rc_reporting;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA ar TO rc_app;
GRANT SELECT ON ALL TABLES IN SCHEMA ar TO rc_worker, rc_reporting;
RESET ROLE;
