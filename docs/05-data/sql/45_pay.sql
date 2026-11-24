SET ROLE rc_migrator;

-- PAY-001 / PAY-002 (US-101): every payment the store takes, whatever pays for (a sale today; later a collection or an
-- online intent). Cash and a standalone card terminal are confirmed when recorded; online payments arrive with UC-08.
CREATE TABLE pay.payment (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  method        text NOT NULL CHECK (method IN ('Cash','ExternalCardTerminal','OnlineGateway','StoreCredit')),
  status        text NOT NULL CHECK (status IN ('ConfirmedCash','ConfirmedManual','Pending','Confirmed','Failed','NeedsReview','Expired','Voided')),
  amount        bigint NOT NULL CHECK (amount > 0),                 -- what is applied to the bill (rials)
  tendered      bigint,                                             -- cash handed over (SAL-006.1)
  change_given  bigint,
  reference_no  text CHECK (reference_no ~ '^[0-9A-Za-z]{4,32}$'),  -- the card terminal's tracking / reference number
  terminal_no   text CHECK (terminal_no ~ '^[0-9A-Za-z]{1,16}$'),   -- the bank terminal's number, when known
  card_last4    text CHECK (card_last4 ~ '^[0-9]{4}$'),
  business_date date NOT NULL,                                      -- the store's local date (PAY-002.1)
  sale_id       uuid,                                               -- sales.sale (cross-module: no FK)
  register_id   uuid,
  shift_id      uuid,
  recorded_by   uuid NOT NULL,
  override_approval_id uuid,                                        -- a manager allowed a repeated reference (audited)
  recorded_at   timestamptz NOT NULL DEFAULT now(),
  voided_at     timestamptz,                                        -- taken back before the sale was finalized
  voided_by     uuid,
  PRIMARY KEY (tenant_id, id),
  CHECK ((method = 'Cash') = (tendered IS NOT NULL)),
  CHECK (tendered IS NULL OR (tendered >= amount AND change_given = tendered - amount)),
  CHECK (method <> 'Cash' OR status IN ('ConfirmedCash','Voided')),
  CHECK (method <> 'ExternalCardTerminal' OR (status IN ('ConfirmedManual','Voided') AND reference_no IS NOT NULL)),
  CHECK ((status = 'Voided') = (voided_at IS NOT NULL) AND (voided_at IS NULL) = (voided_by IS NULL)),
  CHECK (status <> 'ConfirmedManual' OR method = 'ExternalCardTerminal'),
  CHECK (override_approval_id IS NULL OR method = 'ExternalCardTerminal')
);
-- PAY-002.1 / AB-14: a reference is taken once per bank terminal and day; an approved override is the audited exception
CREATE UNIQUE INDEX ux_payment_card_reference ON pay.payment (tenant_id, coalesce(terminal_no, ''), reference_no, business_date)
  WHERE method = 'ExternalCardTerminal' AND override_approval_id IS NULL;
CREATE INDEX ix_payment_sale ON pay.payment (tenant_id, sale_id) WHERE sale_id IS NOT NULL;
CREATE INDEX ix_payment_manual ON pay.payment (tenant_id, business_date) WHERE status = 'ConfirmedManual';   -- PAY-002.2 reconciliation
CALL core.enable_tenant_rls('pay.payment');

GRANT USAGE ON SCHEMA pay TO rc_app, rc_worker, rc_reporting;
GRANT SELECT, INSERT ON pay.payment TO rc_app;
GRANT UPDATE (status, voided_at, voided_by) ON pay.payment TO rc_app;   -- voiding only; amounts and references never change
GRANT SELECT ON pay.payment TO rc_worker, rc_reporting;
RESET ROLE;
