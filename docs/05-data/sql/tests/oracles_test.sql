-- Oracles must detect the anomalies tests.sql deliberately leaves behind, and nothing else:
--   O-2  tenant A: balance seeded to 1 without a movement, then a -1 SaleIssue movement without updating the balance
--   O-13 tenant A: a 2,000,000 Charge inserted without updating credit_account.outstanding
--   O-14 tenant A: audit record seq 2 tampered by a superuser (T74)
\set ON_ERROR_STOP on
\o /dev/null
SET ROLE rc_worker;
CREATE TEMP TABLE r AS SELECT * FROM core.run_oracles();
SELECT t.ok('OR1 O-2 detects stock projection drift (exactly 1)', (SELECT count(*) FROM r WHERE oracle = 'O-2') = 1);
SELECT t.ok('OR2 O-13 detects receivable drift (exactly 1)', (SELECT count(*) FROM r WHERE oracle = 'O-13') = 1);
SELECT t.ok('OR3 O-14 detects tampered audit chain at seq 2',
            (SELECT detail FROM r WHERE oracle = 'O-14') = 'chain broken at seq 2');
SELECT t.ok('OR4 no false positives: O-3, O-4, O-5, O-12 empty; tenant B clean',
            (SELECT count(*) FROM r WHERE oracle IN ('O-3','O-4','O-5','O-12')) = 0
            AND (SELECT count(*) FROM r WHERE tenant_id = '00000000-0000-0000-0000-00000000000b') = 0);
SELECT t.ok('OR5 worker role still cannot list tenants directly (RLS)',
            (SELECT count(*) FROM platform.tenant) = 0);
RESET ROLE;
\echo '== ORACLE CHECKS PASSED'
