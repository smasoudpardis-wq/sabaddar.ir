SET ROLE rc_migrator;

-- GLOBAL tables (no tenant_id). No RLS; access restricted by GRANT: only rc_app / rc_worker
-- (code-level module boundary + DI-064). rc_reporting and rc_tax_signer get NOTHING here.
CREATE TABLE cid.global_customer (
  id          uuid PRIMARY KEY,
  public_code text NOT NULL UNIQUE CHECK (public_code ~ '^CU-[0-9A-HJKMNP-TV-Z]{8}$'),  -- Crockford Base32
  status      text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Merged','Anonymized')),
  merged_into uuid REFERENCES cid.global_customer(id),
  created_at  timestamptz NOT NULL DEFAULT now()
);

-- DI-061: a public code is never reissued, even after rotation.
CREATE TABLE cid.public_code_registry (
  public_code text PRIMARY KEY,
  global_customer_id uuid NOT NULL REFERENCES cid.global_customer(id),
  issued_at  timestamptz NOT NULL DEFAULT now(),
  retired_at timestamptz
);

CREATE TABLE cid.verified_contact (
  id                 uuid PRIMARY KEY,
  global_customer_id uuid NOT NULL REFERENCES cid.global_customer(id),
  contact_type       text NOT NULL CHECK (contact_type IN ('Mobile','Email')),
  value_enc          bytea NOT NULL,     -- envelope-encrypted E.164
  lookup_hash        bytea NOT NULL,     -- HMAC-SHA256(blind-index key, normalized value)
  status             text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Removed')),
  verified_at        timestamptz NOT NULL
);
CREATE UNIQUE INDEX ux_contact_active ON cid.verified_contact (lookup_hash) WHERE status = 'Active';  -- DI-060

-- TENANT-scoped
CREATE TABLE cid.tenant_customer_membership (
  tenant_id          uuid NOT NULL,
  id                 uuid NOT NULL,      -- = TenantCustomerId (the only id other modules see)
  global_customer_id uuid NOT NULL REFERENCES cid.global_customer(id),
  status             text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Left')),
  join_source        text NOT NULL CHECK (join_source IN ('StoreQr','Pos','Portal')),
  joined_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, global_customer_id)                                        -- DI-062
);
CALL core.enable_tenant_rls('cid.tenant_customer_membership');

CREATE TABLE cid.consent_record (
  tenant_id      uuid NOT NULL,
  id             uuid NOT NULL,
  membership_id  uuid NOT NULL,
  purpose        text NOT NULL CHECK (purpose IN ('Operational','Marketing')),
  channel        text NOT NULL CHECK (channel IN ('Sms','Bale','Email','Push')),
  granted        boolean NOT NULL,
  policy_version text NOT NULL,
  source         text NOT NULL,
  recorded_at    timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, membership_id) REFERENCES cid.tenant_customer_membership (tenant_id, id)
);
-- Current consent = latest record per (membership, purpose, channel); history is append-only.
CREATE INDEX ix_consent_latest ON cid.consent_record (tenant_id, membership_id, purpose, channel, recorded_at DESC);
CALL core.enable_tenant_rls('cid.consent_record');
CREATE TRIGGER tr_consent_append_only BEFORE UPDATE OR DELETE ON cid.consent_record
  FOR EACH ROW EXECUTE FUNCTION core.forbid_update_delete();

GRANT USAGE ON SCHEMA cid TO rc_app, rc_worker;
GRANT SELECT, INSERT, UPDATE ON cid.global_customer, cid.public_code_registry, cid.verified_contact,
                               cid.tenant_customer_membership TO rc_app;
GRANT SELECT, INSERT ON cid.consent_record TO rc_app;
GRANT SELECT ON ALL TABLES IN SCHEMA cid TO rc_worker;
RESET ROLE;
