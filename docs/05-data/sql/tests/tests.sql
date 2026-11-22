-- D05 executable checks. Each check prints PASS or aborts the run.
\set ON_ERROR_STOP on
\set A '''00000000-0000-0000-0000-00000000000a'''
\set B '''00000000-0000-0000-0000-00000000000b'''
\o /dev/null

CREATE SCHEMA t;
CREATE FUNCTION t.expect_error(p_label text, p_state text, VARIADIC p_sql text[]) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE s text;
BEGIN
  BEGIN
    FOREACH s IN ARRAY p_sql LOOP EXECUTE s; END LOOP;
  EXCEPTION WHEN OTHERS THEN
    IF SQLSTATE = p_state THEN RAISE NOTICE 'PASS  %  (% %)', p_label, SQLSTATE, SQLERRM; RETURN; END IF;
    RAISE EXCEPTION 'FAIL  %: expected %, got % — %', p_label, p_state, SQLSTATE, SQLERRM;
  END;
  RAISE EXCEPTION 'FAIL  %: expected error % but all statements succeeded', p_label, p_state;
END $$;
CREATE FUNCTION t.ok(p_label text, p_cond boolean) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  IF p_cond IS TRUE THEN RAISE NOTICE 'PASS  %', p_label; ELSE RAISE EXCEPTION 'FAIL  %', p_label; END IF;
END $$;
GRANT USAGE ON SCHEMA t TO rc_app, rc_worker, rc_reporting;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA t TO rc_app, rc_worker, rc_reporting;

------------------------------------------------------------------ seed (as application role)
SET ROLE rc_app;
SELECT set_config('app.tenant_id', :A, false);
INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES (:A, 'فروشگاه الف', 'supermarket');
INSERT INTO platform.branch VALUES (:A, 'a0000000-0000-0000-0000-000000000001', 'B1', 'شعبه مرکزی');
INSERT INTO platform.register VALUES (:A, 'a0000000-0000-0000-0000-000000000002', 'a0000000-0000-0000-0000-000000000001', 'R1');
INSERT INTO inv.warehouse VALUES (:A, 'a0000000-0000-0000-0000-000000000003', 'a0000000-0000-0000-0000-000000000001', 'W1');
INSERT INTO merch.tenant_product (tenant_id, id, name, base_unit_code, tax_rate_bp)
  VALUES (:A, 'a0000000-0000-0000-0000-000000000010', 'شیر ۱ لیتری', 'EA', 1000);
INSERT INTO merch.tenant_variant (tenant_id, id, product_id, sku)
  VALUES (:A, 'a0000000-0000-0000-0000-000000000011', 'a0000000-0000-0000-0000-000000000010', 'MILK-1L');
INSERT INTO merch.tenant_product_barcode (tenant_id, id, variant_id, code_normalized, code_original)
  VALUES (:A, gen_random_uuid(), 'a0000000-0000-0000-0000-000000000011', '06260000000017', '6260000000017');
INSERT INTO merch.price_list VALUES (:A, 'a0000000-0000-0000-0000-000000000020', 'پیش‌فرض');
INSERT INTO merch.price_list_item (tenant_id, id, price_list_id, variant_id, unit_price, valid)
  VALUES (:A, gen_random_uuid(), 'a0000000-0000-0000-0000-000000000020', 'a0000000-0000-0000-0000-000000000011',
          500000, tstzrange('2026-11-01', NULL));
INSERT INTO inv.stock_balance (tenant_id, warehouse_id, variant_id, on_hand, wac, value_on_hand)
  VALUES (:A, 'a0000000-0000-0000-0000-000000000003', 'a0000000-0000-0000-0000-000000000011', 1, 300000, 300000);
INSERT INTO sales.shift (tenant_id, id, register_id, cashier_id, opening_cash)
  VALUES (:A, 'a0000000-0000-0000-0000-000000000030', 'a0000000-0000-0000-0000-000000000002', gen_random_uuid(), 0);
INSERT INTO sales.sale (tenant_id, id, branch_id, register_id, shift_id, cashier_id, terminal_id, client_draft_id, pricing_mode, total)
  VALUES (:A, 'a0000000-0000-0000-0000-000000000040', 'a0000000-0000-0000-0000-000000000001',
          'a0000000-0000-0000-0000-000000000002', 'a0000000-0000-0000-0000-000000000030',
          gen_random_uuid(), gen_random_uuid(), 'draft-0001', 'TaxExclusive', 550000);
INSERT INTO sales.sale_line (tenant_id, sale_id, line_no, variant_id, qty, unit_price, net, tax_rate_bp, tax, line_total)
  VALUES (:A, 'a0000000-0000-0000-0000-000000000040', 1, 'a0000000-0000-0000-0000-000000000011', 1, 500000, 500000, 1000, 50000, 550000);
INSERT INTO acc.account (tenant_id, id, code, name, account_type, is_system) VALUES
  (:A, 'a0000000-0000-0000-0000-000000000101', '1101', 'صندوق', 'Asset', true),
  (:A, 'a0000000-0000-0000-0000-000000000102', '4101', 'فروش کالا', 'Revenue', true),
  (:A, 'a0000000-0000-0000-0000-000000000103', '2201', 'مالیات بر ارزش افزوده پرداختنی', 'Liability', true);
INSERT INTO acc.account (tenant_id, id, code, name, account_type, requires_detail) VALUES
  (:A, 'a0000000-0000-0000-0000-000000000104', '1301', 'حساب‌های دریافتنی تجاری', 'Asset', true);
INSERT INTO acc.fiscal_period (tenant_id, id, fiscal_year, period_no, span) VALUES
  (:A, 'a0000000-0000-0000-0000-000000000201', 1405, 9, tstzrange('2026-11-22 00:00+03:30', '2026-12-22 00:00+03:30')),
  (:A, 'a0000000-0000-0000-0000-000000000202', 1405, 8, tstzrange('2026-10-23 00:00+03:30', '2026-11-22 00:00+03:30'));
UPDATE acc.fiscal_period SET status = 'Closed' WHERE id = 'a0000000-0000-0000-0000-000000000202';

SELECT set_config('app.tenant_id', :B, false);
INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES (:B, 'فروشگاه ب', 'apparel');
INSERT INTO platform.branch VALUES (:B, 'b0000000-0000-0000-0000-000000000001', 'B1', 'شعبه ب');

------------------------------------------------------------------ tenant isolation (Q1, P0-1)
\echo '== tenant isolation'
SELECT t.ok('T01 tenant B sees no rows of tenant A',
  (SELECT count(*) FROM sales.sale) = 0 AND (SELECT count(*) FROM platform.tenant) = 1
  AND (SELECT count(*) FROM merch.tenant_product WHERE tenant_id = :A) = 0);
SELECT t.expect_error('T02 tenant B cannot write a row for tenant A', '42501',
  format($$INSERT INTO platform.branch VALUES (%L, gen_random_uuid(), 'X', 'x')$$, :A));
SELECT t.expect_error('T03 tenant B cannot reference tenant A branch (composite FK)', '23503',
  format($$INSERT INTO platform.register VALUES (%L, gen_random_uuid(), 'a0000000-0000-0000-0000-000000000001', 'R9')$$, :B));
SELECT t.expect_error('T04 tenant B cannot update tenant A sale (0 rows -> no change)', 'P0001',
  $q$DO $d$ DECLARE n int; BEGIN UPDATE sales.sale SET total = 1; GET DIAGNOSTICS n = ROW_COUNT;
     IF n = 0 THEN RAISE EXCEPTION 'zero rows' USING ERRCODE = 'P0001'; END IF; END $d$$q$);
SELECT set_config('app.tenant_id', '', false);
SELECT t.ok('T05 no tenant context -> zero rows everywhere',
  (SELECT count(*) FROM sales.sale) + (SELECT count(*) FROM platform.tenant) + (SELECT count(*) FROM acc.account) = 0);
SELECT t.expect_error('T06 no tenant context -> insert rejected', '42501',
  format($$INSERT INTO platform.branch VALUES (%L, gen_random_uuid(), 'Y', 'y')$$, :A));
SELECT t.expect_error('T07 app role cannot read a partition directly', '42501',
  $$SELECT count(*) FROM inv.stock_movement_2026_11$$);

------------------------------------------------------------------ catalog / inventory
SELECT set_config('app.tenant_id', :A, false);
\echo '== catalog & inventory'
SELECT t.expect_error('T10 duplicate active barcode in a tenant (DI-080)', '23505',
  $$INSERT INTO merch.tenant_product_barcode (tenant_id, id, variant_id, code_normalized, code_original)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000011', '06260000000017', '6260000000017')$$);
SELECT t.expect_error('T11 overlapping price validity (DI-083)', '23P01',
  $$INSERT INTO merch.price_list_item (tenant_id, id, price_list_id, variant_id, unit_price, valid)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000020',
            'a0000000-0000-0000-0000-000000000011', 600000, tstzrange('2026-12-01', NULL))$$);
SELECT t.expect_error('T12 negative available stock rejected by DB (DI-031)', '23514',
  $$UPDATE inv.stock_balance SET on_hand = on_hand - 2 WHERE variant_id = 'a0000000-0000-0000-0000-000000000011'$$);
INSERT INTO inv.posted_source (tenant_id, source_type, source_id) VALUES (:A, 'Sale', 'a0000000-0000-0000-0000-000000000040');
SELECT t.expect_error('T13 same source document cannot post stock twice (BR-032)', '23505',
  $$INSERT INTO inv.posted_source (tenant_id, source_type, source_id) VALUES (core.current_tenant(), 'Sale', 'a0000000-0000-0000-0000-000000000040')$$);
INSERT INTO inv.stock_movement (tenant_id, id, recorded_at, effective_at, warehouse_id, variant_id, movement_type,
                                qty_base, unit_cost, total_cost, source_type, source_id, source_line)
VALUES (:A, gen_random_uuid(), '2026-11-22 10:00+00', '2026-11-22 10:00+00', 'a0000000-0000-0000-0000-000000000003',
        'a0000000-0000-0000-0000-000000000011', 'SaleIssue', -1, 300000, -300000, 'Sale', 'a0000000-0000-0000-0000-000000000040', 1);
SELECT t.expect_error('T14 stock movement is append-only for the app role', '42501',
  $$UPDATE inv.stock_movement SET qty_base = -2$$);
SELECT t.expect_error('T15 movement for another tenant via parent rejected', '42501',
  format($$INSERT INTO inv.stock_movement (tenant_id, id, effective_at, warehouse_id, variant_id, movement_type, qty_base,
          unit_cost, total_cost, source_type, source_id, source_line)
          VALUES (%L, gen_random_uuid(), now(), gen_random_uuid(), gen_random_uuid(), 'SaleIssue', -1, 1, -1, 'Sale', gen_random_uuid(), 1)$$, :B));

------------------------------------------------------------------ sales
\echo '== sales'
SELECT t.expect_error('T20 second open shift on the same register (DI-008)', '23505',
  $$INSERT INTO sales.shift (tenant_id, id, register_id, cashier_id, opening_cash)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000002', gen_random_uuid(), 0)$$);
SELECT t.expect_error('T21 finalized sale must have invoice number', '23514',
  $$UPDATE sales.sale SET status = 'Finalized', finalized_at = now() WHERE id = 'a0000000-0000-0000-0000-000000000040'$$);
UPDATE sales.sale SET status = 'Finalized', finalized_at = now(), fiscal_year = 1405, invoice_no = 'B1-1405-000001'
 WHERE id = 'a0000000-0000-0000-0000-000000000040';
SELECT t.expect_error('T22 finalized sale is immutable (DI-003)', 'RC002',
  $$UPDATE sales.sale SET total = 1 WHERE id = 'a0000000-0000-0000-0000-000000000040'$$);
UPDATE sales.sale SET return_status = 'Partial', version = version + 1 WHERE id = 'a0000000-0000-0000-0000-000000000040';
SELECT t.ok('T23 return bookkeeping still allowed on finalized sale',
  (SELECT return_status FROM sales.sale WHERE id = 'a0000000-0000-0000-0000-000000000040') = 'Partial');
SELECT t.expect_error('T24 finalized line price is immutable', 'RC002',
  $$UPDATE sales.sale_line SET unit_price = 1 WHERE sale_id = 'a0000000-0000-0000-0000-000000000040'$$);
SELECT t.expect_error('T25 cannot add a line to a finalized sale', 'RC002',
  $$INSERT INTO sales.sale_line (tenant_id, sale_id, line_no, variant_id, qty, unit_price, net, tax_rate_bp, tax, line_total)
    VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000040', 2, 'a0000000-0000-0000-0000-000000000011', 1, 1, 1, 0, 0, 1)$$);
SELECT t.expect_error('T26 returned qty cannot exceed sold qty (DI-005)', '23514',
  $$UPDATE sales.sale_line SET returned_qty = 2 WHERE sale_id = 'a0000000-0000-0000-0000-000000000040'$$);
UPDATE sales.sale_line SET returned_qty = 1 WHERE sale_id = 'a0000000-0000-0000-0000-000000000040';
SELECT t.ok('T27 returned qty may increase up to qty', true);
SELECT t.expect_error('T28 line_total must equal net + tax (DI-001)', '23514',
  $$INSERT INTO sales.sale (tenant_id, id, branch_id, register_id, shift_id, cashier_id, terminal_id, client_draft_id, pricing_mode)
    VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000041', gen_random_uuid(), gen_random_uuid(),
            'a0000000-0000-0000-0000-000000000030', gen_random_uuid(), gen_random_uuid(), 'draft-0002', 'TaxExclusive')$$,
  $$INSERT INTO sales.sale_line (tenant_id, sale_id, line_no, variant_id, qty, unit_price, net, tax_rate_bp, tax, line_total)
    VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000041', 1, gen_random_uuid(), 1, 100, 100, 1000, 10, 111)$$);
SELECT t.expect_error('T29 duplicate client draft id (DI-010)', '23505',
  $$INSERT INTO sales.sale (tenant_id, id, branch_id, register_id, shift_id, cashier_id, terminal_id, client_draft_id, pricing_mode)
    VALUES (core.current_tenant(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(),
            'a0000000-0000-0000-0000-000000000030', gen_random_uuid(), gen_random_uuid(), 'draft-0001', 'TaxExclusive')$$);

------------------------------------------------------------------ accounting
\echo '== accounting'
SELECT t.expect_error('T30 unbalanced posted journal rejected at commit (DI-040)', 'RC010',
  $$INSERT INTO acc.journal_entry (tenant_id, id, effective_at, period_id)
    VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000301', '2026-11-22 12:00+03:30', 'a0000000-0000-0000-0000-000000000201')$$,
  $$INSERT INTO acc.journal_line (tenant_id, entry_id, line_no, account_id, debit) VALUES
    (core.current_tenant(), 'a0000000-0000-0000-0000-000000000301', 1, 'a0000000-0000-0000-0000-000000000101', 550000)$$,
  $$INSERT INTO acc.journal_line (tenant_id, entry_id, line_no, account_id, credit) VALUES
    (core.current_tenant(), 'a0000000-0000-0000-0000-000000000301', 2, 'a0000000-0000-0000-0000-000000000102', 500000)$$,
  $$UPDATE acc.journal_entry SET status = 'Posted', posted_at = now() WHERE id = 'a0000000-0000-0000-0000-000000000301'$$,
  $$SET CONSTRAINTS ALL IMMEDIATE$$);
SELECT t.expect_error('T31 a line cannot be both debit and credit', '23514',
  $$INSERT INTO acc.journal_entry (tenant_id, id, effective_at, period_id)
    VALUES (core.current_tenant(), gen_random_uuid(), '2026-11-22 12:00+03:30', 'a0000000-0000-0000-0000-000000000201')$$,
  $$INSERT INTO acc.journal_line (tenant_id, entry_id, line_no, account_id, debit, credit)
    SELECT core.current_tenant(), id, 1, 'a0000000-0000-0000-0000-000000000101', 10, 10 FROM acc.journal_entry LIMIT 1$$);
-- balanced sale journal: Dr cash 550,000 / Cr revenue 500,000 / Cr VAT 50,000
BEGIN;
INSERT INTO acc.journal_entry (tenant_id, id, effective_at, period_id, source_type, source_id, purpose)
  VALUES (:A, 'a0000000-0000-0000-0000-000000000302', '2026-11-22 12:00+03:30', 'a0000000-0000-0000-0000-000000000201',
          'Sale', 'a0000000-0000-0000-0000-000000000040', 'revenue');
INSERT INTO acc.journal_line (tenant_id, entry_id, line_no, account_id, debit, credit) VALUES
  (:A, 'a0000000-0000-0000-0000-000000000302', 1, 'a0000000-0000-0000-0000-000000000101', 550000, 0),
  (:A, 'a0000000-0000-0000-0000-000000000302', 2, 'a0000000-0000-0000-0000-000000000102', 0, 500000),
  (:A, 'a0000000-0000-0000-0000-000000000302', 3, 'a0000000-0000-0000-0000-000000000103', 0, 50000);
UPDATE acc.journal_entry SET status = 'Posted', posted_at = now() WHERE id = 'a0000000-0000-0000-0000-000000000302';
COMMIT;
SELECT t.ok('T32 balanced journal commits', (SELECT status FROM acc.journal_entry WHERE id = 'a0000000-0000-0000-0000-000000000302') = 'Posted');
SELECT t.expect_error('T33 posted journal line is immutable (DI-041)', 'RC012',
  $$UPDATE acc.journal_line SET debit = 1 WHERE entry_id = 'a0000000-0000-0000-0000-000000000302' AND line_no = 1$$);
SELECT t.expect_error('T34 posted journal header is immutable', 'RC012',
  $$UPDATE acc.journal_entry SET description = 'x' WHERE id = 'a0000000-0000-0000-0000-000000000302'$$);
SELECT t.expect_error('T35 same source cannot be posted twice (DI-044)', '23505',
  $$INSERT INTO acc.journal_entry (tenant_id, id, effective_at, period_id, source_type, source_id, purpose)
    VALUES (core.current_tenant(), gen_random_uuid(), '2026-11-22 12:00+03:30', 'a0000000-0000-0000-0000-000000000201',
            'Sale', 'a0000000-0000-0000-0000-000000000040', 'revenue')$$);
SELECT t.expect_error('T36 posting into a closed period (DI-042)', 'RC013',
  $$INSERT INTO acc.journal_entry (tenant_id, id, status, posted_at, effective_at, period_id)
    VALUES (core.current_tenant(), gen_random_uuid(), 'Posted', now(), '2026-11-01 12:00+03:30', 'a0000000-0000-0000-0000-000000000202')$$);
SELECT t.expect_error('T37 effective date outside the period span', 'RC014',
  $$INSERT INTO acc.journal_entry (tenant_id, id, status, posted_at, effective_at, period_id)
    VALUES (core.current_tenant(), gen_random_uuid(), 'Posted', now(), '2027-01-15 12:00+03:30', 'a0000000-0000-0000-0000-000000000201')$$);
SELECT t.expect_error('T38 receivable line without customer detail (DI-043)', 'RC011',
  $$INSERT INTO acc.journal_entry (tenant_id, id, effective_at, period_id)
    VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000303', '2026-11-22 12:00+03:30', 'a0000000-0000-0000-0000-000000000201')$$,
  $$INSERT INTO acc.journal_line (tenant_id, entry_id, line_no, account_id, debit, credit) VALUES
    (core.current_tenant(), 'a0000000-0000-0000-0000-000000000303', 1, 'a0000000-0000-0000-0000-000000000104', 100, 0),
    (core.current_tenant(), 'a0000000-0000-0000-0000-000000000303', 2, 'a0000000-0000-0000-0000-000000000102', 0, 100)$$,
  $$UPDATE acc.journal_entry SET status = 'Posted', posted_at = now() WHERE id = 'a0000000-0000-0000-0000-000000000303'$$,
  $$SET CONSTRAINTS ALL IMMEDIATE$$);
SELECT t.expect_error('T39 overlapping fiscal periods', '23P01',
  $$INSERT INTO acc.fiscal_period (tenant_id, id, fiscal_year, period_no, span)
    VALUES (core.current_tenant(), gen_random_uuid(), 1405, 10, tstzrange('2026-12-01 00:00+03:30', '2027-01-01 00:00+03:30'))$$);

------------------------------------------------------------------ receivables
\echo '== receivables'
INSERT INTO ar.credit_account (tenant_id, id, tenant_customer_id, credit_identity_enc, credit_limit)
  VALUES (:A, 'a0000000-0000-0000-0000-000000000401', gen_random_uuid(), '\x01', 10000000);
INSERT INTO ar.receivable_entry (tenant_id, id, account_id, entry_type, amount, sign, due_date, open_amount, source_type, source_id)
  VALUES (:A, 'a0000000-0000-0000-0000-000000000402', 'a0000000-0000-0000-0000-000000000401', 'Charge', 2000000, 1,
          '2026-12-22', 2000000, 'Sale', gen_random_uuid());
SELECT t.expect_error('T40 charge open amount cannot exceed amount (DI-053)', '23514',
  $$INSERT INTO ar.receivable_entry (tenant_id, id, account_id, entry_type, amount, sign, due_date, open_amount, source_type, source_id)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000401', 'Charge', 100, 1, '2026-12-22', 101, 'Sale', gen_random_uuid())$$);
UPDATE ar.receivable_entry SET open_amount = 1000000 WHERE id = 'a0000000-0000-0000-0000-000000000402';
SELECT t.ok('T41 allocation may decrease open amount', true);
SELECT t.expect_error('T42 open amount cannot increase back', 'RC001',
  $$UPDATE ar.receivable_entry SET open_amount = 2000000 WHERE id = 'a0000000-0000-0000-0000-000000000402'$$);
SELECT t.expect_error('T43 receivable amount is immutable', 'RC001',
  $$UPDATE ar.receivable_entry SET amount = 1 WHERE id = 'a0000000-0000-0000-0000-000000000402'$$);

------------------------------------------------------------------ customer identity
\echo '== customer identity'
INSERT INTO cid.global_customer (id, public_code) VALUES ('c0000000-0000-0000-0000-000000000001', 'CU-7P4K9X2Q');
INSERT INTO cid.verified_contact (id, global_customer_id, contact_type, value_enc, lookup_hash, verified_at)
  VALUES (gen_random_uuid(), 'c0000000-0000-0000-0000-000000000001', 'Mobile', '\x01', '\xabcd', now());
SELECT t.expect_error('T50 verified phone belongs to at most one active customer (DI-060)', '23505',
  $$INSERT INTO cid.global_customer (id, public_code) VALUES ('c0000000-0000-0000-0000-000000000002', 'CU-8Q5M0Y3R')$$,
  $$INSERT INTO cid.verified_contact (id, global_customer_id, contact_type, value_enc, lookup_hash, verified_at)
    VALUES (gen_random_uuid(), 'c0000000-0000-0000-0000-000000000002', 'Mobile', '\x02', '\xabcd', now())$$);
SELECT t.expect_error('T51 public code format (no ambiguous letters I, L, O, U)', '23514',
  $$INSERT INTO cid.global_customer (id, public_code) VALUES (gen_random_uuid(), 'CU-ILOU0000')$$);
INSERT INTO cid.tenant_customer_membership (tenant_id, id, global_customer_id, join_source)
  VALUES (:A, 'a0000000-0000-0000-0000-000000000501', 'c0000000-0000-0000-0000-000000000001', 'StoreQr');
SELECT t.expect_error('T52 one membership per customer per tenant (DI-062)', '23505',
  $$INSERT INTO cid.tenant_customer_membership (tenant_id, id, global_customer_id, join_source)
    VALUES (core.current_tenant(), gen_random_uuid(), 'c0000000-0000-0000-0000-000000000001', 'Pos')$$);
INSERT INTO cid.consent_record (tenant_id, id, membership_id, purpose, channel, granted, policy_version, source)
  VALUES (:A, gen_random_uuid(), 'a0000000-0000-0000-0000-000000000501', 'Operational', 'Sms', true, 'v1', 'StoreQr');
SELECT t.expect_error('T53 consent history is append-only', '42501',
  $$UPDATE cid.consent_record SET granted = false$$);
SELECT set_config('app.tenant_id', :B, false);
SELECT t.ok('T54 tenant B cannot see tenant A membership of the same global customer',
  (SELECT count(*) FROM cid.tenant_customer_membership) = 0);
RESET ROLE;
SET ROLE rc_reporting;
SELECT t.expect_error('T55 reporting role cannot read global customer data', '42501',
  $$SELECT count(*) FROM cid.global_customer$$);
SELECT t.expect_error('T56 reporting role cannot read tax payload column', '42501',
  $$SELECT payload FROM tax.tax_invoice$$);
RESET ROLE;

------------------------------------------------------------------ tax
\echo '== tax'
SET ROLE rc_app;
SELECT set_config('app.tenant_id', :A, false);
SELECT t.expect_error('T60 content_hash must match payload', 'RC021',
  $$INSERT INTO tax.tax_invoice (tenant_id, id, source_type, source_id, kind, issuing_unit, payload, content_hash, issued_at, deadline_at)
    VALUES (core.current_tenant(), gen_random_uuid(), 'Sale', gen_random_uuid(), 'Original', 'UNIT1', '{"a":1}', '\x00', now(), now() + interval '90 days')$$);
INSERT INTO tax.tax_invoice (tenant_id, id, source_type, source_id, kind, issuing_unit, payload, content_hash, issued_at, deadline_at)
  VALUES (:A, 'a0000000-0000-0000-0000-000000000601', 'Sale', 'a0000000-0000-0000-0000-000000000040', 'Original', 'UNIT1',
          '{"total":"550000"}', digest('{"total": "550000"}', 'sha256'), now(), now() + interval '90 days');
UPDATE tax.tax_invoice SET status = 'Signed', signature = '\x99', signed_at = now(), key_ref = 'k1'
 WHERE id = 'a0000000-0000-0000-0000-000000000601';
SELECT t.expect_error('T61 signed tax invoice content is frozen (DI-071)', 'RC020',
  $$UPDATE tax.tax_invoice SET payload = '{"total":"1"}', content_hash = digest('{"total": "1"}', 'sha256')
    WHERE id = 'a0000000-0000-0000-0000-000000000601'$$);
SELECT t.expect_error('T62 one live original invoice per sale (DI-070)', '23505',
  $$INSERT INTO tax.tax_invoice (tenant_id, id, source_type, source_id, kind, issuing_unit, payload, content_hash, issued_at, deadline_at)
    VALUES (core.current_tenant(), gen_random_uuid(), 'Sale', 'a0000000-0000-0000-0000-000000000040', 'Original', 'UNIT1',
            '{"x":1}', digest('{"x": 1}', 'sha256'), now(), now())$$);

------------------------------------------------------------------ audit
\echo '== audit'
INSERT INTO audit.audit_record (tenant_id, id, actor_kind, action, resource_type, resource_id, seq, prev_hash, hash)
SELECT :A, gen_random_uuid(), 'User', a, 'Sale', 'a0000000-0000-0000-0000-000000000040', 0, '\x', '\x'
FROM unnest(ARRAY['sale.finalize','sale.return','price.change']) a;
SELECT t.ok('T70 audit seq assigned by DB (1..3)', (SELECT array_agg(seq ORDER BY seq) FROM audit.audit_record) = ARRAY[1,2,3]::bigint[]);
SELECT t.expect_error('T71 audit is append-only', '42501', $$UPDATE audit.audit_record SET action = 'x'$$);
SELECT t.expect_error('T72 app role cannot touch chain head', '42501', $$UPDATE audit.chain_head SET last_seq = 0$$);
RESET ROLE;
SET ROLE rc_worker;
SELECT set_config('app.tenant_id', :A, false);
SELECT t.ok('T73 intact chain verifies', audit.verify_chain() IS NULL);
RESET ROLE;
-- simulate tampering by a privileged attacker (superuser bypasses grants; trigger disabled to allow UPDATE)
ALTER TABLE audit.audit_record DISABLE TRIGGER tr_audit_append_only;
UPDATE audit.audit_record SET action = 'price.change.tampered' WHERE tenant_id = :A AND seq = 2;
ALTER TABLE audit.audit_record ENABLE TRIGGER tr_audit_append_only;
SET ROLE rc_worker;
SELECT set_config('app.tenant_id', :A, false);
SELECT t.ok('T74 tampering detected at seq 2', audit.verify_chain() = 2);
RESET ROLE;

------------------------------------------------------------------ identity & access (IAM-001, BR-001)
\echo '== identity & access'
SET ROLE rc_app;
INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES
  ('e0000000-0000-0000-0000-000000000001', 'https://kc/realms/merchant', 'sub-owner', 'مالک'),
  ('e0000000-0000-0000-0000-000000000002', 'https://kc/realms/merchant', 'sub-other', 'دیگری');
SELECT set_config('app.tenant_id', :A, false);
INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES (:A, 'e0000000-0000-0000-0000-000000000001');
SELECT set_config('app.tenant_id', :B, false);
INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES (:B, 'e0000000-0000-0000-0000-000000000001'),
                                                         (:B, 'e0000000-0000-0000-0000-000000000002');
UPDATE iam.tenant_membership SET status = 'Suspended' WHERE user_id = 'e0000000-0000-0000-0000-000000000002';
SELECT set_config('app.tenant_id', '', false);
SELECT set_config('app.user_id', 'e0000000-0000-0000-0000-000000000001', false);
SELECT t.ok('T80 my_memberships returns the caller''s active memberships across tenants (A and B)',
  (SELECT count(*) FROM iam.my_memberships()) = 2);
SELECT t.ok('T81 without a tenant context the membership table itself stays hidden (RLS)',
  (SELECT count(*) FROM iam.tenant_membership) = 0);
SELECT set_config('app.user_id', 'e0000000-0000-0000-0000-000000000002', false);
SELECT t.ok('T82 suspended membership is not returned; other users'' memberships never are',
  (SELECT count(*) FROM iam.my_memberships()) = 0);
SELECT set_config('app.user_id', '', false);
SELECT t.ok('T83 no user context returns nothing', (SELECT count(*) FROM iam.my_memberships()) = 0);
SELECT set_config('app.tenant_id', :A, false);
SELECT t.expect_error('T84 a membership cannot be written for another tenant', '42501',
  format($$INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES (%L, 'e0000000-0000-0000-0000-000000000002')$$, :B));
RESET ROLE;

\echo '== ALL CHECKS PASSED'
