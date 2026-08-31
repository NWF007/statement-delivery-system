-- =============================================================================================
-- V019  The widened customer-list index on statement. NON-TRANSACTIONAL - see the .notx suffix.
--
-- Prompt 6 (E4) widens the customer list: ARCHIVED statements must be listable or the restore
-- flow is unreachable, and PURGED ones must appear - status shown, no download offered - because
-- "this existed and was destroyed" is a fact the customer is entitled to see. PENDING and FAILED
-- stay invisible: unfinished work is not a customer fact.
--
-- V006's idx_statement_customer_period was partial on AVAILABLE only and the list was its sole
-- consumer, so it is replaced. Rule 1 of Scripts/README.md: statement is populated (up to 2.5
-- billion rows), so both the build and the drop run CONCURRENTLY, outside a transaction.
--
-- ⚠ IF THE BUILD FAILS it leaves an INVALID index behind; check and clean up before retrying:
--
--     SELECT indexrelid::regclass FROM pg_index WHERE NOT indisvalid;
--     DROP INDEX CONCURRENTLY idx_statement_customer_period_visible;
-- =============================================================================================

CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_statement_customer_period_visible
    ON statement (customer_id, period_start DESC, id DESC)
    WHERE status IN ('AVAILABLE', 'ARCHIVED', 'PURGED');

-- Only after the replacement exists: the list must never be without an index on a partitioned
-- table this size.
DROP INDEX CONCURRENTLY IF EXISTS idx_statement_customer_period;
