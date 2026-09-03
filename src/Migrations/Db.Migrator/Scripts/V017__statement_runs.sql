-- =============================================================================================
-- V017  Statement runs: the batch-generation plan and its work queue.
--
-- ⚠ NUMBERING AND SCOPE DEVIATION, RECORDED HERE BECAUSE THIS FILE IS WHERE A READER WILL LOOK.
-- The original design brief called this migration V013__run_item_claims.sql and described it as
-- ALTERing a statement_run_item table it assumed an earlier migration had already created. Neither
-- premise survives contact with the repository: V013 was taken by V013__customer_key_material.sql,
-- which had itself been renumbered off V012 for the same reason, and no earlier migration created
-- either run table - a fact already recorded in docs/LIMITATIONS.md when this script was written.
-- So this script CREATES both tables, claim columns included, as V017. Forward-only, never edited
-- once run, per the standing constraint.
-- =============================================================================================

-- ---------------------------------------------------------------------------------------------
-- The run: one row per statement period. The UNIQUE constraint is the idempotency anchor for
-- "plan a run": a second POST for the same period conflicts here and gets the existing run back,
-- so a retried trigger can never produce a duplicate plan.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS statement_run
(
    -- Application-supplied UUIDv7, like every other identifier in this system.
    id           UUID        NOT NULL,

    period_start DATE        NOT NULL,
    period_end   DATE        NOT NULL,

    -- PLANNING: created, items not yet fully enqueued. RUNNING: workers may claim. PAUSED: a
    -- deliberate operator/orchestrator hold (ledger circuit open) - work already done is kept.
    -- COMPLETED: terminal; re-triggering the period is a no-op.
    status       TEXT        NOT NULL DEFAULT 'PLANNING',

    -- Set once planning finishes, from a COUNT over the items. Zero while PLANNING.
    total_items  BIGINT      NOT NULL DEFAULT 0,

    -- The completion deadline the monitoring loop projects against. Nullable: a run without a
    -- deadline is monitored but never alerts. Stored on the row rather than living only in worker
    -- configuration, so an operator reading the table sees the same number the alert used.
    deadline_at  TIMESTAMPTZ NULL,

    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT pk_statement_run PRIMARY KEY (id),
    CONSTRAINT uq_statement_run_period UNIQUE (period_start, period_end),
    CONSTRAINT ck_statement_run_period CHECK (period_end >= period_start),
    CONSTRAINT ck_statement_run_status
        CHECK (status IN ('PLANNING', 'RUNNING', 'PAUSED', 'COMPLETED')),
    CONSTRAINT ck_statement_run_totals CHECK (total_items >= 0)
);

COMMENT ON TABLE statement_run IS
    'One batch-generation run per statement period. UNIQUE(period) makes re-triggering idempotent.';

ALTER TABLE statement_run OWNER TO app_migrator;

-- ---------------------------------------------------------------------------------------------
-- The work queue. One row per (run, account); the UNIQUE constraint is what makes PLANNING
-- resumable - an orchestrator that dies at account 12 million restarts, streams the accounts
-- again, and every INSERT for an already-enqueued account hits ON CONFLICT DO NOTHING.
--
-- id is a BIGINT identity rather than a UUID, deliberately: the claim query orders by id, and a
-- monotonically increasing integer gives both a dense claim order and right-edge index inserts
-- during the single-writer planning phase. Nothing outside this table ever references the id.
--
-- No foreign key to account, also deliberately: every row is INSERTed from a SELECT over account
-- itself, so referential integrity holds by construction, and a per-row FK probe on a 30-million
-- row planning pass buys re-verification of that at real cost. The FK to statement_run stays -
-- runs are few and the probe is cache-resident.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS statement_run_item
(
    id           BIGINT      GENERATED ALWAYS AS IDENTITY,
    run_id       UUID        NOT NULL,
    account_id   UUID        NOT NULL,

    -- QUEUED: claimable. RENDERING: claimed by a live (or dead - see the reaper) worker.
    -- DONE: statement row committed. FAILED: the last attempt errored; claimable again while
    -- attempts remain, quarantined by exclusion once attempts reach the ceiling.
    status       TEXT        NOT NULL DEFAULT 'QUEUED',

    -- ⚠ INCREMENTED ON CLAIM, NOT ON COMPLETION. A worker that crashes mid-render never reaches
    -- any completion code, so an increment there would let a poison item loop forever. Burning
    -- the attempt at claim time means a crashing item still marches toward the ceiling and gets
    -- quarantined. The stale-claim reaper relies on this: it resets status but never attempts.
    attempts     INT         NOT NULL DEFAULT 0,

    -- Message and exception TYPE only. Never a stack trace, and NEVER statement content - this
    -- column is readable by every role that can SELECT the queue.
    last_error   TEXT        NULL,

    -- W3C traceparent captured at PLANNING time and restored by the worker, so one render is a
    -- single trace across the asynchronous queue boundary.
    trace_parent TEXT        NULL,

    claimed_at   TIMESTAMPTZ NULL,
    claimed_by   TEXT        NULL,
    started_at   TIMESTAMPTZ NULL,
    finished_at  TIMESTAMPTZ NULL,

    -- The statement this item produced, set in the same transaction that marks it DONE. Pure
    -- traceability; the statement row is the source of truth.
    statement_id UUID        NULL,

    CONSTRAINT pk_statement_run_item PRIMARY KEY (id),
    CONSTRAINT uq_run_item_account UNIQUE (run_id, account_id),
    CONSTRAINT fk_run_item_run FOREIGN KEY (run_id) REFERENCES statement_run (id),
    CONSTRAINT ck_run_item_status CHECK (status IN ('QUEUED', 'RENDERING', 'DONE', 'FAILED')),
    CONSTRAINT ck_run_item_attempts CHECK (attempts >= 0)
);

COMMENT ON TABLE statement_run_item IS
    'PostgreSQL work queue for batch generation. Claimed with FOR UPDATE SKIP LOCKED; attempts increment on claim.';
COMMENT ON COLUMN statement_run_item.attempts IS
    'Incremented at CLAIM time so a crashed worker still burns an attempt and poison items reach the ceiling.';

ALTER TABLE statement_run_item OWNER TO app_migrator;

-- The claim query's index: claimable rows in claim order, per run. PARTIAL on the two claimable
-- statuses so DONE rows - which is eventually all of them - never enter it. The attempts ceiling
-- is a query parameter and so cannot be part of the partial predicate; the residual filter over
-- this index is cheap because quarantined rows are a tiny minority.
CREATE INDEX IF NOT EXISTS idx_run_item_claim
    ON statement_run_item (run_id, id)
    WHERE status IN ('QUEUED', 'FAILED');

-- Reaper index: find stale RENDERING claims without scanning DONE rows.
CREATE INDEX IF NOT EXISTS idx_run_item_stale
    ON statement_run_item (claimed_at)
    WHERE status = 'RENDERING';

-- Progress counters without a full scan.
CREATE INDEX IF NOT EXISTS idx_run_item_status
    ON statement_run_item (run_id, status);

-- ---------------------------------------------------------------------------------------------
-- Grants.
--
-- app_generation plans, claims, renders and records: SELECT, INSERT, UPDATE on both. No DELETE -
-- run history is an operational record, and how long it is kept has not been decided yet. Until
-- it is, nothing removes these rows: no application role holds DELETE, as the REVOKEs at the foot
-- of this script enforce.
--
-- ⚠ DEVIATION FROM THE BRIEF, WITH THE REASON IN FULL. The brief says "no other role gets more
-- than SELECT" - and also requires POST /v1/statement-runs and the failures/retry endpoint on
-- the staff API at :8081, which runs as app_delivery. Those two sentences cannot both hold: the
-- staff API must INSERT the run row and reset FAILED items. The narrowest grant that satisfies
-- the endpoints wins: app_delivery gets INSERT on statement_run (create only - it can never
-- update a run's status or totals) and a COLUMN-SCOPED UPDATE on exactly the three item columns
-- the retry path touches. It cannot claim (claimed_*), cannot finish (finished_at), and cannot
-- move total_items.
-- ---------------------------------------------------------------------------------------------
GRANT SELECT, INSERT, UPDATE ON statement_run       TO app_generation;
GRANT SELECT, INSERT, UPDATE ON statement_run_item  TO app_generation;

GRANT SELECT                               ON statement_run      TO app_delivery;
GRANT INSERT                               ON statement_run      TO app_delivery;
GRANT SELECT                               ON statement_run_item TO app_delivery;
GRANT UPDATE (status, attempts, last_error) ON statement_run_item TO app_delivery;

REVOKE ALL ON statement_run      FROM app_download, app_retention;
REVOKE ALL ON statement_run_item FROM app_download, app_retention;
GRANT  SELECT ON statement_run, statement_run_item TO app_retention;

REVOKE DELETE, TRUNCATE ON statement_run, statement_run_item FROM PUBLIC;
REVOKE DELETE, TRUNCATE ON statement_run, statement_run_item
    FROM app_delivery, app_download, app_generation, app_retention;
