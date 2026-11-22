SET ROLE rc_migrator;

CREATE TABLE merch.tenant_product (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  name             text NOT NULL,
  base_unit_code   text NOT NULL,
  tax_code         text,                    -- شناسه کالا/خدمت مالیاتی (TAX-003)
  tax_rate_bp      int  NOT NULL DEFAULT 0 CHECK (tax_rate_bp BETWEEN 0 AND 10000),
  catalog_product_id uuid,                  -- reference only (no FK into Global catalog)
  status           text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Inactive','Merged')),
  merged_into_id   uuid,
  version          int NOT NULL DEFAULT 1,
  PRIMARY KEY (tenant_id, id),
  CHECK (status <> 'Merged' OR merged_into_id IS NOT NULL)
);
CALL core.enable_tenant_rls('merch.tenant_product');

CREATE TABLE merch.tenant_variant (
  tenant_id   uuid NOT NULL,
  id          uuid NOT NULL,
  product_id  uuid NOT NULL,
  sku         text NOT NULL,
  axis_values jsonb NOT NULL DEFAULT '{}'::jsonb,   -- {"color":"red","size":"42"}
  status      text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Inactive')),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, sku),
  UNIQUE (tenant_id, product_id, axis_values),         -- DI-081
  FOREIGN KEY (tenant_id, product_id) REFERENCES merch.tenant_product (tenant_id, id)
);
CALL core.enable_tenant_rls('merch.tenant_variant');

CREATE TABLE merch.tenant_product_barcode (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  variant_id       uuid NOT NULL,
  code_normalized  text NOT NULL,            -- GTIN-14 for standard codes; raw for internal
  code_original    text NOT NULL,
  is_internal      boolean NOT NULL DEFAULT false,
  packaging_unit   text NOT NULL DEFAULT 'EA',
  unit_factor      numeric(18,6) NOT NULL DEFAULT 1 CHECK (unit_factor > 0),
  active           boolean NOT NULL DEFAULT true,
  added_at         timestamptz NOT NULL DEFAULT now(),
  removed_at       timestamptz,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, variant_id) REFERENCES merch.tenant_variant (tenant_id, id),
  CHECK (is_internal OR code_normalized ~ '^[0-9]{14}$')
);
-- DI-080 / CAT-002.1: one active assignment per barcode per tenant.
CREATE UNIQUE INDEX ux_barcode_active ON merch.tenant_product_barcode (tenant_id, code_normalized) WHERE active;
CALL core.enable_tenant_rls('merch.tenant_product_barcode');

CREATE TABLE merch.price_list (
  tenant_id uuid NOT NULL,
  id        uuid NOT NULL,
  name      text NOT NULL,
  PRIMARY KEY (tenant_id, id)
);
CALL core.enable_tenant_rls('merch.price_list');

CREATE TABLE merch.price_list_item (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  price_list_id uuid NOT NULL,
  variant_id    uuid NOT NULL,
  unit_price    bigint NOT NULL CHECK (unit_price >= 0),     -- IRR
  valid         tstzrange NOT NULL CHECK (NOT isempty(valid) AND lower(valid) IS NOT NULL),
  created_by    uuid,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, price_list_id) REFERENCES merch.price_list (tenant_id, id),
  FOREIGN KEY (tenant_id, variant_id)    REFERENCES merch.tenant_variant (tenant_id, id),
  -- DI-083: no overlapping validity for the same variant in the same list.
  EXCLUDE USING gist (tenant_id WITH =, price_list_id WITH =, variant_id WITH =, valid WITH &&)
);
CALL core.enable_tenant_rls('merch.price_list_item');

GRANT USAGE ON SCHEMA merch TO rc_app, rc_worker, rc_reporting;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA merch TO rc_app;
GRANT SELECT ON ALL TABLES IN SCHEMA merch TO rc_worker, rc_reporting;
RESET ROLE;
