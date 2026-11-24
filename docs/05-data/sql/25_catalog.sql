SET ROLE rc_migrator;

-- CAT-004 / CAT-005 / ADR-007 (US-043): the global catalog, shared by every store. Reference data only: it has no
-- price, cost, supplier, stock or margin, and no tenant_id (fitness rule 8 keeps it so); a store's own data enters it
-- only through moderation (CAT-005.1, US-044/047), never directly.
CREATE TABLE catalog.catalog_product (
  id          uuid PRIMARY KEY,
  name        text NOT NULL CHECK (char_length(btrim(name)) BETWEEN 1 AND 200),
  brand       text CHECK (char_length(brand) BETWEEN 1 AND 100),
  category    text CHECK (char_length(category) BETWEEN 1 AND 100),
  net_content text CHECK (char_length(net_content) BETWEEN 1 AND 50),
  image_url   text CHECK (image_url ~ '^https://[A-Za-z0-9.-]+/[^\s<>"]{1,450}$'),
  status      text NOT NULL DEFAULT 'Published' CHECK (status IN ('Published','Retired')),
  verified_at timestamptz NOT NULL DEFAULT now(),       -- when moderation last confirmed it (candidate lastVerifiedAt)
  created_at  timestamptz NOT NULL DEFAULT now()
);

-- one public (GS1) barcode belongs to one catalog product; internal codes never enter the global catalog
CREATE TABLE catalog.global_barcode (
  gtin14             text PRIMARY KEY CHECK (gtin14 ~ '^[0-9]{14}$' AND left(gtin14, 1) <> '2' AND left(gtin14, 2) NOT IN ('02','04')),
  catalog_product_id uuid NOT NULL REFERENCES catalog.catalog_product (id),
  created_at         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_global_barcode_product ON catalog.global_barcode (catalog_product_id);

-- what a licensed provider said about a barcode, cached (ADR-007): only the descriptive fields, already sanitised
CREATE TABLE catalog.catalog_source_record (
  source      text NOT NULL CHECK (source IN ('open_food_facts')),
  gtin14      text NOT NULL CHECK (gtin14 ~ '^[0-9]{14}$'),
  found       boolean NOT NULL,                         -- a miss is cached too, so the provider is not asked again at once
  fields      jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(fields) = 'object'
                AND (fields - ARRAY['name','brand','category','netContent','imageUrl']) = '{}'::jsonb),
  confidence  numeric(3,2) NOT NULL CHECK (confidence BETWEEN 0 AND 1),
  license     text NOT NULL,
  attribution text NOT NULL,
  fetched_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (source, gtin14)
);

GRANT USAGE ON SCHEMA catalog TO rc_app, rc_worker, rc_reporting;
GRANT SELECT ON catalog.catalog_product, catalog.global_barcode, catalog.catalog_source_record TO rc_app, rc_worker, rc_reporting;
GRANT INSERT, UPDATE ON catalog.catalog_source_record TO rc_app;   -- the provider cache; the catalog itself is moderation's
RESET ROLE;
