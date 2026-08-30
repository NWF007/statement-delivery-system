-- =============================================================================================
-- V014  The cohort index on customer_key. NON-TRANSACTIONAL - see the .notx.sql suffix.
--
-- WHY THIS IS A SCRIPT OF ITS OWN rather than three more lines at the end of V013.
--
-- V013 adds cohort_id to customer_key, a table that already exists and may already hold rows. Rule
-- 1 of Scripts/README.md: an index built on a populated table without CONCURRENTLY holds a lock
-- that blocks every write to that table for the whole build. On 26 million rows that is minutes of
-- outage, delivered by a migration that looked like a one-line addition.
--
-- CREATE INDEX CONCURRENTLY cannot run inside a transaction, and the migrator wraps every ordinary
-- script in one. The `.notx.sql` suffix routes this script to the second pass, which runs without a
-- transaction and with statement_timeout disabled. See Program.cs.
--
-- ⚠ IF THIS SCRIPT FAILS it leaves an INVALID index behind. Nothing drops it automatically, and a
-- re-run will not replace it. Check and clean up before retrying:
--
--     SELECT indexrelid::regclass FROM pg_index WHERE NOT indisvalid;
--     DROP INDEX CONCURRENTLY idx_customer_key_cohort;
--
-- WHAT THE INDEX IS FOR: rotating a cohort KEK means re-wrapping every CEK in that cohort, which
-- is a query for one cohort_id out of 1,024. Without the index that is a sequential scan of the
-- whole table, once per cohort - 1,024 full scans to rotate the estate.
-- =============================================================================================

CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_customer_key_cohort
    ON customer_key (cohort_id);
