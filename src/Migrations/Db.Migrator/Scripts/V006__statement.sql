-- =============================================================================================
-- V006  Statement. The table this whole system exists to serve.
--
-- 30 million rows a month, 360 million a year, 2.52 BILLION at seven-year retention - roughly
-- 800 GB of rows plus 600 GB of indexes. Partitioned by RANGE on period_start, monthly, because
-- retrofitting partitions onto a table that size is a multi-day migration and this is the last
-- moment it is free. See docs/adr/0005-partition-not-shard.md and 0007-partitioning-strategy.md.
--
-- The storage and crypto columns are created NULLABLE and stay NULL until the encryption work
-- lands. They are here now because adding ten columns to a 2.5-billion-row table later means a
-- rewrite; adding them to an empty one is instant.
-- =============================================================================================

CREATE TABLE IF NOT EXISTS statement
(
    -- Application-supplied UUIDv7. No DEFAULT gen_random_uuid(), deliberately - see V005.
    id             UUID        NOT NULL,
    account_id     UUID        NOT NULL,

    -- DENORMALISED, AND THIS IS THE MOST IMPORTANT LINE IN THE FILE.
    --
    -- The owner is reachable by joining statement -> account. This column duplicates it anyway,
    -- because authorisation runs on EVERY request and that join would be against a 2.5-billion-row
    -- table on the hot path of a SECURITY decision. With the owner here, "does this person own
    -- this?" is a single-column predicate that goes straight into the WHERE clause - and a
    -- predicate cannot be forgotten, whereas a post-load ownership check is one missing `if` away
    -- from a data breach.
    --
    -- The cost is real and accepted: moving an account between customers requires backfilling
    -- every statement row for that account. Rare and batchable; authorisation is neither.
    -- See docs/adr/0009-denormalised-customer-id-on-statement.md.
    customer_id    UUID        NOT NULL,

    -- The RANGE partition key.
    period_start   DATE        NOT NULL,
    period_end     DATE        NOT NULL,

    -- Increments on regeneration. A corrected statement is a NEW ROW, never an update in place, so
    -- that what the customer was originally shown remains provable when they dispute it.
    version        INT         NOT NULL DEFAULT 1,

    status         TEXT        NOT NULL DEFAULT 'PENDING',

    -- ---- storage and crypto: nullable, populated later -------------------------------------
    -- Every object is reached by a key COMPUTED FROM THIS ROW, never by listing a bucket. At 2.5
    -- billion objects a listing is not slow, it is unusable.
    storage_key    TEXT,
    storage_tier   TEXT        NOT NULL DEFAULT 'STANDARD',
    size_bytes     BIGINT,
    content_sha256 BYTEA,

    -- Envelope encryption: the data key is stored WRAPPED by a per-customer key encryption key.
    -- Destroying the KEK crypto-erases every statement for that customer without touching object
    -- storage - which is what makes erasure feasible at this scale.
    wrapped_dek    BYTEA,
    dek_algorithm  TEXT,
    kek_id         TEXT,
    iv             BYTEA,
    auth_tag       BYTEA,
    -- ----------------------------------------------------------------------------------------

    -- Derived from period_end, NOT from generated_at: a statement regenerated years late must not
    -- thereby earn extra retention.
    retain_until   DATE        NOT NULL,

    generated_at   TIMESTAMPTZ,

    -- The row outlives the bytes. "This existed and was destroyed on this date" is the answer an
    -- auditor needs; deleting the row makes it indistinguishable from one that never existed.
    purged_at      TIMESTAMPTZ,

    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- A partitioned table's primary key MUST include the partition key. The consequence worth
    -- knowing: this enforces uniqueness of (id, period_start), not of id alone across partitions.
    -- Acceptable for a 128-bit UUIDv7; it would not be for a sequence-derived key.
    CONSTRAINT pk_statement PRIMARY KEY (id, period_start),

    -- One statement per account per period per version. This is what makes regeneration safe:
    -- version 2 coexists with version 1 rather than overwriting it.
    CONSTRAINT uq_statement_account_period_version UNIQUE (account_id, period_start, version),

    CONSTRAINT ck_statement_period CHECK (period_end >= period_start),
    CONSTRAINT ck_statement_period_start_is_month_start CHECK (EXTRACT(DAY FROM period_start) = 1),
    CONSTRAINT ck_statement_version CHECK (version >= 1),
    CONSTRAINT ck_statement_status
        CHECK (status IN ('PENDING', 'AVAILABLE', 'ARCHIVED', 'PURGED', 'FAILED')),
    CONSTRAINT ck_statement_storage_tier
        CHECK (storage_tier IN ('STANDARD', 'INFREQUENT', 'GLACIER')),

    -- An AVAILABLE statement with no storage key is a row claiming bytes exist somewhere it cannot
    -- name. Enforced here rather than in application code because the generation fleet is 400
    -- replicas and only one of them needs to have the bug.
    CONSTRAINT ck_statement_available_has_storage
        CHECK (status <> 'AVAILABLE' OR storage_key IS NOT NULL),

    -- A PURGED statement must not still point at bytes.
    CONSTRAINT ck_statement_purged_has_no_storage
        CHECK (status <> 'PURGED' OR storage_key IS NULL)
)
PARTITION BY RANGE (period_start);

COMMENT ON TABLE statement IS
    'Account statements. RANGE-partitioned monthly on period_start; ~2.5 billion rows at full retention.';
COMMENT ON COLUMN statement.customer_id IS
    'Denormalised owner. Makes authorisation a single-column predicate instead of a join against 2.5 billion rows.';
COMMENT ON COLUMN statement.version IS
    'Regeneration version. A corrected statement is a new row, never an update in place.';

ALTER TABLE statement OWNER TO app_migrator;

-- ---------------------------------------------------------------------------------------------
-- THE HOT PATH INDEX: "my statements, newest first".
--
-- The column order is not arbitrary and must not be "tidied":
--   customer_id     - the authorisation predicate, always an equality
--   period_start DESC, id DESC - EXACTLY the ORDER BY the cursor query uses
--
-- The trailing id DESC is what makes keyset pagination free. The cursor is (period_start, id), so
-- the query orders by both; if the index carried only period_start, every page would need a sort
-- on top of the scan. Getting a sort out of a plan is worth far more than the byte it costs here.
--
-- PARTIAL on status = 'AVAILABLE'. Customers cannot see PENDING, FAILED or PURGED statements, so
-- those rows have no business in the index that serves them - and PURGED rows accumulate forever.
-- ---------------------------------------------------------------------------------------------
CREATE INDEX IF NOT EXISTS idx_statement_customer_period
    ON statement (customer_id, period_start DESC, id DESC)
    WHERE status = 'AVAILABLE';

-- The retention sweep: "what has passed its retain_until?" PARTIAL, because only statements that
-- still hold bytes can be purged - PENDING and already-PURGED rows are noise here.
CREATE INDEX IF NOT EXISTS idx_statement_retain_until
    ON statement (retain_until)
    WHERE status IN ('AVAILABLE', 'ARCHIVED');

-- Generation and regeneration look statements up by account and period.
CREATE INDEX IF NOT EXISTS idx_statement_account_period
    ON statement (account_id, period_start DESC);

-- ---------------------------------------------------------------------------------------------
-- Initial partitions: the current month plus three either side.
--
-- Created through ensure_range_partitions rather than by hand, so that the migration and the
-- PartitionMaintenanceService that keeps running afterwards use ONE implementation. Two mechanisms
-- that must agree on partition naming and bounds is one mechanism too many.
--
-- An INSERT with no matching partition FAILS, so this is not optional: without it the table
-- accepts no writes at all.
-- ---------------------------------------------------------------------------------------------
SELECT ensure_range_partitions('statement'::regclass, 'month', 6, now() - interval '3 months');
