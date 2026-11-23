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
-- TEN-003: active members count against the plan's user cap (function in 10_platform.sql, [DEC D-97]).
CREATE TRIGGER membership_entitlement AFTER INSERT OR UPDATE OF status ON iam.tenant_membership
  FOR EACH ROW EXECUTE FUNCTION platform.enforce_entitlement('users');

-- Role × Scope (D06 §6.1-6.2, US-025). A role is granted at Tenant scope or on one branch / warehouse / register.
-- IAM-003 (US-022): TenantOwner, TenantAdmin and Accountant require MFA; the API decides from the token's acr.
CREATE TABLE iam.role_assignment (
  tenant_id   uuid NOT NULL,
  id          uuid NOT NULL,
  user_id     uuid NOT NULL,
  role        text NOT NULL CHECK (role IN ('TenantOwner','TenantAdmin','BranchManager','Cashier','WarehouseClerk',
                'PurchasingOfficer','Accountant','Auditor','ReadOnlyAnalyst')),
  scope_type  text NOT NULL CHECK (scope_type IN ('Tenant','Branch','Warehouse','Register')),
  scope_id    uuid,                                   -- branch/warehouse/register id (cross-module: no FK)
  assigned_by uuid,                                   -- NULL = system (tenant registration)
  assigned_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, user_id) REFERENCES iam.tenant_membership (tenant_id, user_id),
  CHECK ((scope_type = 'Tenant') = (scope_id IS NULL)),
  -- tenant-wide roles of D06 §6.2 cannot be narrowed to a branch
  CHECK (role NOT IN ('TenantOwner','TenantAdmin','Accountant','Auditor') OR scope_type = 'Tenant'),
  UNIQUE NULLS NOT DISTINCT (tenant_id, user_id, role, scope_type, scope_id)
);
CREATE INDEX ix_role_assignment_user ON iam.role_assignment (tenant_id, user_id);
CALL core.enable_tenant_rls('iam.role_assignment');

-- DI: a tenant always keeps at least one TenantOwner (the last one cannot be removed or re-scoped away).
CREATE OR REPLACE FUNCTION iam.keep_last_owner() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.role = 'TenantOwner' THEN
    PERFORM 1 FROM iam.role_assignment WHERE tenant_id = OLD.tenant_id AND role = 'TenantOwner' FOR UPDATE;
    IF (SELECT count(*) FROM iam.role_assignment WHERE tenant_id = OLD.tenant_id AND role = 'TenantOwner' AND id <> OLD.id) = 0 THEN
      RAISE EXCEPTION 'the last TenantOwner of a tenant cannot be removed' USING ERRCODE = 'RC010';
    END IF;
  END IF;
  RETURN OLD;
END $$;
CREATE TRIGGER keep_last_owner BEFORE DELETE ON iam.role_assignment FOR EACH ROW EXECUTE FUNCTION iam.keep_last_owner();
CREATE TRIGGER no_role_update BEFORE UPDATE ON iam.role_assignment FOR EACH ROW EXECUTE FUNCTION core.forbid_update_delete();

-- Invitations (IAM-001, US-025): a single-use secret link; only its SHA-256 is stored. Delivery by SMS/e-mail comes with
-- Notifications (X-03); until then the inviter shares the link. assignments = [{role, scopeType, scopeId}].
CREATE TABLE iam.invitation (
  tenant_id    uuid NOT NULL,
  id           uuid NOT NULL,
  token_hash   bytea NOT NULL UNIQUE CHECK (length(token_hash) = 32),
  display_name text NOT NULL CHECK (length(display_name) BETWEEN 1 AND 100),
  email        text,
  mobile       text,
  assignments  jsonb NOT NULL CHECK (jsonb_typeof(assignments) = 'array' AND jsonb_array_length(assignments) BETWEEN 1 AND 20),
  invited_by   uuid NOT NULL,
  created_at   timestamptz NOT NULL DEFAULT now(),
  expires_at   timestamptz NOT NULL,
  status       text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','Accepted','Revoked')),
  accepted_by  uuid,
  accepted_at  timestamptz,
  PRIMARY KEY (tenant_id, id),
  CHECK ((status = 'Accepted') = (accepted_by IS NOT NULL AND accepted_at IS NOT NULL))
);
CALL core.enable_tenant_rls('iam.invitation');

-- On-the-spot approvals (IAM-007, US-026, D08 F-14). An approval is single use, valid 5 minutes after the decision and
-- bound to one action + resource + value (binding_hash, D-61). SoD-01: the approver is never the requester (CHECK).
CREATE TABLE iam.approval_request (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  action        text NOT NULL,                       -- the permission that needs approval, e.g. pos.discount.apply
  resource_type text NOT NULL,
  resource_id   text,
  scope_type    text NOT NULL CHECK (scope_type IN ('Tenant','Branch','Warehouse','Register')),
  scope_id      uuid,
  scope_branch  uuid,                                -- branch of a warehouse/register scope, for approver coverage
  condition     text NOT NULL,                       -- ABAC-02/03/06: amount-limit, return-window, outside-hours, ...
  value         text,
  limit_value   text,
  reason        text CHECK (length(reason) <= 500),
  binding_hash  bytea NOT NULL CHECK (length(binding_hash) = 32),
  requested_by  uuid NOT NULL,
  requested_at  timestamptz NOT NULL DEFAULT now(),
  expires_at    timestamptz NOT NULL,                -- a pending request is decided within this window
  status        text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','Approved','Rejected','Expired','Consumed')),
  decided_by    uuid,
  decided_at    timestamptz,
  decision_note text CHECK (length(decision_note) <= 500),
  consumed_at   timestamptz,
  PRIMARY KEY (tenant_id, id),
  CHECK ((scope_type = 'Tenant') = (scope_id IS NULL)),
  CHECK (decided_by IS NULL OR decided_by <> requested_by),                           -- SoD-01 / DI-120
  CHECK ((status IN ('Approved','Rejected','Consumed')) = (decided_by IS NOT NULL AND decided_at IS NOT NULL)),
  CHECK ((status = 'Consumed') = (consumed_at IS NOT NULL))
);
CREATE INDEX ix_approval_pending ON iam.approval_request (tenant_id, status, expires_at) WHERE status = 'Pending';
CALL core.enable_tenant_rls('iam.approval_request');

-- Allowed transitions only: Pending -> Approved | Rejected | Expired, Approved -> Consumed | Expired. Facts are immutable.
CREATE OR REPLACE FUNCTION iam.approval_transition() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NOT ((OLD.status = 'Pending' AND NEW.status IN ('Approved','Rejected','Expired'))
       OR (OLD.status = 'Approved' AND NEW.status IN ('Consumed','Expired'))) THEN
    RAISE EXCEPTION 'approval % cannot go from % to %', OLD.id, OLD.status, NEW.status USING ERRCODE = 'RC012';
  END IF;
  IF (NEW.action, NEW.resource_type, NEW.resource_id, NEW.binding_hash, NEW.requested_by, NEW.requested_at, NEW.scope_type, NEW.scope_id)
     IS DISTINCT FROM (OLD.action, OLD.resource_type, OLD.resource_id, OLD.binding_hash, OLD.requested_by, OLD.requested_at, OLD.scope_type, OLD.scope_id)
     OR (OLD.decided_by IS NOT NULL AND NEW.decided_by IS DISTINCT FROM OLD.decided_by) THEN
    RAISE EXCEPTION 'approval facts are immutable' USING ERRCODE = 'RC012';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER approval_transition BEFORE UPDATE ON iam.approval_request FOR EACH ROW EXECUTE FUNCTION iam.approval_transition();

-- Register PIN (IAM-004, AUTH-08, US-023): per user and tenant, Argon2id (m=64MB, t=3, p=1) in PHC format. Only usable
-- on a registered terminal; 5 consecutive failures lock it until the user sets a new PIN after a full login.
CREATE TABLE iam.user_pin (
  tenant_id       uuid NOT NULL,
  user_id         uuid NOT NULL,
  pin_hash        text NOT NULL CHECK (pin_hash LIKE '$argon2id$v=19$m=65536,t=3,p=1$%'),
  failed_attempts int  NOT NULL DEFAULT 0 CHECK (failed_attempts BETWEEN 0 AND 5),
  locked_at       timestamptz,
  updated_at      timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, user_id),
  FOREIGN KEY (tenant_id, user_id) REFERENCES iam.tenant_membership (tenant_id, user_id),
  CHECK ((failed_attempts = 5) = (locked_at IS NOT NULL))
);
CALL core.enable_tenant_rls('iam.user_pin');

GRANT USAGE ON SCHEMA iam TO rc_app, rc_worker;
GRANT SELECT, INSERT, UPDATE ON iam.merchant_user TO rc_app;
GRANT SELECT, INSERT, UPDATE ON iam.tenant_membership TO rc_app;
GRANT SELECT, INSERT, DELETE ON iam.role_assignment TO rc_app;
GRANT UPDATE ON iam.role_assignment TO rc_app;   -- only for the row lock in keep_last_owner; the no_role_update trigger rejects every UPDATE
GRANT SELECT, INSERT, UPDATE ON iam.invitation TO rc_app;
GRANT SELECT, INSERT, UPDATE ON iam.approval_request TO rc_app;
GRANT SELECT, INSERT, UPDATE ON iam.user_pin TO rc_app;
GRANT SELECT ON iam.merchant_user, iam.tenant_membership, iam.role_assignment TO rc_worker;
GRANT INSERT ON iam.tenant_membership, iam.role_assignment TO rc_worker;   -- owner on platform.tenant-registered (US-011)
RESET ROLE;

-- Before a tenant is selected, the API must know which tenants the authenticated user belongs to (IAM-001, BR-001).
-- This is the only cross-tenant read the application can do, and it returns the CALLER's own active memberships only
-- (app.user_id is set by the unit of work from the validated token, never from request input).
GRANT USAGE ON SCHEMA iam, platform TO rc_tenant_directory;
GRANT SELECT (tenant_id, user_id, status) ON iam.tenant_membership TO rc_tenant_directory;
GRANT SELECT (tenant_id, user_id, role, scope_type, scope_id) ON iam.role_assignment TO rc_tenant_directory;
GRANT SELECT (tenant_id, token_hash, status, expires_at) ON iam.invitation TO rc_tenant_directory;
GRANT SELECT (id, status, legal_name) ON platform.tenant TO rc_tenant_directory;

CREATE OR REPLACE FUNCTION iam.my_memberships()
RETURNS TABLE (tenant_id uuid, tenant_name text, tenant_status text, assignments jsonb)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
  SELECT m.tenant_id, t.legal_name, t.status,
         coalesce(jsonb_agg(jsonb_build_object('role', a.role, 'scopeType', a.scope_type, 'scopeId', a.scope_id)
                            ORDER BY a.role, a.scope_type, a.scope_id) FILTER (WHERE a.role IS NOT NULL), '[]'::jsonb)
  FROM iam.tenant_membership m
  JOIN platform.tenant t ON t.id = m.tenant_id
  LEFT JOIN iam.role_assignment a ON a.tenant_id = m.tenant_id AND a.user_id = m.user_id
  WHERE m.user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
    AND m.status = 'Active'
    AND t.status <> 'Closed'
  GROUP BY m.tenant_id, t.legal_name, t.status
  ORDER BY t.legal_name
$$;
GRANT CREATE ON SCHEMA iam TO rc_tenant_directory;  -- a new owner needs CREATE on the schema; revoked at once (fitness 6)
ALTER FUNCTION iam.my_memberships() OWNER TO rc_tenant_directory;
REVOKE CREATE ON SCHEMA iam FROM rc_tenant_directory;
REVOKE ALL ON FUNCTION iam.my_memberships() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION iam.my_memberships() TO rc_app;

-- Accepting an invitation happens before the invitee is a member, so the tenant is not known yet: this returns the
-- tenant of a PENDING, unexpired invitation for the SHA-256 of a token the caller holds — nothing else.
CREATE OR REPLACE FUNCTION iam.invitation_tenant(p_token_hash bytea) RETURNS uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
  SELECT tenant_id FROM iam.invitation WHERE token_hash = p_token_hash AND status = 'Pending' AND expires_at > now()
$$;
GRANT CREATE ON SCHEMA iam TO rc_tenant_directory;  -- a new owner needs CREATE on the schema; revoked at once (fitness 6)
ALTER FUNCTION iam.invitation_tenant(bytea) OWNER TO rc_tenant_directory;
REVOKE CREATE ON SCHEMA iam FROM rc_tenant_directory;
REVOKE ALL ON FUNCTION iam.invitation_tenant(bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION iam.invitation_tenant(bytea) TO rc_app;

