SET ROLE rc_migrator;

CREATE TABLE tax.tax_invoice (
  tenant_id    uuid NOT NULL,
  id           uuid NOT NULL,
  source_type  text NOT NULL CHECK (source_type IN ('Sale','SaleReturn')),
  source_id    uuid NOT NULL,
  kind         text NOT NULL CHECK (kind IN ('Original','Return','Correction','Cancellation')),
  related_to   uuid,
  status       text NOT NULL DEFAULT 'Pending'
               CHECK (status IN ('Pending','NeedsReview','Signed','Sent','Accepted','Rejected','Superseded')),
  issuing_unit text NOT NULL,          -- شناسه حافظه مالیاتی
  tax_unique_no text,
  payload      jsonb NOT NULL,         -- canonical invoice content (built from SaleLineSnapshot, BR-073)
  content_hash bytea NOT NULL,
  signature    bytea,
  key_ref      text,
  issued_at    timestamptz NOT NULL,
  deadline_at  timestamptz NOT NULL,   -- from TaxSubmissionPolicy (BR-075, DI-073)
  signed_at    timestamptz,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, related_to) REFERENCES tax.tax_invoice (tenant_id, id),
  CHECK (status NOT IN ('Signed','Sent','Accepted','Rejected') OR (signature IS NOT NULL AND signed_at IS NOT NULL))
);
-- DI-070: one live invoice per source+kind; corrections supersede (BR-074).
CREATE UNIQUE INDEX ux_tax_invoice_source ON tax.tax_invoice (tenant_id, source_type, source_id, kind)
  WHERE status <> 'Superseded';
CREATE INDEX ix_tax_queue ON tax.tax_invoice (status, deadline_at) WHERE status IN ('Pending','Signed','Sent','NeedsReview','Rejected');
CALL core.enable_tenant_rls('tax.tax_invoice');

-- DI-071: content is frozen once signed.
CREATE OR REPLACE FUNCTION tax.guard_invoice() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'tax invoices are never deleted' USING ERRCODE = 'RC020'; END IF;
  IF TG_OP = 'UPDATE' AND OLD.status NOT IN ('Pending','NeedsReview')
     AND (NEW.payload IS DISTINCT FROM OLD.payload OR NEW.content_hash IS DISTINCT FROM OLD.content_hash
          OR NEW.signature IS DISTINCT FROM OLD.signature) THEN
    RAISE EXCEPTION 'tax invoice % content is frozen after signing', OLD.id USING ERRCODE = 'RC020';
  END IF;
  IF NEW.content_hash IS DISTINCT FROM public.digest(NEW.payload::text, 'sha256') THEN
    RAISE EXCEPTION 'content_hash does not match payload' USING ERRCODE = 'RC021';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER tr_tax_invoice_guard BEFORE INSERT OR UPDATE OR DELETE ON tax.tax_invoice
  FOR EACH ROW EXECUTE FUNCTION tax.guard_invoice();

GRANT USAGE ON SCHEMA tax TO rc_app, rc_worker, rc_reporting, rc_tax_signer;
GRANT SELECT, INSERT, UPDATE ON tax.tax_invoice TO rc_app, rc_worker;
GRANT SELECT (tenant_id, id, status, payload, content_hash, issuing_unit, key_ref) ON tax.tax_invoice TO rc_tax_signer;
GRANT SELECT (tenant_id, id, status, kind, source_type, source_id, issued_at, deadline_at) ON tax.tax_invoice TO rc_reporting;
RESET ROLE;
