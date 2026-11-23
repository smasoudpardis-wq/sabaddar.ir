SET ROLE rc_migrator;

-- CAT-011: Persian search key — Arabic yeh/kaf to Persian, digits to ASCII, lower case; ZWNJ, tatweel and diacritics
-- (U+064B..U+065F fathatan..wavy hamza, U+0670 superscript alef) removed; runs of spaces collapsed.
CREATE OR REPLACE FUNCTION merch.normalize_fa(p text) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE SET search_path = pg_catalog AS $$
  SELECT btrim(regexp_replace(
           translate(lower(p), 'يكىۀةأإآ٠١٢٣٤٥٦٧٨٩۰۱۲۳۴۵۶۷۸۹' || chr(8204) || chr(1600)
                               || (SELECT string_agg(chr(c), '') FROM generate_series(1611, 1631) c) || chr(1648),
                     'یکیههاا' || 'ا' || '01234567890123456789'),
           '\s+', ' ', 'g'))
$$;

CREATE TABLE merch.tenant_product (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  name             text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 200),
  name_normalized  text GENERATED ALWAYS AS (merch.normalize_fa(name)) STORED,
  brand            text CHECK (length(brand) <= 100),
  base_unit_code   text NOT NULL CHECK (base_unit_code ~ '^[A-Z0-9]{1,10}$'),
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
  sku         text NOT NULL CHECK (sku ~ '^[A-Za-z0-9._/-]{1,64}$'),
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
  packaging_unit   text NOT NULL DEFAULT 'EA' CHECK (packaging_unit ~ '^[A-Z0-9]{1,10}$'),
  unit_factor      numeric(18,6) NOT NULL DEFAULT 1 CHECK (unit_factor > 0),
  active           boolean NOT NULL DEFAULT true,
  added_at         timestamptz NOT NULL DEFAULT now(),
  removed_at       timestamptz,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, variant_id) REFERENCES merch.tenant_variant (tenant_id, id),
  CHECK (is_internal OR code_normalized ~ '^[0-9]{14}$'),
  CHECK (NOT is_internal OR code_normalized ~ '^[A-Za-z0-9-]{1,32}$'),
  CHECK (active = (removed_at IS NULL))
);
-- DI-080 / CAT-002.1: one active assignment per barcode per tenant.
CREATE UNIQUE INDEX ux_barcode_active ON merch.tenant_product_barcode (tenant_id, code_normalized) WHERE active;
CALL core.enable_tenant_rls('merch.tenant_product_barcode');

-- CAT-002: the history of a barcode is kept — a row is only ever deactivated (with removed_at), never edited or revived.
CREATE OR REPLACE FUNCTION merch.barcode_history() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NOT (OLD.active AND NOT NEW.active)
     OR (NEW.variant_id, NEW.code_normalized, NEW.code_original, NEW.is_internal, NEW.packaging_unit, NEW.unit_factor, NEW.added_at)
        IS DISTINCT FROM (OLD.variant_id, OLD.code_normalized, OLD.code_original, OLD.is_internal, OLD.packaging_unit, OLD.unit_factor, OLD.added_at) THEN
    RAISE EXCEPTION 'barcode % can only be deactivated', OLD.id USING ERRCODE = 'RC016';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER barcode_history BEFORE UPDATE ON merch.tenant_product_barcode FOR EACH ROW EXECUTE FUNCTION merch.barcode_history();
CREATE INDEX ix_product_name ON merch.tenant_product (tenant_id, name_normalized);
-- CAT-011 word-prefix search: each query term is also matched as '%term%', which this trigram index serves (D05)
CREATE INDEX ix_product_name_trgm ON merch.tenant_product USING gin (name_normalized public.gin_trgm_ops);

CREATE TABLE merch.price_list (
  tenant_id  uuid NOT NULL,
  id         uuid NOT NULL,
  name       text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 100),
  is_default boolean NOT NULL DEFAULT false,   -- CAT-009: a branch without its own list sells from the tenant's default
  PRIMARY KEY (tenant_id, id)
);
CREATE UNIQUE INDEX ux_price_list_default ON merch.price_list (tenant_id) WHERE is_default;
CALL core.enable_tenant_rls('merch.price_list');

CREATE TABLE merch.price_list_item (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  price_list_id uuid NOT NULL,
  variant_id    uuid NOT NULL,
  unit_price    bigint NOT NULL CHECK (unit_price >= 0),     -- IRR
  valid         tstzrange NOT NULL CHECK (NOT isempty(valid) AND lower(valid) IS NOT NULL),
  created_by    uuid,
  created_at    timestamptz NOT NULL DEFAULT now(),
  reason        text CHECK (length(reason) <= 200),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, price_list_id) REFERENCES merch.price_list (tenant_id, id),
  FOREIGN KEY (tenant_id, variant_id)    REFERENCES merch.tenant_variant (tenant_id, id),
  -- DI-083: no overlapping validity for the same variant in the same list.
  EXCLUDE USING gist (tenant_id WITH =, price_list_id WITH =, variant_id WITH =, valid WITH &&)
);
CALL core.enable_tenant_rls('merch.price_list_item');
CREATE INDEX ix_price_item_variant ON merch.price_list_item (tenant_id, price_list_id, variant_id, lower(valid));

-- CAT-009 / AB-06: a price version is history. What applied is never rewritten: the only change is ending a version
-- (bringing its end forward, never into the past) or correcting a version that has not started yet. Rows are never
-- deleted (no grant).
CREATE OR REPLACE FUNCTION merch.price_history() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  t timestamptz := statement_timestamp() - interval '1 minute';   -- clock skew between app servers
BEGIN
  IF (NEW.tenant_id, NEW.id, NEW.price_list_id, NEW.variant_id, lower(NEW.valid), NEW.created_by, NEW.created_at)
     IS DISTINCT FROM (OLD.tenant_id, OLD.id, OLD.price_list_id, OLD.variant_id, lower(OLD.valid), OLD.created_by, OLD.created_at)
     OR (NEW.unit_price, NEW.reason) IS DISTINCT FROM (OLD.unit_price, OLD.reason) AND lower(OLD.valid) <= t
     OR upper(NEW.valid) IS DISTINCT FROM upper(OLD.valid)
        AND (upper(NEW.valid) IS NULL OR upper(NEW.valid) < t OR upper(OLD.valid) IS NOT NULL AND upper(NEW.valid) > upper(OLD.valid)) THEN
    RAISE EXCEPTION 'price version % is history', OLD.id USING ERRCODE = 'RC023';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER price_history BEFORE UPDATE ON merch.price_list_item FOR EACH ROW EXECUTE FUNCTION merch.price_history();

-- CAT-007: weighted barcode profiles (an EAN-13 starting with 2: prefix, item code, [price check digit], value,
-- check digit). A tenant edits its profiles as one set (PUT) guarded by weighted_barcode_settings.version.
CREATE TABLE merch.weighted_barcode_settings (
  tenant_id uuid PRIMARY KEY,
  version   int NOT NULL DEFAULT 1 CHECK (version >= 1)
);
CALL core.enable_tenant_rls('merch.weighted_barcode_settings');

CREATE TABLE merch.weighted_barcode_profile (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  prefix                text NOT NULL CHECK (prefix ~ '^2[0-9]{0,2}$'),     -- GS1 restricted circulation (20-29)
  item_code_length      int  NOT NULL CHECK (item_code_length BETWEEN 1 AND 6),
  value_type            text NOT NULL CHECK (value_type IN ('Weight','Price')),
  value_length          int  NOT NULL CHECK (value_length BETWEEN 3 AND 6),
  decimals              int  NOT NULL CHECK (decimals BETWEEN 0 AND 3),   -- Weight: digits after the point (kg)
  has_inner_check_digit boolean NOT NULL DEFAULT false,                   -- GS1 price/weight check digit (4 or 5 digits)
  PRIMARY KEY (tenant_id, id),
  CHECK (length(prefix) + item_code_length + value_length + has_inner_check_digit::int = 12),
  CHECK (NOT has_inner_check_digit OR value_length IN (4, 5)),
  CHECK (value_type = 'Weight' OR decimals = 0)                            -- a price is whole rials
);
CALL core.enable_tenant_rls('merch.weighted_barcode_profile');

-- DI-084: one tenant's prefixes never overlap (neither equal nor one starting the other), so a code decodes one way.
CREATE OR REPLACE FUNCTION merch.weighted_prefix_disjoint() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('rc-weighted-prefix:' || NEW.tenant_id, 0));
  IF EXISTS (SELECT 1 FROM merch.weighted_barcode_profile p
             WHERE p.tenant_id = NEW.tenant_id AND p.id <> NEW.id
               AND (starts_with(p.prefix, NEW.prefix) OR starts_with(NEW.prefix, p.prefix))) THEN
    RAISE EXCEPTION 'weighted barcode prefix % overlaps another profile', NEW.prefix USING ERRCODE = 'RC022';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER weighted_prefix_disjoint BEFORE INSERT OR UPDATE ON merch.weighted_barcode_profile
  FOR EACH ROW EXECUTE FUNCTION merch.weighted_prefix_disjoint();

GRANT USAGE ON SCHEMA merch TO rc_app, rc_worker, rc_reporting;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA merch TO rc_app;
GRANT SELECT ON ALL TABLES IN SCHEMA merch TO rc_worker, rc_reporting;
GRANT DELETE ON merch.weighted_barcode_profile TO rc_app;   -- the set is replaced as a whole
RESET ROLE;
