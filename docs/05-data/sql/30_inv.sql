SET ROLE rc_migrator;

CREATE TABLE inv.warehouse (
  tenant_id uuid NOT NULL,
  id        uuid NOT NULL,
  branch_id uuid NOT NULL,              -- reference to platform.branch (cross-module: no FK)
  code      text NOT NULL,
  kind      text NOT NULL DEFAULT 'Sellable' CHECK (kind IN ('Sellable','ReturnsDamaged','Virtual')),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code)
);
CALL core.enable_tenant_rls('inv.warehouse');

-- Writable aggregate (D-29). Projection of the ledger, updated in the same transaction.
CREATE TABLE inv.stock_balance (
  tenant_id      uuid NOT NULL,
  warehouse_id   uuid NOT NULL,
  variant_id     uuid NOT NULL,
  on_hand        numeric(18,6) NOT NULL DEFAULT 0,
  reserved       numeric(18,6) NOT NULL DEFAULT 0 CHECK (reserved >= 0),
  wac            bigint NOT NULL DEFAULT 0 CHECK (wac >= 0),   -- IRR per base unit
  value_on_hand  bigint NOT NULL DEFAULT 0,
  allow_negative boolean NOT NULL DEFAULT false,               -- materialized from NegativeStockPolicy (BR-040)
  version        bigint NOT NULL DEFAULT 0,
  PRIMARY KEY (tenant_id, warehouse_id, variant_id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id),
  -- DI-031 enforced by the database, not only by code:
  CONSTRAINT ck_no_negative_available CHECK (allow_negative OR on_hand - reserved >= 0)
);
CALL core.enable_tenant_rls('inv.stock_balance');

-- Document-level idempotency registry (BR-032): one row per (source document, purpose).
-- Needed because stock_movement is partitioned and cannot carry a global unique key on source.
CREATE TABLE inv.posted_source (
  tenant_id   uuid NOT NULL,
  source_type text NOT NULL,
  source_id   uuid NOT NULL,
  purpose     text NOT NULL DEFAULT 'stock',
  posted_at   timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, source_type, source_id, purpose)
);
CALL core.enable_tenant_rls('inv.posted_source');

-- Append-only ledger, monthly range partitions on recorded_at (D05 §6).
CREATE TABLE inv.stock_movement (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  recorded_at   timestamptz NOT NULL DEFAULT now(),
  effective_at  timestamptz NOT NULL,
  warehouse_id  uuid NOT NULL,
  variant_id    uuid NOT NULL,
  movement_type text NOT NULL CHECK (movement_type IN
    ('GoodsReceipt','SaleIssue','SaleReturnReceipt','AdjustmentIn','AdjustmentOut',
     'TransferOut','TransferIn','CountAdjustment','PurchaseReturn','OpeningBalance')),
  qty_base      numeric(18,6) NOT NULL CHECK (qty_base <> 0),
  unit_cost     bigint NOT NULL CHECK (unit_cost >= 0),
  total_cost    bigint NOT NULL,
  source_type   text NOT NULL,
  source_id     uuid NOT NULL,
  source_line   int  NOT NULL,
  -- BR-044 / INV-005: what the document said (e.g. 2 cartons) and the factor in force then; the ledger is in base units
  entered_qty   numeric(18,6),
  entered_unit  text CHECK (entered_unit ~ '^[A-Z0-9]{1,10}$'),
  unit_factor   numeric(18,6) CHECK (unit_factor > 0),
  PRIMARY KEY (tenant_id, recorded_at, id),
  CHECK ((entered_qty IS NULL) = (entered_unit IS NULL) AND (entered_unit IS NULL) = (unit_factor IS NULL)),
  CHECK (unit_factor IS NULL OR qty_base = entered_qty * unit_factor),
  CHECK (total_cost = 0 OR sign(total_cost) = sign(qty_base))                -- value moves with the quantity
) PARTITION BY RANGE (recorded_at);

CREATE TABLE inv.stock_movement_2026_11 PARTITION OF inv.stock_movement
  FOR VALUES FROM ('2026-11-01 00:00+00') TO ('2026-12-01 00:00+00');
CREATE TABLE inv.stock_movement_2026_12 PARTITION OF inv.stock_movement
  FOR VALUES FROM ('2026-12-01 00:00+00') TO ('2027-01-01 00:00+00');
CREATE TABLE inv.stock_movement_default PARTITION OF inv.stock_movement DEFAULT;

CREATE INDEX ix_movement_variant ON inv.stock_movement (tenant_id, variant_id, warehouse_id, recorded_at);
CREATE INDEX ix_movement_source  ON inv.stock_movement (tenant_id, source_type, source_id);

CALL core.enable_tenant_rls('inv.stock_movement');

-- D05 §6: Workers keep the next months' partitions ready so rows never land in DEFAULT. Partitions get no grants
-- (the application reaches them only through the parent, under its RLS policy; fitness rule 3).
CREATE OR REPLACE FUNCTION inv.ensure_movement_partitions(p_months int DEFAULT 3) RETURNS int
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE
  m date;
  created int := 0;
BEGIN
  IF p_months NOT BETWEEN 0 AND 24 THEN
    RAISE EXCEPTION 'months 0-24' USING ERRCODE = '22023';
  END IF;
  FOR i IN 0..p_months LOOP
    m := (date_trunc('month', statement_timestamp() AT TIME ZONE 'UTC') + make_interval(months => i))::date;
    IF to_regclass(format('inv.stock_movement_%s', to_char(m, 'YYYY_MM'))) IS NULL THEN
      EXECUTE format('CREATE TABLE inv.%I PARTITION OF inv.stock_movement FOR VALUES FROM (%L) TO (%L)',
                     'stock_movement_' || to_char(m, 'YYYY_MM'), m::text || ' 00:00+00', (m + interval '1 month')::date::text || ' 00:00+00');
      created := created + 1;
    END IF;
  END LOOP;
  RETURN created;
END $$;
REVOKE ALL ON FUNCTION inv.ensure_movement_partitions(int) FROM PUBLIC;

-- BR-040: Forbid | AllowWithWarning at Tenant, Branch or Variant level; the most specific wins, default Forbid.
-- The posting materialises the result into stock_balance.allow_negative, where the DI-031 CHECK enforces it.
CREATE TABLE inv.negative_stock_policy (
  tenant_id  uuid NOT NULL,
  scope_type text NOT NULL CHECK (scope_type IN ('Tenant','Branch','Variant')),
  scope_id   uuid NOT NULL,
  policy     text NOT NULL CHECK (policy IN ('Forbid','AllowWithWarning')),
  changed_by uuid,
  changed_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, scope_type, scope_id),
  CHECK (scope_type <> 'Tenant' OR scope_id = tenant_id)
);
CALL core.enable_tenant_rls('inv.negative_stock_policy');

-- INV-002 / BR-033 (US-063): the header of a manual adjustment; its lines are the AdjustmentIn/Out movements whose source
-- is this row. Facts only: never changed or removed (AB-05: the history of adjustments is the evidence).
CREATE TABLE inv.stock_adjustment (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  warehouse_id     uuid NOT NULL,
  reason           text NOT NULL CHECK (reason IN ('Damage','Expiry','Theft','CountError','Other')),
  note             text CHECK (char_length(note) <= 500),
  line_count       int NOT NULL CHECK (line_count BETWEEN 1 AND 200),
  value            bigint NOT NULL CHECK (value >= 0),           -- Σ |Δqty| × WAC of the moment, rials (BR-033)
  shrinkage        bigint NOT NULL CHECK (shrinkage >= 0),       -- value taken out
  overage          bigint NOT NULL CHECK (overage >= 0),         -- value brought in
  journal_entry_id uuid,                                        -- acc.journal_entry (cross-module: no FK)
  approval_id      uuid,                                        -- iam.approval_request when above the threshold
  created_by       uuid NOT NULL,
  posted_at        timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id),
  CHECK (reason <> 'Other' OR btrim(coalesce(note, '')) <> ''),  -- INV-002: «سایر» needs an explanation
  CHECK (value = shrinkage + overage),
  CHECK ((value = 0) = (journal_entry_id IS NULL))               -- a valued adjustment always has its entry
);
CALL core.enable_tenant_rls('inv.stock_adjustment');
CREATE INDEX ix_adjustment_posted ON inv.stock_adjustment (tenant_id, posted_at DESC);
CREATE TRIGGER tr_adjustment_append_only BEFORE UPDATE OR DELETE ON inv.stock_adjustment
  FOR EACH ROW EXECUTE FUNCTION core.forbid_update_delete();

CREATE TRIGGER tr_movement_append_only BEFORE UPDATE OR DELETE ON inv.stock_movement
  FOR EACH ROW EXECUTE FUNCTION core.forbid_update_delete();

GRANT USAGE ON SCHEMA inv TO rc_app, rc_worker, rc_reporting;
GRANT SELECT, INSERT, UPDATE ON inv.warehouse, inv.stock_balance TO rc_app;
GRANT SELECT, INSERT ON inv.posted_source TO rc_app;
GRANT SELECT, INSERT, UPDATE ON inv.negative_stock_policy TO rc_app;
GRANT SELECT ON inv.negative_stock_policy TO rc_worker, rc_reporting;
GRANT SELECT, INSERT ON inv.stock_adjustment TO rc_app;
GRANT SELECT ON inv.stock_adjustment TO rc_worker, rc_reporting;
GRANT EXECUTE ON FUNCTION inv.ensure_movement_partitions(int) TO rc_worker;
-- INV-001 rebuild (operator command run by Workers): balances only, never the ledger
GRANT INSERT, UPDATE ON inv.stock_balance TO rc_worker;
-- Privileges on the PARENT only. Partitions do not inherit RLS policies, so applications must never
-- receive privileges on partitions directly (checked by 90_fitness.sql).
GRANT SELECT, INSERT ON inv.stock_movement TO rc_app;
GRANT SELECT ON inv.warehouse, inv.stock_balance, inv.stock_movement TO rc_worker, rc_reporting;
GRANT INSERT ON inv.warehouse TO rc_worker;   -- tenant provisioning consumer (US-011)
RESET ROLE;
