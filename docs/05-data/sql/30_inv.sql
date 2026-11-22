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
  PRIMARY KEY (tenant_id, recorded_at, id)
) PARTITION BY RANGE (recorded_at);

CREATE TABLE inv.stock_movement_2026_11 PARTITION OF inv.stock_movement
  FOR VALUES FROM ('2026-11-01 00:00+00') TO ('2026-12-01 00:00+00');
CREATE TABLE inv.stock_movement_2026_12 PARTITION OF inv.stock_movement
  FOR VALUES FROM ('2026-12-01 00:00+00') TO ('2027-01-01 00:00+00');
CREATE TABLE inv.stock_movement_default PARTITION OF inv.stock_movement DEFAULT;

CREATE INDEX ix_movement_variant ON inv.stock_movement (tenant_id, variant_id, warehouse_id, recorded_at);
CREATE INDEX ix_movement_source  ON inv.stock_movement (tenant_id, source_type, source_id);

CALL core.enable_tenant_rls('inv.stock_movement');
CREATE TRIGGER tr_movement_append_only BEFORE UPDATE OR DELETE ON inv.stock_movement
  FOR EACH ROW EXECUTE FUNCTION core.forbid_update_delete();

GRANT USAGE ON SCHEMA inv TO rc_app, rc_worker, rc_reporting;
GRANT SELECT, INSERT, UPDATE ON inv.warehouse, inv.stock_balance TO rc_app;
GRANT SELECT, INSERT ON inv.posted_source TO rc_app;
-- Privileges on the PARENT only. Partitions do not inherit RLS policies, so applications must never
-- receive privileges on partitions directly (checked by 90_fitness.sql).
GRANT SELECT, INSERT ON inv.stock_movement TO rc_app;
GRANT SELECT ON inv.warehouse, inv.stock_balance, inv.stock_movement TO rc_worker, rc_reporting;
GRANT INSERT ON inv.warehouse TO rc_worker;   -- tenant provisioning consumer (US-011)
RESET ROLE;
