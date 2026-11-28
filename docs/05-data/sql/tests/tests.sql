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
INSERT INTO merch.price_list VALUES (:A, 'a0000000-0000-0000-0000-000000000020', 'پیش‌فرض', true);
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
-- US-041 catalog (CAT-001/002, CAT-011)
SELECT t.ok('T140 Persian search key: Arabic yeh/kaf, Persian digits and half-spaces normalise',
  merch.normalize_fa('شير  ي‌ك ۱ ليتري') = merch.normalize_fa('شیر یک 1 لیتری'));
SELECT t.ok('T164 Persian search key: diacritics (fatha, kasra, tanvin, shadda, superscript alef) are removed (CAT-011)',
  merch.normalize_fa('شیرِ تازهٔ کَمّ چرباً') = 'شیر تازه کم چربا' AND merch.normalize_fa('رحمٰن') = 'رحمن');
SELECT t.ok('T141 the stored search key follows the name', (SELECT name_normalized FROM merch.tenant_product WHERE id = 'a0000000-0000-0000-0000-000000000010') = 'شیر 1 لیتری');
SELECT t.expect_error('T142 a barcode cannot be moved to another variant (history kept)', 'RC016',
  $$UPDATE merch.tenant_product_barcode SET packaging_unit = 'CT' WHERE code_normalized = '06260000000017'$$);
UPDATE merch.tenant_product_barcode SET active = false, removed_at = now() WHERE code_normalized = '06260000000017';
SELECT t.expect_error('T143 a removed barcode cannot be revived (add it again instead)', 'RC016',
  $$UPDATE merch.tenant_product_barcode SET active = true, removed_at = NULL WHERE code_normalized = '06260000000017'$$);
INSERT INTO merch.tenant_product_barcode (tenant_id, id, variant_id, code_normalized, code_original)
  VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000011', '06260000000017', '6260000000017');
SELECT t.ok('T144 after removal the code can be assigned again; both rows stay (CAT-002 history)',
  (SELECT count(*) FROM merch.tenant_product_barcode WHERE code_normalized = '06260000000017') = 2);
SELECT t.expect_error('T145 a standard barcode is stored only as GTIN-14', '23514',
  $$INSERT INTO merch.tenant_product_barcode (tenant_id, id, variant_id, code_normalized, code_original)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000011', '6260000000017', '6260000000017')$$);
SELECT t.expect_error('T146 barcode rows are never deleted', '42501',
  $$DELETE FROM merch.tenant_product_barcode WHERE code_normalized = '06260000000017'$$);
-- US-053 discount rules (CAT-010)
INSERT INTO merch.discount_rule (tenant_id, id, name, target_kind, target_id, percent_bp, valid)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000090', 'جشنواره', 'Product', 'a0000000-0000-0000-0000-000000000010', 1000,
          tstzrange(now() - interval '1 day', NULL));
SELECT t.expect_error('T188 a discount rule cannot be changed, only ended', 'RC025',
  $$UPDATE merch.discount_rule SET percent_bp = 5000 WHERE id = 'a0000000-0000-0000-0000-000000000090'$$);
SELECT t.expect_error('T189 a discount rule cannot be ended in the past', 'RC025',
  $$UPDATE merch.discount_rule SET valid = tstzrange(lower(valid), now() - interval '2 hours') WHERE id = 'a0000000-0000-0000-0000-000000000090'$$);
UPDATE merch.discount_rule SET valid = tstzrange(lower(valid), now()) WHERE id = 'a0000000-0000-0000-0000-000000000090';
SELECT t.expect_error('T190 an ended rule is not reopened', 'RC025',
  $$UPDATE merch.discount_rule SET valid = tstzrange(lower(valid), now() + interval '1 day') WHERE id = 'a0000000-0000-0000-0000-000000000090'$$);
SELECT t.expect_error('T191 a discount is 0.01% to 100%', '23514',
  $$INSERT INTO merch.discount_rule (tenant_id, id, name, target_kind, target_id, percent_bp, valid)
    VALUES (core.current_tenant(), gen_random_uuid(), 'x', 'Product', gen_random_uuid(), 10001, tstzrange(now(), NULL))$$);
SELECT t.expect_error('T192 a manual discount cap is 0-100%', '23514',
  $$INSERT INTO merch.manual_discount_cap (tenant_id, role, cap_bp) VALUES (core.current_tenant(), 'Cashier', -1)$$);
-- US-051 variant matrix (CAT-008)
INSERT INTO merch.tenant_product (tenant_id, id, name, base_unit_code, axes)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000080', 'تی‌شرت', 'EA', '{color,size}');
INSERT INTO merch.tenant_variant (tenant_id, id, product_id, sku, axis_values)
  VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000080', 'TEE-R-M', '{"color":"قرمز","size":"M"}');
SELECT t.expect_error('T183 a matrix variant must name every axis of its product', 'RC024',
  $$INSERT INTO merch.tenant_variant (tenant_id, id, product_id, sku, axis_values)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000080', 'TEE-R', '{"color":"قرمز"}')$$);
SELECT t.expect_error('T184 and no axis the product does not have', 'RC024',
  $$INSERT INTO merch.tenant_variant (tenant_id, id, product_id, sku, axis_values)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000080', 'TEE-R-M-X', '{"color":"قرمز","size":"M","model":"X"}')$$);
SELECT t.expect_error('T185 an axis value is not blank', 'RC024',
  $$INSERT INTO merch.tenant_variant (tenant_id, id, product_id, sku, axis_values)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000080', 'TEE-B', '{"color":" ","size":"L"}')$$);
SELECT t.expect_error('T186 the axes are fixed once the product has variants', 'RC024',
  $$UPDATE merch.tenant_product SET axes = '{color}' WHERE id = 'a0000000-0000-0000-0000-000000000080'$$);
SELECT t.expect_error('T187 only color, size and model are axes', '23514',
  $$INSERT INTO merch.tenant_product (tenant_id, id, name, base_unit_code, axes) VALUES (core.current_tenant(), gen_random_uuid(), 'x', 'EA', '{flavor}')$$);
-- US-045 weighted barcode profiles (CAT-007, DI-084)
INSERT INTO merch.weighted_barcode_profile (tenant_id, id, prefix, item_code_length, value_type, value_length, decimals)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000070', '21', 5, 'Weight', 5, 3);
SELECT t.expect_error('T168 a prefix that starts another profile''s prefix is rejected (DI-084)', 'RC022',
  $$INSERT INTO merch.weighted_barcode_profile (tenant_id, id, prefix, item_code_length, value_type, value_length, decimals)
    VALUES (core.current_tenant(), gen_random_uuid(), '2', 6, 'Price', 5, 0)$$);
SELECT t.expect_error('T169 an equal prefix is rejected too (DI-084)', 'RC022',
  $$INSERT INTO merch.weighted_barcode_profile (tenant_id, id, prefix, item_code_length, value_type, value_length, decimals)
    VALUES (core.current_tenant(), gen_random_uuid(), '21', 5, 'Price', 5, 0)$$);
SELECT t.expect_error('T170 the parts must fill the 12 data digits of an EAN-13', '23514',
  $$INSERT INTO merch.weighted_barcode_profile (tenant_id, id, prefix, item_code_length, value_type, value_length, decimals)
    VALUES (core.current_tenant(), gen_random_uuid(), '22', 5, 'Price', 4, 0)$$);
INSERT INTO merch.weighted_barcode_profile (tenant_id, id, prefix, item_code_length, value_type, value_length, decimals, has_inner_check_digit)
  VALUES (core.current_tenant(), gen_random_uuid(), '22', 5, 'Price', 4, 0, true);
SELECT t.ok('T171 a disjoint prefix is accepted', (SELECT count(*) FROM merch.weighted_barcode_profile) = 2);
SELECT t.expect_error('T11 overlapping price validity (DI-083)', '23P01',
  $$INSERT INTO merch.price_list_item (tenant_id, id, price_list_id, variant_id, unit_price, valid)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000020',
            'a0000000-0000-0000-0000-000000000011', 600000, tstzrange('2026-12-01', NULL))$$);
-- US-052 price history (CAT-009, AB-06)
SELECT t.expect_error('T172 one default price list per tenant', '23505',
  $$INSERT INTO merch.price_list VALUES (core.current_tenant(), gen_random_uuid(), 'دوم', true)$$);
SELECT t.expect_error('T173 a price that applied cannot be rewritten', 'RC023',
  $$UPDATE merch.price_list_item SET unit_price = 1 WHERE variant_id = 'a0000000-0000-0000-0000-000000000011'$$);
SELECT t.expect_error('T174 a version cannot be ended in the past (what was charged stays)', 'RC023',
  $$UPDATE merch.price_list_item SET valid = tstzrange(lower(valid), now() - interval '1 day') WHERE variant_id = 'a0000000-0000-0000-0000-000000000011'$$);
UPDATE merch.price_list_item SET valid = tstzrange(lower(valid), now() + interval '1 day') WHERE variant_id = 'a0000000-0000-0000-0000-000000000011';
INSERT INTO merch.price_list_item (tenant_id, id, price_list_id, variant_id, unit_price, valid)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000021', 'a0000000-0000-0000-0000-000000000020',
          'a0000000-0000-0000-0000-000000000011', 550000, tstzrange(now() + interval '1 day', NULL));
UPDATE merch.price_list_item SET unit_price = 540000 WHERE id = 'a0000000-0000-0000-0000-000000000021';
SELECT t.ok('T175 a version can be ended in the future, and a version not yet started can be corrected',
  (SELECT unit_price FROM merch.price_list_item WHERE id = 'a0000000-0000-0000-0000-000000000021') = 540000);
SELECT t.expect_error('T176 an ended version cannot be reopened or extended', 'RC023',
  $$UPDATE merch.price_list_item SET valid = tstzrange(lower(valid), NULL) WHERE unit_price = 500000 AND variant_id = 'a0000000-0000-0000-0000-000000000011'$$);
SELECT t.expect_error('T177 price versions are never deleted', '42501',
  $$DELETE FROM merch.price_list_item WHERE id = 'a0000000-0000-0000-0000-000000000021'$$);
SELECT t.expect_error('T12 negative available stock rejected by DB (DI-031)', '23514',
  $$UPDATE inv.stock_balance SET on_hand = on_hand - 2 WHERE variant_id = 'a0000000-0000-0000-0000-000000000011'$$);
INSERT INTO inv.posted_source (tenant_id, source_type, source_id) VALUES (:A, 'Sale', 'a0000000-0000-0000-0000-000000000040');
SELECT t.expect_error('T13 same source document cannot post stock twice (BR-032)', '23505',
  $$INSERT INTO inv.posted_source (tenant_id, source_type, source_id) VALUES (core.current_tenant(), 'Sale', 'a0000000-0000-0000-0000-000000000040')$$);
INSERT INTO inv.stock_movement (tenant_id, id, recorded_at, effective_at, warehouse_id, variant_id, movement_type,
                                qty_base, unit_cost, total_cost, source_type, source_id, source_line)
VALUES (:A, gen_random_uuid(), '2026-11-22 10:00+00', '2026-11-22 10:00+00', 'a0000000-0000-0000-0000-000000000003',
        'a0000000-0000-0000-0000-000000000011', 'SaleIssue', -1, 300000, -300000, 'Sale', 'a0000000-0000-0000-0000-000000000040', 1);
SELECT t.expect_error('T178 a movement in cartons must equal its base quantity (INV-005, BR-044)', '23514',
  $$INSERT INTO inv.stock_movement (tenant_id, id, effective_at, warehouse_id, variant_id, movement_type, qty_base, unit_cost, total_cost,
      source_type, source_id, source_line, entered_qty, entered_unit, unit_factor)
    VALUES (core.current_tenant(), gen_random_uuid(), now(), 'a0000000-0000-0000-0000-000000000003', 'a0000000-0000-0000-0000-000000000011',
            'GoodsReceipt', 47, 1000, 47000, 'GoodsReceipt', gen_random_uuid(), 1, 2, 'CT', 24)$$);
SELECT t.expect_error('T179 value moves with the quantity: an issue cannot add value', '23514',
  $$INSERT INTO inv.stock_movement (tenant_id, id, effective_at, warehouse_id, variant_id, movement_type, qty_base, unit_cost, total_cost,
      source_type, source_id, source_line)
    VALUES (core.current_tenant(), gen_random_uuid(), now(), 'a0000000-0000-0000-0000-000000000003', 'a0000000-0000-0000-0000-000000000011',
            'SaleIssue', -1, 1000, 1000, 'Sale', gen_random_uuid(), 1)$$);
SELECT t.expect_error('T180 a tenant-level negative-stock policy names the tenant itself', '23514',
  $$INSERT INTO inv.negative_stock_policy (tenant_id, scope_type, scope_id, policy) VALUES (core.current_tenant(), 'Tenant', gen_random_uuid(), 'Forbid')$$);
SELECT t.expect_error('T181 the application cannot create partitions', '42501',
  $$SELECT inv.ensure_movement_partitions(1)$$);
-- US-063 adjustments (INV-002, BR-033)
SELECT t.expect_error('T193 an adjustment for «Other» needs a note (INV-002)', '23514',
  $$INSERT INTO inv.stock_adjustment (tenant_id, id, warehouse_id, reason, note, line_count, value, shrinkage, overage, journal_entry_id, created_by)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000003', 'Other', ' ', 1, 0, 0, 0, NULL, gen_random_uuid())$$);
SELECT t.expect_error('T194 a valued adjustment always has its journal entry', '23514',
  $$INSERT INTO inv.stock_adjustment (tenant_id, id, warehouse_id, reason, line_count, value, shrinkage, overage, journal_entry_id, created_by)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000003', 'Damage', 1, 5000, 5000, 0, NULL, gen_random_uuid())$$);
SELECT t.expect_error('T195 the value is the shrinkage plus the overage', '23514',
  $$INSERT INTO inv.stock_adjustment (tenant_id, id, warehouse_id, reason, line_count, value, shrinkage, overage, journal_entry_id, created_by)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000003', 'Theft', 1, 100, 5000, 0, gen_random_uuid(), gen_random_uuid())$$);
INSERT INTO inv.stock_adjustment (tenant_id, id, warehouse_id, reason, line_count, value, shrinkage, overage, journal_entry_id, created_by)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000095', 'a0000000-0000-0000-0000-000000000003', 'Expiry', 1, 5000, 5000, 0,
          gen_random_uuid(), gen_random_uuid());
SELECT t.expect_error('T196 an adjustment is never changed by the app (AB-05)', '42501',
  $$UPDATE inv.stock_adjustment SET value = 0, shrinkage = 0, journal_entry_id = NULL WHERE id = 'a0000000-0000-0000-0000-000000000095'$$);
-- US-071 suppliers and goods receipts (PRC-001/002)
INSERT INTO inv.supplier (tenant_id, id, name, national_id, created_by)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000096', 'پخش البرز', '10101010101', gen_random_uuid());
SELECT t.expect_error('T197 a national id belongs to one supplier of the store', '23505',
  $$INSERT INTO inv.supplier (tenant_id, id, name, national_id, created_by) VALUES (core.current_tenant(), gen_random_uuid(), 'دیگری', '10101010101', gen_random_uuid())$$);
SELECT t.expect_error('T198 a receipt total is its net cost plus input VAT', '23514',
  $$INSERT INTO inv.goods_receipt (tenant_id, id, supplier_id, warehouse_id, effective_at, net_total, input_vat, total, journal_entry_id, created_by)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000096', 'a0000000-0000-0000-0000-000000000003', now(),
            1000, 100, 1000, gen_random_uuid(), gen_random_uuid())$$);
SELECT t.expect_error('T199 a valued receipt always has its journal entry (PRC-002)', '23514',
  $$INSERT INTO inv.goods_receipt (tenant_id, id, supplier_id, warehouse_id, effective_at, net_total, input_vat, total, journal_entry_id, created_by)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000096', 'a0000000-0000-0000-0000-000000000003', now(),
            1000, 0, 1000, NULL, gen_random_uuid())$$);
INSERT INTO inv.goods_receipt (tenant_id, id, supplier_id, warehouse_id, effective_at, net_total, input_vat, total, journal_entry_id, created_by)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000097', 'a0000000-0000-0000-0000-000000000096', 'a0000000-0000-0000-0000-000000000003',
          now(), 2500, 0, 2500, gen_random_uuid(), gen_random_uuid());
SELECT t.expect_error('T200 a line costs its quantity times its unit cost less its discount', '23514',
  $$INSERT INTO inv.goods_receipt_line (tenant_id, receipt_id, line_no, variant_id, qty, unit, unit_factor, unit_cost, discount, line_cost)
    VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000097', 1, 'a0000000-0000-0000-0000-000000000011', 2.5, 'EA', 1, 1001, 0, 2500)$$);
INSERT INTO inv.goods_receipt_line (tenant_id, receipt_id, line_no, variant_id, qty, unit, unit_factor, unit_cost, discount, line_cost)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000097', 1, 'a0000000-0000-0000-0000-000000000011', 2.5, 'EA', 1, 1001, 3, 2500);
SELECT t.expect_error('T201 a posted receipt is never changed by the app', '42501',
  $$UPDATE inv.goods_receipt_line SET unit_cost = 0 WHERE receipt_id = 'a0000000-0000-0000-0000-000000000097'$$);
-- US-064 transfers (INV-008)
SELECT t.expect_error('T219 a transfer goes from one warehouse to another', '23514',
  $$INSERT INTO inv.stock_transfer (tenant_id, id, from_warehouse_id, to_warehouse_id, line_count, value, created_by)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000003', 'a0000000-0000-0000-0000-000000000003', 1, 0, gen_random_uuid())$$);
SELECT t.expect_error('T220 a transfer is never changed by the app', '42501',
  $$UPDATE inv.stock_transfer SET value = 0$$);
-- US-066 reorder points (INV-010)
SELECT t.expect_error('T221 a reorder point is not negative', '23514',
  $$INSERT INTO inv.reorder_point (tenant_id, warehouse_id, variant_id, reorder_point, changed_by)
    VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000003', 'a0000000-0000-0000-0000-000000000011', -1, gen_random_uuid())$$);
SELECT t.expect_error('T222 a reorder point belongs to a warehouse of the store', '23503',
  $$INSERT INTO inv.reorder_point (tenant_id, warehouse_id, variant_id, reorder_point, changed_by)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000011', 5, gen_random_uuid())$$);
-- US-062 reservations (INV-004.1)
INSERT INTO inv.stock_reservation (tenant_id, id, warehouse_id, variant_id, qty_base, source_type, source_id, expires_at)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000098', 'a0000000-0000-0000-0000-000000000003', 'a0000000-0000-0000-0000-000000000011',
          1, 'Sale', 'a0000000-0000-0000-0000-000000000099', now() + interval '15 minutes');
SELECT t.expect_error('T223 one active hold per source and item', '23505',
  $$INSERT INTO inv.stock_reservation (tenant_id, id, warehouse_id, variant_id, qty_base, source_type, source_id, expires_at)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000003', 'a0000000-0000-0000-0000-000000000011',
            1, 'Sale', 'a0000000-0000-0000-0000-000000000099', now() + interval '15 minutes')$$);
SELECT t.expect_error('T224 a reservation''s quantity never changes (RC029)', 'RC029',
  $$UPDATE inv.stock_reservation SET status = 'Active' WHERE id = 'a0000000-0000-0000-0000-000000000098'$$);
UPDATE inv.stock_reservation SET status = 'Released', closed_at = now() WHERE id = 'a0000000-0000-0000-0000-000000000098';
SELECT t.expect_error('T225 a closed reservation stays closed (RC029)', 'RC029',
  $$UPDATE inv.stock_reservation SET status = 'Consumed' WHERE id = 'a0000000-0000-0000-0000-000000000098'$$);
-- US-065 stock counts (INV-009)
INSERT INTO inv.inventory_count (tenant_id, id, warehouse_id, snapshot_at, created_by)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000060', 'a0000000-0000-0000-0000-000000000003', now(), gen_random_uuid());
INSERT INTO inv.count_entry (tenant_id, count_id, client_batch_id, variant_id, qty, counted_by)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000060', 'batch-0001', 'a0000000-0000-0000-0000-000000000011', 3, gen_random_uuid());
SELECT t.expect_error('T229 one count at a time per warehouse', '23505',
  $$INSERT INTO inv.inventory_count (tenant_id, id, warehouse_id, snapshot_at, created_by)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000003', now(), gen_random_uuid())$$);
SELECT t.expect_error('T230 a counted batch is not changed (no grant; the trigger refuses it too)', '42501',
  $$UPDATE inv.count_entry SET qty = 30 WHERE count_id = 'a0000000-0000-0000-0000-000000000060'$$);
UPDATE inv.inventory_count SET status = 'Closed', closed_at = now(), closed_by = gen_random_uuid() WHERE id = 'a0000000-0000-0000-0000-000000000060';
SELECT t.expect_error('T231 nothing more is counted once the count is closed (RC032)', 'RC032',
  $$INSERT INTO inv.count_entry (tenant_id, count_id, client_batch_id, variant_id, qty, counted_by)
    VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000060', 'batch-0002', 'a0000000-0000-0000-0000-000000000011', 1, gen_random_uuid())$$);
SELECT t.expect_error('T232 a closed count is not reopened (RC032)', 'RC032',
  $$UPDATE inv.inventory_count SET status = 'Open', closed_at = NULL, closed_by = NULL WHERE id = 'a0000000-0000-0000-0000-000000000060'$$);
-- US-072 supplier payments and purchase returns (PRC-003/004)
SELECT t.expect_error('T233 a supplier payment is a positive amount, in cash or by bank', '23514',
  $$INSERT INTO inv.supplier_payment (tenant_id, id, supplier_id, amount, method, journal_entry_id, created_by)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000096', 0, 'Cash', gen_random_uuid(), gen_random_uuid())$$);
INSERT INTO inv.supplier_payment (tenant_id, id, supplier_id, amount, method, reference, journal_entry_id, created_by)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000061', 'a0000000-0000-0000-0000-000000000096', 1000, 'Bank', 'TR-1', gen_random_uuid(), gen_random_uuid());
SELECT t.expect_error('T234 a supplier payment is never changed by the app', '42501',
  $$UPDATE inv.supplier_payment SET amount = 1 WHERE id = 'a0000000-0000-0000-0000-000000000061'$$);
SELECT t.expect_error('T235 a valued purchase return always has its journal entry (PRC-004)', '23514',
  $$INSERT INTO inv.purchase_return (tenant_id, id, supplier_id, warehouse_id, cost, agreed_amount, created_by)
    VALUES (core.current_tenant(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000096', 'a0000000-0000-0000-0000-000000000003', 900, 1000, gen_random_uuid())$$);
INSERT INTO inv.purchase_return (tenant_id, id, supplier_id, warehouse_id, cost, agreed_amount, journal_entry_id, created_by)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000062', 'a0000000-0000-0000-0000-000000000096', 'a0000000-0000-0000-0000-000000000003',
          900, 1000, gen_random_uuid(), gen_random_uuid());
SELECT t.expect_error('T236 a posted purchase return is never changed by the app', '42501',
  $$UPDATE inv.purchase_return SET agreed_amount = 1 WHERE id = 'a0000000-0000-0000-0000-000000000062'$$);
-- US-172 receipt templates (BR-086, DI-085)
SELECT t.ok('T240 every industry starts with template v1 and its locked fields',
  (SELECT count(*) FROM platform.receipt_template_industry WHERE version = 1 AND 'invoiceNo' = ANY(locked_fields)) = 3);
SELECT t.expect_error('T241 a published template version is never changed', '42501',
  $$UPDATE platform.receipt_template_industry SET legal_text = 'دیگر' WHERE industry_code = 'general'$$);
SELECT t.expect_error('T242 only known receipt fields can be locked', '23514',
  $$INSERT INTO platform.receipt_template_industry (industry_code, version, legal_text, locked_fields, reason, created_by)
    VALUES ('general', 2, 'متن', ARRAY['invoiceNo','favouriteColour'], 'آزمون', 'test')$$);
SELECT t.expect_error('T243 the app cannot add an industry (reference data)', '42501',
  $$INSERT INTO platform.industry_profile (code, name) VALUES ('bakery', 'نانوایی')$$);
INSERT INTO merch.receipt_template_override (tenant_id, footer_text, updated_by) VALUES (core.current_tenant(), 'با سپاس از خرید شما', gen_random_uuid());
-- US-043 global catalog (CAT-004/005)
SELECT t.expect_error('T244 the app does not write the global catalog (moderation does)', '42501',
  $$INSERT INTO catalog.catalog_product (id, name) VALUES (gen_random_uuid(), 'کالای فروشگاه')$$);
SELECT t.expect_error('T245 a provider cache keeps only descriptive fields', '23514',
  $$INSERT INTO catalog.catalog_source_record (source, gtin14, found, fields, confidence, license, attribution)
    VALUES ('open_food_facts', '06260000000013', true, '{"name":"x","price":"1000"}', 0.5, 'ODbL', 'Open Food Facts')$$);
INSERT INTO catalog.catalog_source_record (source, gtin14, found, fields, confidence, license, attribution)
  VALUES ('open_food_facts', '06260000000013', true, '{"name":"شیر"}', 0.6, 'ODbL', 'Open Food Facts');
SELECT t.ok('T246 every store reads the global catalog (global reference data, no RLS)',
  (SELECT count(*) FROM catalog.catalog_source_record WHERE gtin14 = '06260000000013') = 1);
-- US-092 receipt snapshot and links (BR-086, CUS-009)
SELECT t.ok('T247 the app holds no way to list receipt links of other stores (RLS) and finds one only by its token hash',
  (SELECT count(*) FROM sales.receipt_link_target(decode(repeat('ab', 32), 'hex'))) = 0);
SELECT t.expect_error('T248 a receipt link token is a 32-byte hash', '23514',
  $$INSERT INTO sales.receipt_link (tenant_id, token_hash, sale_id, created_by) VALUES (core.current_tenant(), '\x01', gen_random_uuid(), gen_random_uuid())$$);
SELECT t.expect_error('T251 a suggestion to the global catalog has only a public barcode (no internal code)', '23514',
  $$INSERT INTO catalog.catalog_suggestion (id, gtin14, name, source_hash) VALUES (gen_random_uuid(), '02000000000017', 'کالای وزنی', decode(repeat('ab', 32), 'hex'))$$);
SELECT t.expect_error('T252 a store cannot publish or reject a suggestion (moderation does)', '42501',
  $$UPDATE catalog.catalog_suggestion SET status = 'Published'$$);
-- US-162 integration keys (INT-001)
SELECT t.expect_error('T253 an integration key has only the MVP read scopes', '23514',
  $$INSERT INTO platform.integration_key (tenant_id, id, prefix, secret_hash, name, scopes, created_by, expires_at)
    VALUES (core.current_tenant(), gen_random_uuid(), 'abcdefghij', decode(repeat('ab', 32), 'hex'), 'شریک', '{sales.write}', gen_random_uuid(), now() + interval '30 days')$$);
SELECT t.expect_error('T254 an integration key lasts at most a year', '23514',
  $$INSERT INTO platform.integration_key (tenant_id, id, prefix, secret_hash, name, scopes, created_by, expires_at)
    VALUES (core.current_tenant(), gen_random_uuid(), 'abcdefghij', decode(repeat('ab', 32), 'hex'), 'شریک', '{catalog.read}', gen_random_uuid(), now() + interval '2 years')$$);
SELECT t.expect_error('T255 what a store suggested to the global catalog cannot be rewritten (DI-093)', '42501',
  $$UPDATE catalog.catalog_suggestion SET name = 'دیگر'$$);
SELECT t.expect_error('T256 the app cannot decide a suggestion except through moderation (US-047)', '42501',
  $$UPDATE catalog.catalog_suggestion SET status = 'Rejected', decided_at = now(), decided_by = 'iss|x', decision_reason = 'بدون ناظر'$$);
INSERT INTO catalog.catalog_suggestion (id, gtin14, name, source_hash) VALUES
  ('0193a000-0000-7000-8000-000000000471', '06260000000470', 'نمک آزمون', decode(repeat('a1', 32), 'hex')),
  ('0193a000-0000-7000-8000-000000000472', '06260000000470', 'نمک', decode(repeat('a2', 32), 'hex'));
CREATE TEMP TABLE t257 AS SELECT * FROM catalog.decide_suggestion('0193a000-0000-7000-8000-000000000471', 'Publish', 'نمک تصفیه', NULL, NULL, 'تأیید', 'iss|mod');
SELECT t.ok('T257 publishing a suggestion creates one global product and settles every suggestion for its barcode (US-047)',
  (SELECT outcome FROM t257) = 'Published'
  AND (SELECT count(*) FROM catalog.catalog_suggestion WHERE gtin14 = '06260000000470' AND status = 'Published') = 2
  AND (SELECT p.name FROM catalog.global_barcode g JOIN catalog.catalog_product p ON p.id = g.catalog_product_id WHERE g.gtin14 = '06260000000470') = 'نمک تصفیه');
SELECT t.ok('T258 a decided suggestion is not decided again',
  (SELECT outcome FROM catalog.decide_suggestion('0193a000-0000-7000-8000-000000000472', 'Reject', NULL, NULL, NULL, 'دوباره', 'iss|mod')) = 'AlreadyDecided');
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
-- US-081 shifts and cash (UC-06)
INSERT INTO platform.register (tenant_id, id, branch_id, code) VALUES (:A, 'a0000000-0000-0000-0000-000000000052', 'a0000000-0000-0000-0000-000000000001', 'R2');
SELECT t.expect_error('T147 a shift needs a register of this tenant', '23503',
  $$INSERT INTO sales.shift (tenant_id, id, register_id, cashier_id, opening_cash) VALUES (core.current_tenant(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 0)$$);
INSERT INTO sales.shift (tenant_id, id, register_id, cashier_id, opening_cash)
  VALUES (:A, 'a0000000-0000-0000-0000-000000000053', 'a0000000-0000-0000-0000-000000000052', 'e0000000-0000-0000-0000-000000000002', 1000000);
INSERT INTO sales.cash_movement (tenant_id, id, shift_id, direction, amount, reason, recorded_by) VALUES
  (:A, gen_random_uuid(), 'a0000000-0000-0000-0000-000000000053', 'In', 300000, 'پول خرد', 'e0000000-0000-0000-0000-000000000002'),
  (:A, gen_random_uuid(), 'a0000000-0000-0000-0000-000000000053', 'Out', 500000, 'واریز به بانک', 'e0000000-0000-0000-0000-000000000002');
SELECT t.ok('T148 the shift knows its branch and expected cash = opening + in - out (+ cash sales)',
  (SELECT branch_id FROM sales.shift WHERE id = 'a0000000-0000-0000-0000-000000000053') = 'a0000000-0000-0000-0000-000000000001'
  AND sales.shift_expected_cash('a0000000-0000-0000-0000-000000000053') = 800000);
SELECT t.expect_error('T149 the variance must equal counted - expected', '23514',
  $$UPDATE sales.shift SET status = 'Closed', expected_cash = 800000, counted_cash = 700000, variance = 0, closed_at = now(), closed_by = 'e0000000-0000-0000-0000-000000000002'
    WHERE id = 'a0000000-0000-0000-0000-000000000053'$$);
UPDATE sales.shift SET status = 'Closed', expected_cash = 800000, counted_cash = 700000, variance = -100000, variance_status = 'PendingApproval',
  closed_at = now(), closed_by = 'e0000000-0000-0000-0000-000000000002' WHERE id = 'a0000000-0000-0000-0000-000000000053';
SELECT t.expect_error('T150 no cash moves in or out of a closed shift', 'RC017',
  format($$INSERT INTO sales.cash_movement (tenant_id, id, shift_id, direction, amount, reason, recorded_by) VALUES (%L, gen_random_uuid(), 'a0000000-0000-0000-0000-000000000053', 'Out', 1, 'بعد از بستن', gen_random_uuid())$$, :A));
SELECT t.expect_error('T151 the count of a closed shift cannot be changed', 'RC017',
  $$UPDATE sales.shift SET counted_cash = 800000, variance = 0 WHERE id = 'a0000000-0000-0000-0000-000000000053'$$);
SELECT t.expect_error('T152 a cashier cannot approve the variance of their own shift (SoD-01)', '23514',
  $$UPDATE sales.shift SET variance_status = 'Approved', variance_decided_by = 'e0000000-0000-0000-0000-000000000002', variance_decided_at = now() WHERE id = 'a0000000-0000-0000-0000-000000000053'$$);
SELECT t.expect_error('T153 cash movements are append-only', '42501',
  $$UPDATE sales.cash_movement SET amount = 1$$);
SELECT t.expect_error('T21 finalized sale must have invoice number', '23514',
  $$UPDATE sales.sale SET status = 'Finalized', finalized_at = now() WHERE id = 'a0000000-0000-0000-0000-000000000040'$$);
UPDATE sales.sale SET status = 'Finalized', finalized_at = now(), fiscal_year = 1405, invoice_no = 'B1-1405-000001'
 WHERE id = 'a0000000-0000-0000-0000-000000000040';
SELECT t.expect_error('T22 finalized sale is immutable (DI-003)', 'RC002',
  $$UPDATE sales.sale SET total = 1 WHERE id = 'a0000000-0000-0000-0000-000000000040'$$);
UPDATE sales.sale SET return_status = 'Partial', version = version + 1 WHERE id = 'a0000000-0000-0000-0000-000000000040';
SELECT t.ok('T23 return bookkeeping still allowed on finalized sale',
  (SELECT return_status FROM sales.sale WHERE id = 'a0000000-0000-0000-0000-000000000040') = 'Partial');
UPDATE sales.sale SET receipt_template = 'general@1', receipt_footer = 'با سپاس', print_count = 1 WHERE id = 'a0000000-0000-0000-0000-000000000040';
SELECT t.expect_error('T249 a receipt snapshot is written once (BR-086)', 'RC034',
  $$UPDATE sales.sale SET receipt_footer = 'دیگر' WHERE id = 'a0000000-0000-0000-0000-000000000040'$$);
SELECT t.expect_error('T250 a print count never goes down', 'RC034',
  $$UPDATE sales.sale SET print_count = 0 WHERE id = 'a0000000-0000-0000-0000-000000000040'$$);
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

-- US-082 cart (SAL-001, SAL-010)
INSERT INTO sales.sale (tenant_id, id, branch_id, register_id, shift_id, cashier_id, terminal_id, client_draft_id, pricing_mode)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000042', 'a0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-000000000002',
          'a0000000-0000-0000-0000-000000000030', gen_random_uuid(), gen_random_uuid(), 'draft-0042', 'TaxExclusive');
INSERT INTO sales.sale_line (tenant_id, sale_id, line_no, variant_id, qty, unit, unit_factor, unit_price, gross, discount, discount_source, discount_bp,
                             discount_rule_id, net, tax_rate_bp, tax, line_total)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000042', 1, 'a0000000-0000-0000-0000-000000000011', 1, 'CT', 24, 1200000, 1200000,
          120000, 'Rule', 1000, gen_random_uuid(), 1080000, 1000, 108000, 1188000);
SELECT t.expect_error('T202 a discount is never more than the line', '23514',
  $$UPDATE sales.sale_line SET discount = 1200001 WHERE sale_id = 'a0000000-0000-0000-0000-000000000042'$$);
SELECT t.expect_error('T203 an automatic discount names its rule; a line without discount has no source', '23514',
  $$UPDATE sales.sale_line SET discount_rule_id = NULL WHERE sale_id = 'a0000000-0000-0000-0000-000000000042'$$,
  $$UPDATE sales.sale_line SET discount = 0, discount_rule_id = NULL WHERE sale_id = 'a0000000-0000-0000-0000-000000000042'$$);
UPDATE sales.sale SET status = 'Held' WHERE id = 'a0000000-0000-0000-0000-000000000042';
SELECT t.expect_error('T204 lines of a held cart do not change (resume it first)', 'RC026',
  $$UPDATE sales.sale_line SET qty = 2 WHERE sale_id = 'a0000000-0000-0000-0000-000000000042'$$);
SELECT t.expect_error('T205 a client draft id is 8-64 of A-Z a-z 0-9 _ -', '23514',
  $$INSERT INTO sales.sale (tenant_id, id, branch_id, register_id, shift_id, cashier_id, terminal_id, client_draft_id, pricing_mode)
    VALUES (core.current_tenant(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000030', gen_random_uuid(),
            gen_random_uuid(), 'x y', 'TaxExclusive')$$);
SELECT t.expect_error('T206 a sale says which channel made it: POS, Admin or API (SAL-010)', '23514',
  $$INSERT INTO sales.sale (tenant_id, id, branch_id, register_id, shift_id, cashier_id, terminal_id, client_draft_id, pricing_mode, channel)
    VALUES (core.current_tenant(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'a0000000-0000-0000-0000-000000000030', gen_random_uuid(),
            gen_random_uuid(), 'draft-0206', 'TaxExclusive', 'Phone')$$);

-- US-101 payments and tenders (PAY-001/002, SAL-006)
INSERT INTO sales.sale (tenant_id, id, branch_id, register_id, shift_id, cashier_id, terminal_id, client_draft_id, pricing_mode, total)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000043', 'a0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-000000000002',
          'a0000000-0000-0000-0000-000000000030', gen_random_uuid(), gen_random_uuid(), 'draft-0043', 'TaxExclusive', 1000);
INSERT INTO sales.sale_line (tenant_id, sale_id, line_no, variant_id, qty, unit_price, gross, net, tax_rate_bp, tax, line_total)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000043', 1, 'a0000000-0000-0000-0000-000000000011', 1, 1000, 1000, 1000, 0, 0, 1000);
INSERT INTO pay.payment (tenant_id, id, method, status, amount, reference_no, terminal_no, business_date, recorded_by)
  VALUES (core.current_tenant(), gen_random_uuid(), 'ExternalCardTerminal', 'ConfirmedManual', 600, '123456789012', '8899', '2026-11-24', gen_random_uuid());
SELECT t.expect_error('T207 a card reference is taken once per bank terminal and day (PAY-002.1)', '23505',
  $$INSERT INTO pay.payment (tenant_id, id, method, status, amount, reference_no, terminal_no, business_date, recorded_by)
    VALUES (core.current_tenant(), gen_random_uuid(), 'ExternalCardTerminal', 'ConfirmedManual', 400, '123456789012', '8899', '2026-11-24', gen_random_uuid())$$);
INSERT INTO pay.payment (tenant_id, id, method, status, amount, reference_no, terminal_no, business_date, recorded_by, override_approval_id)
  VALUES (core.current_tenant(), gen_random_uuid(), 'ExternalCardTerminal', 'ConfirmedManual', 400, '123456789012', '8899', '2026-11-24', gen_random_uuid(), gen_random_uuid());
SELECT t.ok('T208 a manager-approved override may repeat it; another day or terminal is a different reference', true);
-- US-105 / PAY-002.2 settlement imports
INSERT INTO pay.payment (tenant_id, id, method, status, amount, reference_no, terminal_no, business_date, recorded_by)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-0000000000b1', 'ExternalCardTerminal', 'ConfirmedManual', 600000, '777788889999', '8899', '2026-11-24', gen_random_uuid());
INSERT INTO pay.settlement_import (tenant_id, id, file_name, file_sha256, line_count, matched_count, matched_amount, fees, journal_entry_id, imported_by)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-0000000000b2', 'settle.csv', sha256('one'), 2, 1, 600000, 3000, gen_random_uuid(), gen_random_uuid());
INSERT INTO pay.settlement_line (tenant_id, import_id, line_no, terminal_no, reference_no, transaction_date, amount, fee, outcome, payment_id)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-0000000000b2', 1, '8899', '777788889999', '2026-11-24', 600000, 3000, 'Matched', 'a0000000-0000-0000-0000-0000000000b1'),
         (core.current_tenant(), 'a0000000-0000-0000-0000-0000000000b2', 2, '8899', '111122223333', '2026-11-24', 5000, 0, 'NotFound', NULL);
SELECT t.expect_error('T259 a payment is settled once, whichever file brings it (PAY-002.2)', '23505',
  $$INSERT INTO pay.settlement_import (tenant_id, id, file_name, file_sha256, line_count, matched_count, matched_amount, fees, journal_entry_id, imported_by)
      VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-0000000000b3', 'again.csv', sha256('two'), 1, 1, 600000, 0, gen_random_uuid(), gen_random_uuid())$$,
  $$INSERT INTO pay.settlement_line (tenant_id, import_id, line_no, reference_no, transaction_date, amount, outcome, payment_id)
      VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-0000000000b3', 1, '777788889999', '2026-11-24', 600000, 'Matched', 'a0000000-0000-0000-0000-0000000000b1')$$);
SELECT t.expect_error('T260 the same settlement file is not imported twice', '23505',
  $$INSERT INTO pay.settlement_import (tenant_id, id, file_name, file_sha256, line_count, matched_count, matched_amount, fees, imported_by)
      VALUES (core.current_tenant(), gen_random_uuid(), 'copy.csv', sha256('one'), 1, 0, 0, 0, gen_random_uuid())$$);
SELECT t.expect_error('T261 an import with matched lines always carries its journal entry (J4)', '23514',
  $$INSERT INTO pay.settlement_import (tenant_id, id, file_name, file_sha256, line_count, matched_count, matched_amount, fees, imported_by)
      VALUES (core.current_tenant(), gen_random_uuid(), 'x.csv', sha256('three'), 1, 1, 1000, 0, gen_random_uuid())$$);
SELECT t.expect_error('T262 a settlement line is never rewritten (append-only)', '42501',
  $$UPDATE pay.settlement_line SET outcome = 'Matched' WHERE line_no = 2 AND import_id = 'a0000000-0000-0000-0000-0000000000b2'$$);
SELECT t.expect_error('T263 a not-found line points at no payment', '23514',
  $$INSERT INTO pay.settlement_line (tenant_id, import_id, line_no, reference_no, transaction_date, amount, outcome, payment_id)
      VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-0000000000b2', 3, '444455556666', '2026-11-24', 10, 'NotFound', 'a0000000-0000-0000-0000-0000000000b1')$$);
SELECT t.expect_error('T209 cash records what was handed over and the change from it (SAL-006.1)', '23514',
  $$INSERT INTO pay.payment (tenant_id, id, method, status, amount, tendered, change_given, business_date, recorded_by)
    VALUES (core.current_tenant(), gen_random_uuid(), 'Cash', 'ConfirmedCash', 400, 1000, 500, '2026-11-24', gen_random_uuid())$$);
SELECT t.expect_error('T210 a card payment always carries its reference (PAY-002)', '23514',
  $$INSERT INTO pay.payment (tenant_id, id, method, status, amount, business_date, recorded_by)
    VALUES (core.current_tenant(), gen_random_uuid(), 'ExternalCardTerminal', 'ConfirmedManual', 400, '2026-11-24', gen_random_uuid())$$);
INSERT INTO sales.sale_tender (tenant_id, sale_id, seq, method, amount, payment_id)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000043', 1, 'ExternalCardTerminal', 600, gen_random_uuid());
SELECT t.expect_error('T211 tenders never add up to more than the total (RC027)', 'RC027',
  $$INSERT INTO sales.sale_tender (tenant_id, sale_id, seq, method, amount, payment_id)
    VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000043', 2, 'Cash', 401, gen_random_uuid())$$);
SELECT t.expect_error('T212 once paid into, the lines of a bill do not change (RC026)', 'RC026',
  $$UPDATE sales.sale_line SET qty = 2 WHERE sale_id = 'a0000000-0000-0000-0000-000000000043'$$);
UPDATE sales.sale_line SET cost_at_sale = 300000, snapshot = '{"name":"شیر"}' WHERE sale_id = 'a0000000-0000-0000-0000-000000000043';
SELECT t.ok('T214 Finalize may still write the snapshot and cost of a paid bill''s lines (SAL-005)',
  (SELECT cost_at_sale FROM sales.sale_line WHERE sale_id = 'a0000000-0000-0000-0000-000000000043') = 300000);
SELECT t.expect_error('T215 a tender is never deleted (no grant; the trigger refuses it too)', '42501',
  $$DELETE FROM sales.sale_tender WHERE sale_id = 'a0000000-0000-0000-0000-000000000043'$$);
SELECT t.expect_error('T216 a tender''s amount never changes (RC028)', 'RC028',
  $$UPDATE sales.sale_tender SET amount = 1 WHERE sale_id = 'a0000000-0000-0000-0000-000000000043'$$);
UPDATE sales.sale_tender SET voided_at = now(), voided_by = gen_random_uuid(), void_reason = 'اشتباه در مبلغ'
 WHERE sale_id = 'a0000000-0000-0000-0000-000000000043' AND seq = 1;
SELECT t.expect_error('T217 a voided tender stays voided (RC028)', 'RC028',
  $$UPDATE sales.sale_tender SET voided_at = NULL, voided_by = NULL, void_reason = NULL WHERE sale_id = 'a0000000-0000-0000-0000-000000000043'$$);
INSERT INTO sales.sale_tender (tenant_id, sale_id, seq, method, amount, payment_id)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000043', 2, 'Cash', 1000, gen_random_uuid());
SELECT t.ok('T218 a voided tender no longer counts toward the total (the whole 1,000 is paid again)', true);
SELECT t.expect_error('T213 payments are taken only on an open sale (RC027)', 'RC027',
  $$INSERT INTO sales.sale_tender (tenant_id, sale_id, seq, method, amount, payment_id)
    VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000042', 1, 'Cash', 1, gen_random_uuid())$$);

-- US-083 hold, resume, cancel (SAL-011, D02 §6.1)
INSERT INTO sales.sale (tenant_id, id, branch_id, register_id, shift_id, cashier_id, terminal_id, client_draft_id, pricing_mode)
  VALUES (core.current_tenant(), 'a0000000-0000-0000-0000-000000000044', 'a0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-000000000002',
          'a0000000-0000-0000-0000-000000000030', gen_random_uuid(), gen_random_uuid(), 'draft-0044', 'TaxExclusive');
UPDATE sales.sale SET status = 'Held', hold_note = 'خانم احمدی' WHERE id = 'a0000000-0000-0000-0000-000000000044';
SELECT t.expect_error('T226 a held cart is not finalized before it is resumed (RC031)', 'RC031',
  $$UPDATE sales.sale SET status = 'Finalized', invoice_no = 'X', finalized_at = now(), fiscal_year = 1405 WHERE id = 'a0000000-0000-0000-0000-000000000044'$$);
SELECT t.expect_error('T227 a cancelled sale says when and why', '23514',
  $$UPDATE sales.sale SET status = 'Cancelled' WHERE id = 'a0000000-0000-0000-0000-000000000044'$$);
SELECT t.expect_error('T228 a cart with payments in force is not cancelled (RC031)', 'RC031',
  $$UPDATE sales.sale SET status = 'Cancelled', cancelled_at = now(), cancel_reason = 'اشتباه' WHERE id = 'a0000000-0000-0000-0000-000000000043'$$);
UPDATE sales.sale SET status = 'Draft', last_activity_at = now() - interval '25 hours' WHERE id = 'a0000000-0000-0000-0000-000000000044';

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
SELECT t.expect_error('T237 a closed period is never reopened (D-45, AB-19)', 'RC033',
  $$UPDATE acc.fiscal_period SET status = 'Open', closed_at = NULL WHERE id = 'a0000000-0000-0000-0000-000000000202'$$);
SELECT t.expect_error('T238 a period''s span never changes', 'RC033',
  $$UPDATE acc.fiscal_period SET span = tstzrange('2026-10-01 00:00+03:30', '2026-11-22 00:00+03:30') WHERE id = 'a0000000-0000-0000-0000-000000000202'$$);
SELECT t.ok('T239 a period closed without a time is stamped now',
  (SELECT closed_at IS NOT NULL FROM acc.fiscal_period WHERE id = 'a0000000-0000-0000-0000-000000000202'));
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
INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES (:A, gen_random_uuid(), 'e0000000-0000-0000-0000-000000000001', 'TenantOwner', 'Tenant');
SELECT set_config('app.tenant_id', :B, false);
INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES (:B, 'e0000000-0000-0000-0000-000000000001'),
                                                         (:B, 'e0000000-0000-0000-0000-000000000002');
UPDATE iam.tenant_membership SET status = 'Suspended' WHERE user_id = 'e0000000-0000-0000-0000-000000000002';
SELECT set_config('app.tenant_id', '', false);
SELECT set_config('app.user_id', 'e0000000-0000-0000-0000-000000000001', false);
SELECT t.ok('T80 my_memberships returns the caller''s active memberships across tenants (A and B)',
  (SELECT count(*) FROM iam.my_memberships()) = 2);
SELECT t.ok('T85 my_memberships returns each membership''s role assignments with scope (US-022/025)',
  (SELECT assignments FROM iam.my_memberships() WHERE tenant_id = :A) = '[{"role":"TenantOwner","scopeType":"Tenant","scopeId":null}]'::jsonb
  AND (SELECT assignments FROM iam.my_memberships() WHERE tenant_id = :B) = '[]'::jsonb);
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
SELECT set_config('app.tenant_id', :A, false);
SELECT t.expect_error('T86 an unknown role is rejected', '23514',
  format($$INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES (%L, gen_random_uuid(), 'e0000000-0000-0000-0000-000000000001', 'SuperUser', 'Tenant')$$, :A));
SELECT t.expect_error('T97 tenant-wide roles cannot be narrowed to a branch', '23514',
  format($$INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES (%L, gen_random_uuid(), 'e0000000-0000-0000-0000-000000000001', 'Accountant', 'Branch', 'a0000000-0000-0000-0000-000000000001')$$, :A));
SELECT t.expect_error('T98 the last TenantOwner cannot be removed', 'RC010',
  $$DELETE FROM iam.role_assignment WHERE role = 'TenantOwner'$$);
SELECT t.expect_error('T99 role assignments are immutable (remove and re-add)', 'RC001',
  $$UPDATE iam.role_assignment SET scope_type = 'Tenant'$$);
-- US-024: suspending members never leaves a tenant without an active owner
SELECT t.expect_error('T165 the last active TenantOwner cannot be suspended (IAM-009)', 'RC010',
  $$UPDATE iam.tenant_membership SET status = 'Suspended' WHERE user_id = 'e0000000-0000-0000-0000-000000000001'$$);
INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES (:A, 'e0000000-0000-0000-0000-000000000002');
INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES (:A, 'e0000000-0000-0000-0000-0000000000a2', 'e0000000-0000-0000-0000-000000000002', 'TenantOwner', 'Tenant');
UPDATE iam.tenant_membership SET status = 'Suspended', version = version + 1 WHERE user_id = 'e0000000-0000-0000-0000-000000000002';
SELECT t.ok('T166 a second owner can be suspended while another owner stays active',
  (SELECT status FROM iam.tenant_membership WHERE user_id = 'e0000000-0000-0000-0000-000000000002') = 'Suspended');
SELECT t.expect_error('T167 a suspended owner does not count: the only active owner keeps the role', 'RC010',
  $$DELETE FROM iam.role_assignment WHERE role = 'TenantOwner' AND user_id = 'e0000000-0000-0000-0000-000000000001'$$);
DELETE FROM iam.role_assignment WHERE id = 'e0000000-0000-0000-0000-0000000000a2';
UPDATE iam.tenant_membership SET status = 'Removed' WHERE user_id = 'e0000000-0000-0000-0000-000000000002';
INSERT INTO iam.invitation (tenant_id, id, token_hash, display_name, assignments, invited_by, expires_at)
  VALUES (:A, gen_random_uuid(), sha256('secret-token'), 'صندوق‌دار', '[{"role":"Cashier","scopeType":"Branch","scopeId":"a0000000-0000-0000-0000-000000000001"}]', 'e0000000-0000-0000-0000-000000000001', now() + interval '7 days'),
         (:A, gen_random_uuid(), sha256('expired-token'), 'قدیمی', '[{"role":"Cashier","scopeType":"Tenant","scopeId":null}]', 'e0000000-0000-0000-0000-000000000001', now() - interval '1 minute');
SELECT set_config('app.tenant_id', '', false);
SELECT t.ok('T100 invitation_tenant finds a pending invitation only by its token hash, never an expired one',
  iam.invitation_tenant(sha256('secret-token')) = :A::uuid AND iam.invitation_tenant(sha256('expired-token')) IS NULL
  AND iam.invitation_tenant(sha256('guess')) IS NULL);
SELECT t.ok('T101 invitations are invisible without the tenant context (RLS)', (SELECT count(*) FROM iam.invitation) = 0);
-- US-006 idempotency registry (core)
SELECT set_config('app.tenant_id', :A, false);
INSERT INTO core.idempotency_record (tenant_id, key, operation, payload_hash, status)
  VALUES (:A, 'key-0000000000000001', 'test', decode(repeat('ab', 32), 'hex'), 'InProgress');
SELECT set_config('app.tenant_id', :B, false);
SELECT t.ok('T87 the same key is independent per tenant and invisible to other tenants',
  (SELECT count(*) FROM core.idempotency_record) = 0);
INSERT INTO core.idempotency_record (tenant_id, key, operation, payload_hash, status)
  VALUES (:B, 'key-0000000000000001', 'test', decode(repeat('cd', 32), 'hex'), 'InProgress');
SELECT t.expect_error('T88 a malformed key is rejected', '23514',
  format($$INSERT INTO core.idempotency_record (tenant_id, key, operation, payload_hash, status) VALUES (%L, 'short', 'test', decode(repeat('ab', 32), 'hex'), 'InProgress')$$, :B));
SELECT t.expect_error('T89 Completed needs a 2xx response status (only successes are stored, D-34)', '23514',
  $$UPDATE core.idempotency_record SET status = 'Completed', response_status = 409, completed_at = now()$$);
SELECT t.expect_error('T90 the application cannot delete idempotency records (retention, D-14; purge is the worker''s)', '42501',
  $$DELETE FROM core.idempotency_record$$);
-- US-005 outbox / inbox (core)
SELECT set_config('app.tenant_id', :A, false);
INSERT INTO core.outbox (tenant_id, id, producer, event_type, event_version, aggregate_id, payload)
  VALUES (:A, gen_random_uuid(), 'Sales', 'sales.sale-finalized', 1, gen_random_uuid(), '{}');
SELECT t.expect_error('T91 the application writes events but cannot read or change the outbox', '42501',
  $$SELECT count(*) FROM core.outbox$$);
SELECT t.expect_error('T92 a module can only emit events with its own prefix (sales.* from Sales)', '23514',
  format($$INSERT INTO core.outbox (tenant_id, id, producer, event_type, event_version, aggregate_id, payload)
           VALUES (%L, gen_random_uuid(), 'Sales', 'tax.invoice-requested', 1, gen_random_uuid(), '{}')$$, :A));
SELECT t.expect_error('T93 the application cannot write the inbox (consumers run in Workers)', '42501',
  format($$INSERT INTO core.inbox (tenant_id, consumer, event_id) VALUES (%L, 'x', gen_random_uuid())$$, :A));
RESET ROLE;
SET ROLE rc_worker;
SELECT set_config('app.tenant_id', '', false);
SELECT t.ok('T94 without a tenant context the worker sees no outbox rows (TM-042)', (SELECT count(*) FROM core.outbox) = 0);
SELECT t.ok('T95 outbox_due_tenants lists only tenant ids with due messages', ARRAY(SELECT core.outbox_due_tenants()) = ARRAY[:A::uuid]);
SELECT set_config('app.tenant_id', :A, false);
SELECT t.ok('T96 with the tenant context the worker sees that tenant''s message', (SELECT count(*) FROM core.outbox) = 1);
DELETE FROM core.outbox;
-- US-026 approvals
SET ROLE rc_app;
SELECT set_config('app.tenant_id', :A, false);
INSERT INTO iam.approval_request (tenant_id, id, action, resource_type, scope_type, condition, binding_hash, requested_by, expires_at)
  VALUES (:A, 'd0000000-0000-0000-0000-000000000001', 'pos.discount.apply', 'Sale', 'Tenant', 'amount-limit', sha256('b'), 'e0000000-0000-0000-0000-000000000002', now() + interval '10 minutes');
SELECT t.expect_error('T105 the requester can never approve their own request (SoD-01)', '23514',
  $$UPDATE iam.approval_request SET status = 'Approved', decided_by = requested_by, decided_at = now() WHERE id = 'd0000000-0000-0000-0000-000000000001'$$);
UPDATE iam.approval_request SET status = 'Rejected', decided_by = 'e0000000-0000-0000-0000-000000000001', decided_at = now() WHERE id = 'd0000000-0000-0000-0000-000000000001';
SELECT t.expect_error('T106 a decided approval cannot be re-decided', 'RC012',
  $$UPDATE iam.approval_request SET status = 'Approved' WHERE id = 'd0000000-0000-0000-0000-000000000001'$$);
INSERT INTO iam.approval_request (tenant_id, id, action, resource_type, scope_type, condition, binding_hash, requested_by, expires_at, status, decided_by, decided_at)
  VALUES (:A, 'd0000000-0000-0000-0000-000000000002', 'pos.discount.apply', 'Sale', 'Tenant', 'amount-limit', sha256('b'), 'e0000000-0000-0000-0000-000000000002', now() + interval '10 minutes',
          'Approved', 'e0000000-0000-0000-0000-000000000001', now());
SELECT t.expect_error('T107 what was approved cannot be changed afterwards (valid transition, changed binding)', 'RC012',
  $$UPDATE iam.approval_request SET status = 'Consumed', consumed_at = now(), binding_hash = sha256('other') WHERE id = 'd0000000-0000-0000-0000-000000000002'$$);
RESET ROLE;

-- US-023 PIN
SET ROLE rc_app;
SELECT set_config('app.tenant_id', :A, false);
SELECT t.expect_error('T108 a PIN is stored only as Argon2id with the AUTH-08 parameters', '23514',
  format($$INSERT INTO iam.user_pin (tenant_id, user_id, pin_hash) VALUES (%L, 'e0000000-0000-0000-0000-000000000001', '1234')$$, :A));
SELECT t.expect_error('T109 the fifth failure must lock the PIN (failed_attempts and locked_at move together)', '23514',
  format($$INSERT INTO iam.user_pin (tenant_id, user_id, pin_hash, failed_attempts) VALUES (%L, 'e0000000-0000-0000-0000-000000000001', '$argon2id$v=19$m=65536,t=3,p=1$c2FsdA$aGFzaA', 5)$$, :A));
RESET ROLE;

-- US-013 terminals
SET ROLE rc_app;
SELECT set_config('app.tenant_id', :A, false);
INSERT INTO platform.terminal (tenant_id, id, branch_id, device_name, secret_hash, paired_by)
  VALUES (:A, 'f0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-000000000001', 'صندوق ۱', sha256('dev-secret'), 'e0000000-0000-0000-0000-000000000001');
UPDATE platform.terminal SET status = 'Revoked', revoked_at = now() WHERE id = 'f0000000-0000-0000-0000-000000000001';
SELECT t.expect_error('T102 a revoked terminal cannot be re-activated', 'RC011',
  $$UPDATE platform.terminal SET status = 'Active', revoked_at = NULL WHERE id = 'f0000000-0000-0000-0000-000000000001'$$);
SELECT t.expect_error('T103 a terminal cannot be attached to a branch of another tenant (composite FK)', '23503',
  format($$INSERT INTO platform.terminal (tenant_id, id, branch_id, device_name, secret_hash, paired_by) VALUES (%L, gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'x', sha256('y'), gen_random_uuid())$$, :A));
SELECT set_config('app.tenant_id', :B, false);
SELECT t.ok('T104 terminals are invisible to other tenants (RLS)', (SELECT count(*) FROM platform.terminal) = 0);
RESET ROLE;
DELETE FROM core.idempotency_record;  -- test rows were left InProgress on purpose; oracle O-4 would flag them

-- US-014 plans and entitlements (TEN-003, AT-004)
\set C '''c1000000-0000-0000-0000-000000000001'''
SET ROLE rc_app;
SELECT set_config('app.tenant_id', :C, false);
INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES (:C, 'پلن', 'supermarket');
SELECT t.ok('T110 a new tenant starts on the trial plan with an end date from the plan',
  (SELECT plan_code = 'Trial' AND trial_ends_at BETWEEN now() + interval '13 days' AND now() + interval '15 days' FROM platform.subscription));
SELECT t.expect_error('T111 the application cannot change its own plan', '42501', $$UPDATE platform.subscription SET plan_code = 'Enterprise'$$);
SELECT t.expect_error('T111b the application cannot edit plan limits', '42501', $$UPDATE platform.plan SET max_branches = 99$$);
RESET ROLE;
UPDATE platform.subscription SET plan_code = 'Basic' WHERE tenant_id = :C;   -- Basic: 1 branch, 2 registers, 3 users
SET ROLE rc_app;
SELECT set_config('app.tenant_id', :C, false);
INSERT INTO platform.branch (tenant_id, id, code, name) VALUES (:C, 'c2000000-0000-0000-0000-000000000001', 'B1', 'یک');
SELECT t.expect_error('T112 a second active branch exceeds the Basic plan', 'RC030',
  format($$INSERT INTO platform.branch (tenant_id, id, code, name) VALUES (%L, gen_random_uuid(), 'B2', 'دو')$$, :C));
SELECT t.expect_error('T120 at the cap a duplicate code still reports the duplicate, not the plan', '23505',
  format($$INSERT INTO platform.branch (tenant_id, id, code, name) VALUES (%L, gen_random_uuid(), 'B1', 'تکراری')$$, :C));
INSERT INTO platform.branch (tenant_id, id, code, name, status) VALUES (:C, 'c2000000-0000-0000-0000-000000000002', 'B3', 'غیرفعال', 'Inactive');
SELECT t.ok('T113 an inactive branch takes no slot', (SELECT count(*) FROM platform.branch) = 2);
SELECT t.expect_error('T114 re-activating a branch at the cap is refused', 'RC030',
  $$UPDATE platform.branch SET status = 'Active' WHERE id = 'c2000000-0000-0000-0000-000000000002'$$);
INSERT INTO platform.register (tenant_id, id, branch_id, code) VALUES
  (:C, gen_random_uuid(), 'c2000000-0000-0000-0000-000000000001', 'R1'), (:C, gen_random_uuid(), 'c2000000-0000-0000-0000-000000000001', 'R2');
SELECT t.expect_error('T115 a third register exceeds the Basic plan', 'RC030',
  format($$INSERT INTO platform.register (tenant_id, id, branch_id, code) VALUES (%L, gen_random_uuid(), 'c2000000-0000-0000-0000-000000000001', 'R3')$$, :C));
RESET ROLE;
INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name)
  SELECT ('c3000000-0000-0000-0000-00000000000' || i)::uuid, 'https://t', 'plan-user-' || i, 'کاربر' FROM generate_series(1, 4) i;
SET ROLE rc_app;
SELECT set_config('app.tenant_id', :C, false);
INSERT INTO iam.tenant_membership (tenant_id, user_id)
  SELECT :C, ('c3000000-0000-0000-0000-00000000000' || i)::uuid FROM generate_series(1, 3) i;
SELECT t.expect_error('T116 a fourth active member exceeds the Basic plan', 'RC030',
  format($$INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES (%L, 'c3000000-0000-0000-0000-000000000004')$$, :C));
UPDATE iam.tenant_membership SET status = 'Suspended' WHERE user_id = 'c3000000-0000-0000-0000-000000000003';
INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES (:C, 'c3000000-0000-0000-0000-000000000004');
SELECT t.ok('T117 a suspended member frees the slot', (SELECT count(*) FROM iam.tenant_membership WHERE status = 'Active') = 3);
RESET ROLE;
UPDATE platform.subscription SET plan_code = 'Enterprise' WHERE tenant_id = :C;
SET ROLE rc_app;
SELECT set_config('app.tenant_id', :C, false);
INSERT INTO platform.branch (tenant_id, id, code, name) SELECT :C, gen_random_uuid(), 'E' || i, 'سازمانی' FROM generate_series(1, 20) i;
SELECT t.ok('T118 Enterprise has no caps', (SELECT count(*) FROM platform.branch WHERE status = 'Active') = 21);
SELECT set_config('app.tenant_id', :A, false);
SELECT t.ok('T119 a tenant sees only its own subscription (RLS)', (SELECT count(*) FROM platform.subscription WHERE tenant_id = :C) = 0);
RESET ROLE;
-- US-015 read-only (TEN-004)
SET ROLE rc_app;
SELECT set_config('app.tenant_id', :C, false);
SELECT t.ok('T122 an active subscription is not read-only', platform.read_only_reason() IS NULL);
RESET ROLE;
UPDATE platform.subscription SET plan_code = 'Trial', trial_ends_at = now() - interval '1 minute' WHERE tenant_id = :C;
SET ROLE rc_app;
SELECT set_config('app.tenant_id', :C, false);
SELECT t.ok('T123 a trial past its end is read-only', platform.read_only_reason() = 'trial-expired');
RESET ROLE;
UPDATE platform.tenant SET status = 'Suspended' WHERE id = :C;
SET ROLE rc_app;
SELECT set_config('app.tenant_id', :C, false);
SELECT t.ok('T124 suspension wins over the trial state', platform.read_only_reason() = 'suspended');
SELECT t.ok('T125 a read-only tenant still reads all of its data', (SELECT count(*) FROM platform.branch) = 22);   -- 21 active + 1 inactive
SELECT set_config('app.tenant_id', :A, false);
SELECT t.ok('T126 the reason is about the context tenant only', platform.read_only_reason() IS NULL);
SELECT set_config('app.tenant_id', :C, false);
SELECT t.expect_error('T127 a store cannot lift its own suspension', '42501', $$UPDATE platform.tenant SET status = 'Active'$$);
UPDATE platform.tenant SET legal_name = 'پلن ۲';
SELECT t.ok('T128 a store still edits its own settings', (SELECT legal_name FROM platform.tenant) = 'پلن ۲');
-- US-027 support JIT access (IAM-008, SoD-05)
SELECT set_config('app.tenant_id', :A, false);
SELECT t.expect_error('T133 support access is at most 4 hours', '23514',
  format($$INSERT INTO iam.support_access_request (tenant_id, id, requested_by, reason, duration_minutes) VALUES (%L, gen_random_uuid(), 'https://p|sup', 'بررسی خطای گزارش', 241)$$, :A));
INSERT INTO iam.support_access_request (tenant_id, id, requested_by, reason, duration_minutes)
  VALUES (:A, 'f5000000-0000-0000-0000-000000000001', 'https://p|sup', 'بررسی خطای گزارش فروش', 60);
SELECT t.expect_error('T134 a non-owner cannot approve (SoD-05)', 'RC015',
  $$UPDATE iam.support_access_request SET status = 'Approved', decided_by = 'e0000000-0000-0000-0000-000000000002', decided_at = now(), granted_until = now() + interval '60 minutes' WHERE id = 'f5000000-0000-0000-0000-000000000001'$$);
SELECT t.expect_error('T135 the window is exactly the requested length', '23514',
  $$UPDATE iam.support_access_request SET status = 'Approved', decided_by = 'e0000000-0000-0000-0000-000000000001', decided_at = now(), granted_until = now() + interval '8 hours' WHERE id = 'f5000000-0000-0000-0000-000000000001'$$);
UPDATE iam.support_access_request SET status = 'Approved', decided_by = 'e0000000-0000-0000-0000-000000000001', decided_at = now(), granted_until = now() + interval '60 minutes'
  WHERE id = 'f5000000-0000-0000-0000-000000000001';
SELECT t.expect_error('T136 the reason and length are immutable after the request', 'RC015',
  $$UPDATE iam.support_access_request SET duration_minutes = 240, reason = 'دلیل دیگری برای دسترسی' WHERE id = 'f5000000-0000-0000-0000-000000000001'$$);
SELECT t.expect_error('T137 a decided request cannot be re-decided', 'RC015',
  $$UPDATE iam.support_access_request SET status = 'Rejected' WHERE id = 'f5000000-0000-0000-0000-000000000001'$$);
UPDATE iam.support_access_request SET status = 'Revoked', revoked_by = 'e0000000-0000-0000-0000-000000000001', revoked_at = now()
  WHERE id = 'f5000000-0000-0000-0000-000000000001';
SELECT t.expect_error('T138 ended access cannot be reopened', 'RC015',
  $$UPDATE iam.support_access_request SET status = 'Approved', revoked_by = NULL, revoked_at = NULL WHERE id = 'f5000000-0000-0000-0000-000000000001'$$);
SELECT t.expect_error('T139 the history cannot be deleted', '42501',
  $$DELETE FROM iam.support_access_request WHERE id = 'f5000000-0000-0000-0000-000000000001'$$);
-- US-111 accounts (ACC-001, AT-100)
SELECT set_config('app.tenant_id', :A, false);
UPDATE acc.account SET name = 'صندوق مرکزی' WHERE code = '1101';
SELECT t.ok('T154 a system account can be renamed', (SELECT name FROM acc.account WHERE code = '1101') = 'صندوق مرکزی');
SELECT t.expect_error('T155 a system account keeps its code, type and flags', 'RC018',
  $$UPDATE acc.account SET account_type = 'Expense' WHERE code = '1101'$$);
SELECT t.expect_error('T156 accounts are never deleted', '42501',
  $$DELETE FROM acc.account WHERE code = '1101'$$);
-- US-151 notifications (NOT-005/006/007, DI-130/131)
SELECT set_config('app.tenant_id', :A, false);
SELECT t.expect_error('T157 no code path can queue a marketing message (NOT-007)', '23514',
  format($$INSERT INTO notif.notification (tenant_id, id, purpose, channel, recipient_ref, template_code, template_version) VALUES (%L, gen_random_uuid(), 'Marketing', 'Sms', 'cust:1', 'promo', 1)$$, :A));
INSERT INTO notif.notification (tenant_id, id, purpose, channel, recipient_ref, template_code, template_version, source_type, source_id)
  VALUES (:A, 'f6000000-0000-0000-0000-000000000001', 'Receipt', 'Sms', 'cust:1', 'receipt', 1, 'Sale', 'a0000000-0000-0000-0000-000000000040');
SELECT t.expect_error('T158 one notification per source, purpose and channel', '23505',
  format($$INSERT INTO notif.notification (tenant_id, id, purpose, channel, recipient_ref, template_code, template_version, source_type, source_id) VALUES (%L, gen_random_uuid(), 'Receipt', 'Sms', 'cust:1', 'receipt', 1, 'Sale', 'a0000000-0000-0000-0000-000000000040')$$, :A));
SELECT t.expect_error('T159 only the Workers send: the application cannot change a notification', '42501',
  $$UPDATE notif.notification SET status = 'Sending'$$);
RESET ROLE;
SET ROLE rc_worker;
SELECT set_config('app.tenant_id', :A, false);
SELECT t.expect_error('T160 a queued notification cannot jump to Delivered (NOT-006)', 'RC019',
  $$UPDATE notif.notification SET status = 'Delivered', delivered_at = now(), provider_message_id = 'p1' WHERE id = 'f6000000-0000-0000-0000-000000000001'$$);
UPDATE notif.notification SET status = 'Sending', attempts = 1 WHERE id = 'f6000000-0000-0000-0000-000000000001';
UPDATE notif.notification SET status = 'Sent', provider_message_id = 'p1' WHERE id = 'f6000000-0000-0000-0000-000000000001';
SELECT t.expect_error('T161 Delivered only with a provider receipt time (DI-130)', '23514',
  $$UPDATE notif.notification SET status = 'Delivered' WHERE id = 'f6000000-0000-0000-0000-000000000001'$$);
UPDATE notif.notification SET attempts = attempts + 1 WHERE id = 'f6000000-0000-0000-0000-000000000001';   -- 2
UPDATE notif.notification SET attempts = attempts + 1 WHERE id = 'f6000000-0000-0000-0000-000000000001';   -- 3
UPDATE notif.notification SET attempts = attempts + 1 WHERE id = 'f6000000-0000-0000-0000-000000000001';   -- 4
UPDATE notif.notification SET attempts = attempts + 1 WHERE id = 'f6000000-0000-0000-0000-000000000001';   -- 5
SELECT t.expect_error('T162 never more than five attempts (DI-131)', '23514',
  $$UPDATE notif.notification SET attempts = attempts + 1 WHERE id = 'f6000000-0000-0000-0000-000000000001'$$);
SELECT t.expect_error('T163 attempts never jump or go back', 'RC019',
  $$UPDATE notif.notification SET attempts = 1 WHERE id = 'f6000000-0000-0000-0000-000000000001'$$);
RESET ROLE;
SET ROLE rc_app;
-- US-016 export jobs
SELECT set_config('app.tenant_id', :A, false);
SELECT t.expect_error('T129 an export cannot point at another tenant''s storage', '23514',
  format($$INSERT INTO platform.export_job (tenant_id, id, requested_by, categories, status, object_key, completed_at, expires_at) VALUES (%L, gen_random_uuid(), gen_random_uuid(), '{Products}', 'Completed', 'tenants/' || %L || '/exports/abcdefghijklmnopqrstuvwxyz012345.zip', now(), now())$$, :A, :C));
INSERT INTO platform.export_job (tenant_id, id, requested_by, categories) VALUES (:A, 'e1000000-0000-0000-0000-000000000001', gen_random_uuid(), '{Products,Sales}');
SELECT t.expect_error('T130 the API cannot complete or alter an export', '42501',
  $$UPDATE platform.export_job SET status = 'Failed' WHERE id = 'e1000000-0000-0000-0000-000000000001'$$);
SELECT t.expect_error('T131 only known categories can be exported', '23514',
  format($$INSERT INTO platform.export_job (tenant_id, id, requested_by, categories) VALUES (%L, gen_random_uuid(), gen_random_uuid(), '{Passwords}')$$, :A));
RESET ROLE;
SET ROLE rc_worker;
SELECT set_config('app.tenant_id', :C, false);
SELECT t.ok('T132 the worker sees no other tenant''s export jobs (RLS)', (SELECT count(*) FROM platform.export_job) = 0);
SELECT inv.ensure_movement_partitions(3);
SELECT t.ok('T182 the worker keeps three months of movement partitions ready, idempotently, with no grants',
  inv.ensure_movement_partitions(3) = 0
  AND to_regclass(format('inv.stock_movement_%s', to_char(statement_timestamp() AT TIME ZONE 'UTC' + interval '3 months', 'YYYY_MM'))) IS NOT NULL
  AND NOT has_table_privilege('rc_app', format('inv.stock_movement_%s', to_char(statement_timestamp() AT TIME ZONE 'UTC' + interval '3 months', 'YYYY_MM')), 'SELECT'));
RESET ROLE;
SET ROLE rc_app;
RESET ROLE;
SELECT set_config('app.tenant_id', '', false);
INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ('c1000000-0000-0000-0000-000000000002', 'عملیاتی', 'general');
SELECT t.ok('T121 a tenant created by an operator without a context also starts its trial, and the context is restored',
  (SELECT count(*) FROM platform.subscription WHERE tenant_id = 'c1000000-0000-0000-0000-000000000002') = 1
  AND coalesce(current_setting('app.tenant_id', true), '') = '');

-- US-173 retention (BR-087, TEN-005.1)
SET ROLE rc_app;
SELECT set_config('app.tenant_id', :A, false);
SELECT t.ok('T264 the platform floors are seeded and a future version changes nothing yet',
  platform.retention_floor('FinancialDocuments') = 3650 AND platform.retention_floor('Notifications') = 90);
INSERT INTO platform.retention_policy (category, version, minimum_days, default_days, valid_from, reason, created_by)
  VALUES ('TechnicalLogs', 2, 60, 90, now() + interval '30 days', 'test', 'op');
SELECT t.ok('T265 a policy valid from the future is not in force', platform.retention_floor('TechnicalLogs') = 30);
SELECT t.expect_error('T266 a policy version never changes', '42501',
  $$UPDATE platform.retention_policy SET minimum_days = 1 WHERE category = 'Audit'$$);
SELECT t.expect_error('T267 a default below the floor is refused', '23514',
  $$INSERT INTO platform.retention_policy (category, version, minimum_days, default_days, valid_from, reason, created_by) VALUES ('Audit', 2, 3650, 100, now(), 'test', 'op')$$);
SELECT t.expect_error('T268 a store cannot keep data for less than the floor', 'RC036',
  format($$INSERT INTO platform.tenant_retention (tenant_id, category, days, set_by) VALUES (%L, 'Notifications', 60, gen_random_uuid())$$, :A));
INSERT INTO platform.tenant_retention (tenant_id, category, days, set_by) VALUES (:A, 'Notifications', 365, gen_random_uuid());
SELECT t.ok('T269 a store may keep data longer than the default', platform.effective_retention_days('Notifications') = 365);
SELECT t.expect_error('T270 a store cannot lift its own legal hold or set one', '42501',
  format($$INSERT INTO platform.legal_hold (tenant_id, category, reason, created_by) VALUES (%L, 'Notifications', 'x y z', 'me')$$, :A));
DELETE FROM platform.tenant_retention WHERE category = 'Notifications';
SELECT t.ok('T271 clearing the choice returns to the default', platform.effective_retention_days('Notifications') = 180);
INSERT INTO notif.notification (tenant_id, id, purpose, channel, recipient_ref, template_code, template_version, status, created_at)
  VALUES (:A, 'a0000000-0000-0000-0000-0000000000c1', 'Internal', 'Sms', 'u1', 'x-code', 1, 'Failed', now() - interval '400 days'),
         (:A, 'a0000000-0000-0000-0000-0000000000c2', 'Internal', 'Sms', 'u1', 'x-code', 1, 'Failed', now() - interval '10 days');
SELECT platform.set_legal_hold(:A, 'Notifications', 'قرار قضایی', NULL, 'op|1');
SELECT t.ok('T272 the store sees its hold', (SELECT count(*) FROM platform.legal_hold) = 1);
RESET ROLE;
SET ROLE rc_worker;
SELECT set_config('app.tenant_id', :A, false);
SELECT t.expect_error('T273 no deletion under a legal hold', 'RC036',
  $$DELETE FROM notif.notification WHERE id = 'a0000000-0000-0000-0000-0000000000c1'$$);
RESET ROLE;
SET ROLE rc_app;
SELECT t.ok('T274 an operator lifts the hold', platform.release_legal_hold(:A, 'Notifications') AND NOT platform.release_legal_hold(:A, 'Notifications'));
RESET ROLE;
SET ROLE rc_worker;
SELECT set_config('app.tenant_id', :A, false);
SELECT t.expect_error('T275 nothing is deleted inside its retention', 'RC036',
  $$DELETE FROM notif.notification WHERE id = 'a0000000-0000-0000-0000-0000000000c2'$$);
DELETE FROM notif.notification WHERE id = 'a0000000-0000-0000-0000-0000000000c1';
SELECT t.ok('T276 past its retention and with no hold, a notification is deleted',
  (SELECT count(*) FROM notif.notification WHERE id = 'a0000000-0000-0000-0000-0000000000c1') = 0);
INSERT INTO platform.retention_run (tenant_id, id, category, outcome, cutoff, deleted_rows) VALUES (:A, gen_random_uuid(), 'Notifications', 'Purged', now(), 1);
SELECT t.expect_error('T277 a run record is never rewritten', '42501', $$UPDATE platform.retention_run SET deleted_rows = 0$$);
RESET ROLE;
SELECT set_config('app.tenant_id', '', false);

-- US-152 SMS template overrides (NOT-009)
SET ROLE rc_app;
SELECT set_config('app.tenant_id', :A, false);
INSERT INTO notif.template_override (tenant_id, template_code, version, store_name, signature, created_by)
  VALUES (:A, 'receipt', 1, 'فروشگاه الف', 'با تشکر', gen_random_uuid());
SELECT t.expect_error('T278 an override is a new version, never an edit', '42501',
  $$UPDATE notif.template_override SET store_name = 'x' WHERE version = 1$$);
SELECT t.expect_error('T279 only the receipt and reminder texts can be personalised', '23514',
  format($$INSERT INTO notif.template_override (tenant_id, template_code, version, store_name, created_by) VALUES (%L, 'internal', 1, 'x', gen_random_uuid())$$, :A));
SELECT t.expect_error('T280 the signed name is short and a single line', '23514',
  format($$INSERT INTO notif.template_override (tenant_id, template_code, version, store_name, created_by) VALUES (%L, 'receipt', 2, %L, gen_random_uuid())$$, :A, 'ا' || chr(10) || 'ب'));
SELECT t.expect_error('T281 a version number is used once', '23505',
  format($$INSERT INTO notif.template_override (tenant_id, template_code, version, store_name, created_by) VALUES (%L, 'receipt', 1, 'دیگر', gen_random_uuid())$$, :A));
SELECT set_config('app.tenant_id', :B, false);
SELECT t.ok('T282 another store sees none of it (RLS)', (SELECT count(*) FROM notif.template_override) = 0);
RESET ROLE;
SELECT set_config('app.tenant_id', '', false);

-- US-171 operator console: list stores, suspend and reactivate (TEN-004)
SET ROLE rc_app;
SELECT set_config('app.tenant_id', '', false);
SELECT t.ok('T283 the operator lists stores across tenants with plan and branch count, in id order, paged',
  (SELECT count(*) FROM platform.list_tenants(NULL, 100) WHERE id IN (:A, :B)) = 2
  AND (SELECT count(*) FROM platform.list_tenants(NULL, 1)) = 1
  AND (SELECT id FROM platform.list_tenants(:A, 1) ) > :A::uuid
  AND (SELECT count(*) FROM platform.list_tenants(NULL, 5, :A)) = 1);
SELECT t.ok('T284 an operator suspends a store', platform.set_tenant_status(:A, 'Suspended', 'عدم پرداخت', 'op|1') = 'ok');
SELECT t.ok('T284b the store reads as suspended and the operator context is restored',
  (SELECT status FROM platform.list_tenants(NULL, 5, :A)) = 'Suspended' AND coalesce(current_setting('app.tenant_id', true), '') = '');
SELECT t.ok('T285 the same status again, an unknown status or an unknown store is refused',
  platform.set_tenant_status(:A, 'Suspended', 'دوباره', 'op|1') = 'invalid-transition'
  AND platform.set_tenant_status(:A, 'Trial', 'نامعتبر', 'op|1') = 'invalid-transition'
  AND platform.set_tenant_status(gen_random_uuid(), 'Active', 'هیچ', 'op|1') = 'not-found');
SELECT t.ok('T286 reactivation works and Closed is final',
  platform.set_tenant_status(:A, 'Active', 'پرداخت شد', 'op|1') = 'ok'
  AND platform.set_tenant_status(:A, 'Closed', 'درخواست مشتری', 'op|1') = 'ok'
  AND platform.set_tenant_status(:A, 'Active', 'بازگشایی', 'op|1') = 'invalid-transition');
SELECT set_config('app.tenant_id', :A, false);
SELECT t.ok('T287 the store sees its own status history, three changes', (SELECT count(*) FROM platform.tenant_status_change) = 3);
SELECT t.expect_error('T288 a store cannot change its own status or history', '42501',
  $$UPDATE platform.tenant SET status = 'Active' WHERE true$$);
SELECT t.expect_error('T289 the history is never rewritten', '42501', $$DELETE FROM platform.tenant_status_change$$);
RESET ROLE;
-- put the test store back as it was for the checks that follow
UPDATE platform.tenant SET status = 'Trial' WHERE id = :A;
SELECT set_config('app.tenant_id', '', false);

-- US-141 customer OTP (CUS-001)
SET ROLE rc_app;
INSERT INTO cid.otp_challenge (id, phone_hash, ip_hash, code_hash, phone_enc, expires_at)
  VALUES ('c1000000-0000-0000-0000-0000000000a1', sha256('p'), sha256('i'), sha256('c'), '\x01', now() + interval '2 minutes');
SELECT t.expect_error('T290 a challenge lives at most ten minutes', '23514',
  $$INSERT INTO cid.otp_challenge (id, phone_hash, ip_hash, code_hash, phone_enc, expires_at) VALUES (gen_random_uuid(), sha256('p'), sha256('i'), sha256('c'), '\x01', now() + interval '1 hour')$$);
SELECT t.expect_error('T291 at most five attempts', '23514', $$UPDATE cid.otp_challenge SET attempts = 6 WHERE id = 'c1000000-0000-0000-0000-0000000000a1'$$);
SELECT t.expect_error('T292 the code a challenge holds never changes', 'RC001', $$UPDATE cid.otp_challenge SET code_hash = sha256('other') WHERE id = 'c1000000-0000-0000-0000-0000000000a1'$$);
UPDATE cid.otp_challenge SET attempts = 1 WHERE id = 'c1000000-0000-0000-0000-0000000000a1';
UPDATE cid.otp_challenge SET status = 'Verified' WHERE id = 'c1000000-0000-0000-0000-0000000000a1';
SELECT t.expect_error('T293 a verified challenge is final (single use)', 'RC001', $$UPDATE cid.otp_challenge SET status = 'Failed' WHERE id = 'c1000000-0000-0000-0000-0000000000a1'$$);
SELECT t.expect_error('T294 the application cannot delete challenges, only the Workers purge', '42501', $$DELETE FROM cid.otp_challenge$$);
RESET ROLE;

-- US-142 store invites and the customer's own stores (CUS-005, CUS-003)
SET ROLE rc_app;
SELECT set_config('app.tenant_id', :A, false);
INSERT INTO cid.store_invite (tenant_id, id, branch_id, created_by, expires_at)
  VALUES (:A, 'c2000000-0000-0000-0000-0000000000a1', 'a0000000-0000-0000-0000-000000000001', gen_random_uuid(), now() + interval '90 days');
SELECT t.expect_error('T295 an invite lasts at most a year', '23514',
  format($$INSERT INTO cid.store_invite (tenant_id, id, branch_id, created_by, expires_at) VALUES (%L, gen_random_uuid(), 'a0000000-0000-0000-0000-000000000001', gen_random_uuid(), now() + interval '3 years')$$, :A));
SELECT t.expect_error('T296 an invite belongs to a branch of its own store', '23503',
  format($$INSERT INTO cid.store_invite (tenant_id, id, branch_id, created_by, expires_at) VALUES (%L, gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), now() + interval '1 day')$$, :A));
UPDATE cid.store_invite SET revoked_at = now() WHERE id = 'c2000000-0000-0000-0000-0000000000a1';
SELECT t.expect_error('T297 only the revocation of an invite can change', '42501', $$UPDATE cid.store_invite SET expires_at = now() + interval '200 days'$$);
SELECT set_config('app.tenant_id', :B, false);
SELECT t.ok('T298 another store sees none of the invites (RLS)', (SELECT count(*) FROM cid.store_invite) = 0);
SELECT set_config('app.tenant_id', '', false);
INSERT INTO cid.global_customer (id, public_code) VALUES ('c2000000-0000-0000-0000-0000000000c1', 'CU-23456789');
SELECT set_config('app.tenant_id', :A, false);
INSERT INTO cid.tenant_customer_membership (tenant_id, id, global_customer_id, join_source) VALUES (:A, 'c2000000-0000-0000-0000-0000000000d1', 'c2000000-0000-0000-0000-0000000000c1', 'StoreQr');
SELECT set_config('app.tenant_id', '', false);
SELECT t.ok('T299 the customer lists their own stores across tenants without a tenant context',
  (SELECT count(*) FROM cid.my_memberships('c2000000-0000-0000-0000-0000000000c1')) = 1
  AND (SELECT store_name FROM cid.my_memberships('c2000000-0000-0000-0000-0000000000c1')) = 'فروشگاه الف');
RESET ROLE;

\echo '== ALL CHECKS PASSED'
