SET ROLE rc_migrator;

-- Notifications (US-151, NOT-001/005/006/007, DI-130/131). A notification is queued in the producer's unit of work and
-- sent by the Workers through a channel adapter (SMS, Bale). Each try is one attempt row whose idempotency key
-- (notificationId:channel:attemptNo) goes to the provider. Recipients are references (TenantCustomerRef or a merchant
-- user), never raw numbers: the adapter resolves the address only when sending (DI-064).
CREATE TABLE notif.notification (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  purpose          text NOT NULL CHECK (purpose IN ('Receipt','DueReminder','Internal')),   -- NOT-007: no Marketing path
  channel          text NOT NULL CHECK (channel IN ('Sms','Bale')),
  recipient_ref    text NOT NULL CHECK (length(recipient_ref) BETWEEN 1 AND 100),
  template_code    text NOT NULL CHECK (template_code ~ '^[a-z][a-z0-9-]{1,40}$'),
  template_version int  NOT NULL CHECK (template_version >= 1),
  payload          jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(payload) = 'object' AND length(payload::text) <= 2000),
  source_type      text,
  source_id        uuid,
  status           text NOT NULL DEFAULT 'Queued'
                   CHECK (status IN ('Queued','Sending','Sent','Retrying','Delivered','Failed','Undeliverable','DeadLettered')),
  attempts         int  NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 5),             -- DI-131
  next_attempt_at  timestamptz NOT NULL DEFAULT now(),
  provider_message_id text,
  delivered_at     timestamptz,
  last_error       text CHECK (length(last_error) <= 300),
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  CHECK ((source_type IS NULL) = (source_id IS NULL)),
  CHECK ((status = 'Delivered') = (delivered_at IS NOT NULL)),                             -- DI-130: only with a receipt
  CHECK (status NOT IN ('Sent','Delivered') OR provider_message_id IS NOT NULL)
);
-- one notification per (source, purpose, channel): a redelivered event does not message twice
CREATE UNIQUE INDEX ux_notification_source ON notif.notification (tenant_id, source_type, source_id, purpose, channel)
  WHERE source_type IS NOT NULL;
CREATE INDEX ix_notification_due ON notif.notification (tenant_id, next_attempt_at) WHERE status IN ('Queued','Retrying');
CALL core.enable_tenant_rls('notif.notification');

CREATE TABLE notif.notification_attempt (
  tenant_id        uuid NOT NULL,
  notification_id  uuid NOT NULL,
  attempt_no       int  NOT NULL CHECK (attempt_no BETWEEN 1 AND 5),
  idempotency_key  text NOT NULL,
  started_at       timestamptz NOT NULL DEFAULT now(),
  finished_at      timestamptz,
  outcome          text CHECK (outcome IN ('Sent','Transient','Permanent')),
  provider_message_id text,
  error            text CHECK (length(error) <= 300),
  PRIMARY KEY (tenant_id, notification_id, attempt_no),
  UNIQUE (tenant_id, idempotency_key),
  FOREIGN KEY (tenant_id, notification_id) REFERENCES notif.notification (tenant_id, id),
  CHECK ((outcome IS NULL) = (finished_at IS NULL))
);
CALL core.enable_tenant_rls('notif.notification_attempt');

-- NOT-006 state machine: Queued/Retrying -> Sending -> Sent | Retrying | Failed | Undeliverable | DeadLettered;
-- Sent -> Delivered | Failed (provider receipt); terminal states never change. Facts (who, what, why) are immutable.
CREATE OR REPLACE FUNCTION notif.notification_transition() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF (NEW.purpose, NEW.channel, NEW.recipient_ref, NEW.template_code, NEW.template_version, NEW.payload, NEW.source_type, NEW.source_id, NEW.created_at)
     IS DISTINCT FROM (OLD.purpose, OLD.channel, OLD.recipient_ref, OLD.template_code, OLD.template_version, OLD.payload, OLD.source_type, OLD.source_id, OLD.created_at) THEN
    RAISE EXCEPTION 'notification % facts are immutable', OLD.id USING ERRCODE = 'RC019';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status IN ('Queued','Retrying') AND NEW.status = 'Sending')
    OR (OLD.status = 'Sending' AND NEW.status IN ('Sent','Retrying','Failed','Undeliverable','DeadLettered'))
    OR (OLD.status = 'Sent' AND NEW.status IN ('Delivered','Failed'))) THEN
    RAISE EXCEPTION 'notification % cannot go from % to %', OLD.id, OLD.status, NEW.status USING ERRCODE = 'RC019';
  END IF;
  IF NEW.attempts < OLD.attempts OR NEW.attempts > OLD.attempts + 1 THEN
    RAISE EXCEPTION 'notification % attempts only grow by one', OLD.id USING ERRCODE = 'RC019';
  END IF;
  NEW.updated_at := now();
  RETURN NEW;
END $$;
CREATE TRIGGER notification_transition BEFORE UPDATE ON notif.notification
  FOR EACH ROW EXECUTE FUNCTION notif.notification_transition();

GRANT USAGE ON SCHEMA notif TO rc_app, rc_worker, rc_reporting;
GRANT SELECT, INSERT ON notif.notification TO rc_app;                       -- producers queue; only the Workers send
GRANT SELECT, INSERT, UPDATE ON notif.notification, notif.notification_attempt TO rc_worker;
GRANT SELECT (tenant_id, id, purpose, channel, status, attempts, created_at, updated_at) ON notif.notification TO rc_reporting;
RESET ROLE;
