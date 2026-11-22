-- IdentityAccess (D02 §5.2, ADR-003): internal user identity and tenant memberships.
-- Run as superuser: the membership-lookup function is owned by rc_tenant_directory.
SET ROLE rc_migrator;

-- GLOBAL: one row per IdP identity (issuer + subject). No tenant data here.
CREATE TABLE iam.merchant_user (
  id           uuid PRIMARY KEY,
  idp_issuer   text NOT NULL,
  idp_subject  text NOT NULL,
  display_name text NOT NULL,
  status       text NOT NULL DEFAULT 'Active' CHECK (status IN ('Invited','Active','Disabled')),
  created_at   timestamptz NOT NULL DEFAULT now(),
  UNIQUE (idp_issuer, idp_subject)
);

-- TENANT-scoped (RLS): a user may belong to several tenants (IAM-001).
CREATE TABLE iam.tenant_membership (
  tenant_id uuid NOT NULL,            -- reference to platform.tenant (cross-module: no FK, D05 §3)
  user_id   uuid NOT NULL REFERENCES iam.merchant_user(id),
  status    text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Suspended','Removed')),
  joined_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, user_id)
);
CREATE INDEX ix_membership_user ON iam.tenant_membership (user_id) WHERE status = 'Active';
CALL core.enable_tenant_rls('iam.tenant_membership');

GRANT USAGE ON SCHEMA iam TO rc_app, rc_worker;
GRANT SELECT, INSERT, UPDATE ON iam.merchant_user TO rc_app;
GRANT SELECT, INSERT, UPDATE ON iam.tenant_membership TO rc_app;
GRANT SELECT ON iam.merchant_user, iam.tenant_membership TO rc_worker;
RESET ROLE;

-- Before a tenant is selected, the API must know which tenants the authenticated user belongs to (IAM-001, BR-001).
-- This is the only cross-tenant read the application can do, and it returns the CALLER's own active memberships only
-- (app.user_id is set by the unit of work from the validated token, never from request input).
GRANT USAGE ON SCHEMA iam, platform TO rc_tenant_directory;
GRANT SELECT (tenant_id, user_id, status) ON iam.tenant_membership TO rc_tenant_directory;
GRANT SELECT (id, status, legal_name) ON platform.tenant TO rc_tenant_directory;

CREATE OR REPLACE FUNCTION iam.my_memberships()
RETURNS TABLE (tenant_id uuid, tenant_name text, tenant_status text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
  SELECT m.tenant_id, t.legal_name, t.status
  FROM iam.tenant_membership m
  JOIN platform.tenant t ON t.id = m.tenant_id
  WHERE m.user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
    AND m.status = 'Active'
    AND t.status <> 'Closed'
  ORDER BY t.legal_name
$$;
ALTER FUNCTION iam.my_memberships() OWNER TO rc_tenant_directory;
REVOKE ALL ON FUNCTION iam.my_memberships() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION iam.my_memberships() TO rc_app;
