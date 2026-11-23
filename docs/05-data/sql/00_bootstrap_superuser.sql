-- RetailCloud — D05 sample DDL. Run as a superuser ONCE per cluster/database.
-- Creates group roles, extensions and schemas. All objects are owned by rc_migrator.
-- Target: PostgreSQL 16+ (production target 17/18). Tested on 16.

DO $$
DECLARE r text;
BEGIN
  FOREACH r IN ARRAY ARRAY['rc_migrator','rc_app','rc_worker','rc_reporting','rc_tax_signer'] LOOP
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
      EXECUTE format('CREATE ROLE %I NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEROLE NOCREATEDB', r);
    END IF;
  END LOOP;
END $$;
-- Tenant directory (D-37): the ONLY non-superuser BYPASSRLS role; owns two narrow SECURITY DEFINER functions
-- (core.list_tenant_ids in 91_oracles.sql, iam.my_memberships in 15_iam.sql) and can read nothing else (fitness rule 5).
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rc_tenant_directory') THEN
    CREATE ROLE rc_tenant_directory NOLOGIN BYPASSRLS;
  END IF;
END $$;
-- Real login users (e.g. api_svc) are granted membership: GRANT rc_app TO api_svc;
-- Login users are issued as short-lived dynamic credentials by OpenBao (D04 §3.3).

CREATE EXTENSION IF NOT EXISTS pgcrypto;    -- digest() for audit hash chain
CREATE EXTENSION IF NOT EXISTS btree_gist;  -- exclusion constraints on (uuid, range)
CREATE EXTENSION IF NOT EXISTS pg_trgm;     -- product name search (CAT-011, D05 §indexes)

DO $$
DECLARE s text;
BEGIN
  FOREACH s IN ARRAY ARRAY['core','platform','iam','merch','inv','sales','acc','ar','cid','tax','notif','audit'] LOOP
    EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I AUTHORIZATION rc_migrator', s);
    EXECUTE format('REVOKE ALL ON SCHEMA %I FROM PUBLIC', s);
  END LOOP;
END $$;

DO $$ BEGIN EXECUTE format('GRANT CREATE ON DATABASE %I TO rc_migrator', current_database()); END $$;
