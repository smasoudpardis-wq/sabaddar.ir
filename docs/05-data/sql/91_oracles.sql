-- D10 §5.13 / D03 PM-09: independent consistency oracles, run nightly in production and after every test/load run.
-- Run as superuser (creates the tenant-directory role); the runner itself executes as rc_worker under RLS.

-- Tenant directory (D-37): the ONLY BYPASSRLS role; can read nothing but tenant ids/status, via one function.
-- (role rc_tenant_directory is created in 00_bootstrap_superuser.sql)
GRANT USAGE ON SCHEMA platform, core TO rc_tenant_directory;
GRANT SELECT (id, status) ON platform.tenant TO rc_tenant_directory;

CREATE OR REPLACE FUNCTION core.list_tenant_ids() RETURNS SETOF uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS
$$ SELECT id FROM platform.tenant WHERE status <> 'Closed' ORDER BY id $$;
GRANT CREATE ON SCHEMA core TO rc_tenant_directory;  -- a new owner needs CREATE on the schema; revoked at once (fitness 6)
ALTER FUNCTION core.list_tenant_ids() OWNER TO rc_tenant_directory;
REVOKE CREATE ON SCHEMA core FROM rc_tenant_directory;
REVOKE ALL ON FUNCTION core.list_tenant_ids() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION core.list_tenant_ids() TO rc_worker;

SET ROLE rc_migrator;
CREATE OR REPLACE FUNCTION core.run_oracles()
RETURNS TABLE (oracle text, tenant_id uuid, detail text)
LANGUAGE plpgsql AS $$
DECLARE t uuid;
BEGIN
  FOR t IN SELECT * FROM core.list_tenant_ids() LOOP
    PERFORM set_config('app.tenant_id', t::text, true);   -- RLS scopes every query below to tenant t

    -- O-2 stock projection = ledger (both directions), quantity and value (INV-001, INV-006)
    RETURN QUERY
      SELECT 'O-2'::text, t, format('warehouse=%s variant=%s on_hand=%s ledger=%s value=%s ledger_value=%s',
                                    x.warehouse_id, x.variant_id, x.on_hand, x.ledger, x.value_on_hand, x.ledger_value)
      FROM (SELECT COALESCE(b.warehouse_id, m.warehouse_id) AS warehouse_id, COALESCE(b.variant_id, m.variant_id) AS variant_id,
                   COALESCE(b.on_hand, 0) AS on_hand, COALESCE(m.q, 0) AS ledger,
                   COALESCE(b.value_on_hand, 0) AS value_on_hand, COALESCE(m.v, 0) AS ledger_value
            FROM inv.stock_balance b
            FULL JOIN (SELECT warehouse_id, variant_id, sum(qty_base) q, sum(total_cost) v FROM inv.stock_movement GROUP BY 1, 2) m
              ON m.warehouse_id = b.warehouse_id AND m.variant_id = b.variant_id) x
      WHERE x.on_hand <> x.ledger OR x.value_on_hand <> x.ledger_value;

    -- O-3 negative available where not allowed (DB CHECK should make this impossible)
    RETURN QUERY SELECT 'O-3', t, format('variant=%s available=%s', variant_id, on_hand - reserved)
      FROM inv.stock_balance WHERE NOT allow_negative AND on_hand - reserved < 0;

    -- O-4 duplicate invoice numbers / duplicate postings per source
    RETURN QUERY SELECT 'O-4', t, format('invoice_no=%s count=%s', invoice_no, count(*))
      FROM sales.sale WHERE invoice_no IS NOT NULL GROUP BY branch_id, fiscal_year, invoice_no HAVING count(*) > 1;
    RETURN QUERY SELECT 'O-4', t, format('journal source %s/%s/%s count=%s', source_type, source_id, purpose, count(*))
      FROM acc.journal_entry WHERE source_type IS NOT NULL GROUP BY source_type, source_id, purpose HAVING count(*) > 1;
    -- O-4 an idempotency record is InProgress only inside its own transaction; a committed one is a bug (US-006)
    RETURN QUERY SELECT 'O-4', t, format('idempotency key %s committed InProgress', key)
      FROM core.idempotency_record WHERE status = 'InProgress';

    -- O-5 posted journals balanced with >= 2 lines
    RETURN QUERY SELECT 'O-5', t, format('journal=%s dr=%s cr=%s lines=%s', e.id, sum(l.debit), sum(l.credit), count(l.*))
      FROM acc.journal_entry e LEFT JOIN acc.journal_line l ON l.entry_id = e.id
      WHERE e.status IN ('Posted', 'Reversed') GROUP BY e.id
      HAVING COALESCE(sum(l.debit), 0) <> COALESCE(sum(l.credit), 0) OR count(l.*) < 2;

    -- O-12 every finalized sale has exactly one live original tax invoice
    RETURN QUERY SELECT 'O-12', t, format('sale=%s live_tax_invoices=%s', s.id, count(ti.id))
      FROM sales.sale s
      LEFT JOIN tax.tax_invoice ti ON ti.source_type = 'Sale' AND ti.source_id = s.id AND ti.kind = 'Original'
                                  AND ti.status <> 'Superseded'
      WHERE s.status = 'Finalized' GROUP BY s.id HAVING count(ti.id) <> 1;

    -- O-13 credit account outstanding = receivable sub-ledger
    RETURN QUERY SELECT 'O-13', t, format('account=%s outstanding=%s ledger=%s', a.id, a.outstanding, COALESCE(sum(r.sign * r.amount), 0))
      FROM ar.credit_account a LEFT JOIN ar.receivable_entry r ON r.account_id = a.id
      GROUP BY a.id, a.outstanding HAVING a.outstanding <> COALESCE(sum(r.sign * r.amount), 0);

    -- O-14 audit hash chain intact
    RETURN QUERY SELECT 'O-14', t, format('chain broken at seq %s', audit.verify_chain())
      WHERE audit.verify_chain() IS NOT NULL;
  END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION core.run_oracles() TO rc_worker;
RESET ROLE;
