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

-- CUS-001 / TM-043 (US-141): one-time codes for phone verification. Global (a customer belongs to no store yet). The code and the
-- phone are kept only as keyed hashes; a row doubles as the send log for the per-phone and per-address limits.
CREATE TABLE cid.otp_challenge (
  id         uuid PRIMARY KEY,
  phone_hash bytea NOT NULL CHECK (octet_length(phone_hash) = 32),
  ip_hash    bytea NOT NULL CHECK (octet_length(ip_hash) = 32),
  code_hash  bytea NOT NULL CHECK (octet_length(code_hash) = 32),
  phone_enc  bytea NOT NULL,                     -- envelope-encrypted E.164: needed once, to create the customer on success; purged with the row
  created_at timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL,
  attempts   int NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 5),
  status     text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','Verified','Failed')),
  CHECK (expires_at > created_at AND expires_at <= created_at + interval '10 minutes')
);
CREATE INDEX ix_otp_phone ON cid.otp_challenge (phone_hash, created_at DESC);
CREATE INDEX ix_otp_ip ON cid.otp_challenge (ip_hash, created_at DESC);
-- a verified or failed challenge never changes again; only attempts and the final status move
CREATE FUNCTION cid.otp_challenge_guard() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF (NEW.id, NEW.phone_hash, NEW.ip_hash, NEW.code_hash, NEW.created_at, NEW.expires_at)
     IS DISTINCT FROM (OLD.id, OLD.phone_hash, OLD.ip_hash, OLD.code_hash, OLD.created_at, OLD.expires_at) THEN
    RAISE EXCEPTION 'a challenge keeps its facts' USING ERRCODE = 'RC001';
  END IF;
  IF OLD.status <> 'Pending' AND (NEW.status <> OLD.status OR NEW.attempts <> OLD.attempts) THEN
    RAISE EXCEPTION 'a finished challenge does not change' USING ERRCODE = 'RC001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER tr_otp_challenge_guard BEFORE UPDATE ON cid.otp_challenge FOR EACH ROW EXECUTE FUNCTION cid.otp_challenge_guard();

GRANT USAGE ON SCHEMA cid TO rc_app, rc_worker;
GRANT SELECT, INSERT, UPDATE ON cid.global_customer, cid.public_code_registry, cid.verified_contact,
                               cid.tenant_customer_membership TO rc_app;
GRANT SELECT, INSERT ON cid.consent_record TO rc_app;
GRANT SELECT, INSERT, UPDATE ON cid.otp_challenge TO rc_app;
GRANT DELETE ON cid.otp_challenge TO rc_worker;   -- purge after a day (data minimisation)
GRANT SELECT ON ALL TABLES IN SCHEMA cid TO rc_worker;
RESET ROLE;
