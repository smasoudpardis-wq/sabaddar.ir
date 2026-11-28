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

-- CAT-005.1 (US-044): a store's opt-in suggestion of one of its products, for moderation (US-047). Only the descriptive
-- fields and the public barcode leave the store: no price, cost, stock or supplier, and not even which store sent it —
-- only a one-way fingerprint so the same store suggesting the same code twice counts once.
CREATE TABLE catalog.catalog_suggestion (
  id           uuid PRIMARY KEY,
  gtin14       text NOT NULL CHECK (gtin14 ~ '^[0-9]{14}$' AND left(gtin14, 1) <> '2' AND left(gtin14, 2) NOT IN ('02','04')),
  name         text NOT NULL CHECK (char_length(btrim(name)) BETWEEN 1 AND 200),
  brand        text CHECK (char_length(brand) BETWEEN 1 AND 100),
  category     text CHECK (char_length(category) BETWEEN 1 AND 100),
  source_hash  bytea NOT NULL CHECK (octet_length(source_hash) = 32),
  status       text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','Published','Rejected')),
  submitted_at timestamptz NOT NULL DEFAULT now(),
  -- US-047: the moderator's decision (decided_by is the platform operator, iss|sub; never a store user)
  decided_at   timestamptz,
  decided_by   text CHECK (char_length(decided_by) BETWEEN 3 AND 300),
  decision_reason text CHECK (char_length(btrim(decision_reason)) BETWEEN 3 AND 500),
  catalog_product_id uuid REFERENCES catalog.catalog_product (id),
  UNIQUE (gtin14, source_hash),
  CHECK ((status = 'Pending') = (decided_at IS NULL) AND (decided_at IS NULL) = (decided_by IS NULL)),
  CHECK (status <> 'Rejected' OR decision_reason IS NOT NULL),
  CHECK ((status = 'Published') = (catalog_product_id IS NOT NULL))
);

-- US-047 / CAT-005: a suggestion is decided once (Pending → Published | Rejected) and what the store sent never changes
-- (RC035, global). A published product keeps its moderated text; only a later moderation changes it.
CREATE OR REPLACE FUNCTION catalog.suggestion_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status <> 'Pending' OR NEW.gtin14 IS DISTINCT FROM OLD.gtin14 OR NEW.name IS DISTINCT FROM OLD.name
     OR NEW.brand IS DISTINCT FROM OLD.brand OR NEW.category IS DISTINCT FROM OLD.category
     OR NEW.source_hash IS DISTINCT FROM OLD.source_hash OR NEW.submitted_at IS DISTINCT FROM OLD.submitted_at THEN
    RAISE EXCEPTION 'a catalog suggestion is decided once and its content never changes' USING ERRCODE = 'RC035';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_catalog_suggestion_guard BEFORE UPDATE ON catalog.catalog_suggestion
  FOR EACH ROW EXECUTE FUNCTION catalog.suggestion_guard();
CREATE INDEX ix_catalog_suggestion_pending ON catalog.catalog_suggestion (submitted_at) WHERE status = 'Pending';

GRANT USAGE ON SCHEMA catalog TO rc_app, rc_worker, rc_reporting;
GRANT SELECT ON catalog.catalog_product, catalog.global_barcode, catalog.catalog_source_record TO rc_app, rc_worker, rc_reporting;
GRANT INSERT, UPDATE ON catalog.catalog_source_record TO rc_app;   -- the provider cache; the catalog itself is moderation's
GRANT INSERT ON catalog.catalog_suggestion TO rc_app;   -- a store only adds a suggestion; deciding is moderation's (US-047)
GRANT SELECT (id, gtin14, source_hash) ON catalog.catalog_suggestion TO rc_app;   -- enough to see its own was already sent
-- US-047: moderation (platform realm, CatalogModerator; the endpoint decides who) reads the queue and decides through
-- catalog.decide_suggestion only: the app role still cannot write the global catalog directly (T244).
GRANT SELECT (name, brand, category, status, submitted_at, decided_at, catalog_product_id) ON catalog.catalog_suggestion TO rc_app;

-- US-047 / CAT-005 / DI-093: one moderation decision. Publish copies only the allow-listed descriptive fields (the
-- moderator may correct them) into the global catalog — a new product with its public barcode, or the product that
-- already holds that barcode, re-verified — and settles every pending suggestion for the same barcode with it.
-- Reject needs a reason and settles only this suggestion. Outcomes: Published, Rejected, NotFound, AlreadyDecided.
-- SECURITY DEFINER (owner rc_migrator, like inv.lock_balances): the app role executes it but has no write grant.
CREATE OR REPLACE FUNCTION catalog.decide_suggestion(p_id uuid, p_decision text, p_name text, p_brand text, p_category text,
                                                     p_reason text, p_operator text)
RETURNS TABLE (outcome text, catalog_product_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE
  s catalog.catalog_suggestion%ROWTYPE;
  pid uuid;
BEGIN
  IF p_decision NOT IN ('Publish', 'Reject') THEN
    RAISE EXCEPTION 'decision must be Publish or Reject' USING ERRCODE = '22023';
  END IF;
  SELECT * INTO s FROM catalog.catalog_suggestion WHERE id = p_id FOR UPDATE;
  IF NOT FOUND THEN RETURN QUERY SELECT 'NotFound'::text, NULL::uuid; RETURN; END IF;
  IF s.status <> 'Pending' THEN RETURN QUERY SELECT 'AlreadyDecided'::text, s.catalog_product_id; RETURN; END IF;
  IF p_decision = 'Reject' THEN
    UPDATE catalog.catalog_suggestion SET status = 'Rejected', decided_at = now(), decided_by = p_operator, decision_reason = p_reason
     WHERE id = p_id;
    RETURN QUERY SELECT 'Rejected'::text, NULL::uuid; RETURN;
  END IF;
  SELECT g.catalog_product_id INTO pid FROM catalog.global_barcode g WHERE g.gtin14 = s.gtin14;
  IF pid IS NULL THEN
    pid := gen_random_uuid();
    INSERT INTO catalog.catalog_product (id, name, brand, category)
    VALUES (pid, coalesce(nullif(btrim(p_name), ''), s.name), coalesce(nullif(btrim(p_brand), ''), s.brand),
            coalesce(nullif(btrim(p_category), ''), s.category));
    INSERT INTO catalog.global_barcode (gtin14, catalog_product_id) VALUES (s.gtin14, pid);
  ELSE
    UPDATE catalog.catalog_product SET name = coalesce(nullif(btrim(p_name), ''), s.name),
           brand = coalesce(nullif(btrim(p_brand), ''), s.brand, brand),
           category = coalesce(nullif(btrim(p_category), ''), s.category, category), status = 'Published', verified_at = now()
     WHERE id = pid;
  END IF;
  UPDATE catalog.catalog_suggestion SET status = 'Published', decided_at = now(), decided_by = p_operator,
         decision_reason = p_reason, catalog_product_id = pid
   WHERE gtin14 = s.gtin14 AND status = 'Pending';
  RETURN QUERY SELECT 'Published'::text, pid;
END $$;
REVOKE ALL ON FUNCTION catalog.decide_suggestion(uuid, text, text, text, text, text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION catalog.decide_suggestion(uuid, text, text, text, text, text, text) TO rc_app;
RESET ROLE;
