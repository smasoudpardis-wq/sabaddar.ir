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

-- PAY-002.2 / AB-14 (US-105): the bank's settlement file, imported by an accountant, matched line by line on reference and
-- amount against the standalone terminal's payments. Append-only: an import is never edited; a wrong file is answered by
-- the accountant reversing its journal entry and importing the right one (another file, another hash).
CREATE TABLE pay.settlement_import (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  file_name        text NOT NULL CHECK (length(file_name) BETWEEN 1 AND 200),
  file_sha256      bytea NOT NULL CHECK (length(file_sha256) = 32),
  line_count       int NOT NULL CHECK (line_count BETWEEN 1 AND 5000),
  matched_count    int NOT NULL CHECK (matched_count BETWEEN 0 AND line_count),
  matched_amount   bigint NOT NULL CHECK (matched_amount >= 0),     -- Σ gross of the matched lines: what leaves CardInTransit (J4)
  fees             bigint NOT NULL CHECK (fees BETWEEN 0 AND matched_amount),
  journal_entry_id uuid,                                            -- acc.journal_entry (cross-module: no FK)
  imported_by      uuid NOT NULL,
  imported_at      timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  CONSTRAINT ux_settlement_file UNIQUE (tenant_id, file_sha256),    -- the same file is not imported twice
  CHECK ((matched_count > 0) = (journal_entry_id IS NOT NULL)),
  CHECK ((matched_count = 0) = (matched_amount = 0))
);
CALL core.enable_tenant_rls('pay.settlement_import');

CREATE TABLE pay.settlement_line (
  tenant_id        uuid NOT NULL,
  import_id        uuid NOT NULL,
  line_no          int NOT NULL CHECK (line_no >= 1),
  terminal_no      text CHECK (terminal_no ~ '^[0-9A-Za-z]{1,16}$'),
  reference_no     text NOT NULL CHECK (reference_no ~ '^[0-9A-Za-z]{4,32}$'),
  transaction_date date NOT NULL,
  amount           bigint NOT NULL CHECK (amount > 0),
  fee              bigint NOT NULL DEFAULT 0 CHECK (fee BETWEEN 0 AND amount),
  outcome          text NOT NULL CHECK (outcome IN ('Matched','AmountMismatch','NotFound','Ambiguous','AlreadySettled','DuplicateInFile','Unfinalized')),
  payment_id       uuid,                                            -- the payment it settles (Matched) or resembles (AmountMismatch, Unfinalized)
  PRIMARY KEY (tenant_id, import_id, line_no),
  FOREIGN KEY (tenant_id, import_id) REFERENCES pay.settlement_import (tenant_id, id),
  FOREIGN KEY (tenant_id, payment_id) REFERENCES pay.payment (tenant_id, id),
  CHECK (outcome <> 'Matched' OR payment_id IS NOT NULL),
  CHECK (payment_id IS NULL OR outcome IN ('Matched','AmountMismatch','Unfinalized'))
);
-- a payment is settled once, whichever file brings it
CREATE UNIQUE INDEX ux_settlement_payment ON pay.settlement_line (tenant_id, payment_id) WHERE outcome = 'Matched';
CREATE INDEX ix_settlement_line_payment ON pay.settlement_line (tenant_id, payment_id) WHERE payment_id IS NOT NULL;
CALL core.enable_tenant_rls('pay.settlement_line');

GRANT USAGE ON SCHEMA pay TO rc_app, rc_worker, rc_reporting;
GRANT SELECT, INSERT ON pay.payment TO rc_app;
GRANT UPDATE (status, voided_at, voided_by) ON pay.payment TO rc_app;   -- voiding only; amounts and references never change
GRANT SELECT ON pay.payment TO rc_worker, rc_reporting;
GRANT SELECT, INSERT ON pay.settlement_import, pay.settlement_line TO rc_app;   -- append-only
GRANT SELECT ON pay.settlement_import, pay.settlement_line TO rc_worker, rc_reporting;
RESET ROLE;
