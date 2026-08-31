-- =============================================================================================
-- V019  The widened customer-list index on statement - THE PARTITIONED-PARENT PROCEDURE.
--
-- Prompt 6 (E4) widens the customer list: ARCHIVED statements must be listable or the restore
-- flow is unreachable, and PURGED ones must appear - status shown, no download offered. V006's
-- idx_statement_customer_period was partial on AVAILABLE only and the list was its sole
-- consumer, so it is replaced.
--
-- ⚠ THE FIRST VERSION OF THIS SCRIPT USED CREATE INDEX CONCURRENTLY ON THE PARENT and failed
-- with SQLSTATE 0A000 on its very first real execution (CI): PostgreSQL cannot build a
-- partitioned parent's index concurrently, full stop. The sanctioned pattern is what this
-- script now does:
--
--   1. CREATE INDEX ... ON ONLY <parent>   - metadata only, no scan, instant at any size.
--      The parent index is INVALID until every child has an attached matching index.
--   2. Build each CHILD's index, then ATTACH it. Locally and in CI each child is one month of
--      test data, so a plain in-transaction build is instant. ⚠ ON A POPULATED PRODUCTION
--      ESTATE do NOT run step 2 as-is: build each child CONCURRENTLY, operationally, outside
--      any transaction, then ATTACH - after which this script's IF NOT EXISTS loop is a no-op.
--   3. New partitions created later (ensure_range_partitions / partition maintenance) clone
--      every parent index automatically, so the widened index propagates without further work.
-- =============================================================================================

CREATE INDEX IF NOT EXISTS idx_statement_customer_period_visible
    ON ONLY statement (customer_id, period_start DESC, id DESC)
    WHERE status IN ('AVAILABLE', 'ARCHIVED', 'PURGED');

-- Step 2: per-child build + attach. Dynamic because partition names derive from now() at
-- migration time. Safety contract: each child is one MONTH of statements; the production
-- procedure for large children is in the header. Idempotent via IF NOT EXISTS + the attach
-- no-oping when already attached.
DO $$
DECLARE
    child      regclass;
    child_name text;
    idx_name   text;
BEGIN
    FOR child IN
        SELECT inhrelid::regclass FROM pg_inherits WHERE inhparent = 'statement'::regclass
    LOOP
        child_name := (SELECT relname FROM pg_class WHERE oid = child);
        idx_name   := child_name || '_cust_period_visible_idx';

        EXECUTE format(
            'CREATE INDEX IF NOT EXISTS %I ON %I (customer_id, period_start DESC, id DESC)
              WHERE status IN (''AVAILABLE'', ''ARCHIVED'', ''PURGED'')',
            idx_name, child_name);

        -- Attach unless already attached (a re-run, or a child created after the parent index
        -- existed and therefore auto-attached).
        IF NOT EXISTS (
            SELECT 1 FROM pg_inherits
             WHERE inhparent = 'idx_statement_customer_period_visible'::regclass
               AND inhrelid  = idx_name::regclass)
        THEN
            EXECUTE format(
                'ALTER INDEX idx_statement_customer_period_visible ATTACH PARTITION %I',
                idx_name);
        END IF;
    END LOOP;
END $$;

-- Only after the replacement exists everywhere: the old AVAILABLE-only index goes, parent and
-- children together.
DROP INDEX IF EXISTS idx_statement_customer_period;
