-- =============================================================================================
-- V018  The retention lifecycle: erasure scheduling, restore requests, tombstones, orphan
--       reports and reconciliation runs.
--
-- Prompt 6 turns the dormant compliance surface (V008's legal_hold and customer_key) into the
-- working lifecycle. Everything here is additive except two constraint rebuilds on customer_key,
-- both widening: forward-only, never edited (see V016's note on the discipline).
-- =============================================================================================

-- ---------------------------------------------------------------------------------------------
-- customer_key: the cooling-off state.
--
-- Crypto-erasure is the one operation in this system that no backup can undo - the backups are
-- ciphertext under the key being destroyed. Irreversible operations get a reversal window
-- (ADR-0035): a request schedules destruction seven days out, and the executor re-evaluates the
-- legal position when the window closes. SCHEDULED_DESTRUCTION is that window's state.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE customer_key
    ADD COLUMN IF NOT EXISTS destruction_due_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS destruction_reason TEXT;

COMMENT ON COLUMN customer_key.destruction_due_at IS
    'When the cooling-off window closes and the executor may destroy the key. NULL unless scheduled.';
COMMENT ON COLUMN customer_key.destruction_reason IS
    'Why the key was (or is scheduled to be) destroyed, e.g. the POPIA s24 request reference.';

-- Widen the status vocabulary. DROP + ADD is the only way to change a CHECK; the new constraint
-- is strictly more permissive plus the new consistency rules, so existing rows all satisfy it.
ALTER TABLE customer_key DROP CONSTRAINT IF EXISTS ck_customer_key_status;
ALTER TABLE customer_key
    ADD CONSTRAINT ck_customer_key_status
        CHECK (status IN ('ACTIVE', 'ROTATING', 'SCHEDULED_DESTRUCTION', 'DESTROYED')) NOT VALID;

-- A scheduled destruction must say when the window closes; anything else must not carry a due
-- date, or a cancelled erasure would leave a live fuse behind.
ALTER TABLE customer_key
    ADD CONSTRAINT ck_customer_key_scheduled_has_due
        CHECK ((status = 'SCHEDULED_DESTRUCTION') = (destruction_due_at IS NOT NULL)) NOT VALID;

-- A destroyed key must record why. The audit trail has it too; this makes the row self-evidencing.
ALTER TABLE customer_key
    ADD CONSTRAINT ck_customer_key_destroyed_has_reason
        CHECK (status <> 'DESTROYED' OR destruction_reason IS NOT NULL) NOT VALID;

-- ---------------------------------------------------------------------------------------------
-- legal_hold: the API's request fields.
--
-- V008 modelled placement and release; the Part B API also captures WHY. The case reference
-- identifies the matter; the reason is the human sentence beside it.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE legal_hold
    ADD COLUMN IF NOT EXISTS reason         TEXT,
    ADD COLUMN IF NOT EXISTS release_reason TEXT;

COMMENT ON COLUMN legal_hold.reason IS 'Why the hold was placed, from the placing request.';
COMMENT ON COLUMN legal_hold.release_reason IS 'Why the hold was released. Set with released_at.';

-- ---------------------------------------------------------------------------------------------
-- erasure_request: one row per POPIA s24 request, surviving cancellation and completion.
--
-- The customer_key columns above drive the EXECUTOR; this table is the record of the REQUESTS -
-- who asked, on what basis, what happened. A cancelled request keeps its row: the sequence
-- "requested, cancelled during cooling-off" is itself evidence.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS erasure_request
(
    -- Application-supplied UUIDv7. No DEFAULT, deliberately - see V005.
    id                 UUID        NOT NULL,
    customer_id        UUID        NOT NULL,

    reason             TEXT        NOT NULL,
    request_reference  TEXT        NOT NULL,
    requested_by       TEXT        NOT NULL,
    requested_at       TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- The cooling-off window's end, mirrored onto customer_key.destruction_due_at while scheduled.
    due_at             TIMESTAMPTZ NOT NULL,

    status             TEXT        NOT NULL DEFAULT 'SCHEDULED',
    cancelled_at       TIMESTAMPTZ,
    cancelled_by       TEXT,
    completed_at       TIMESTAMPTZ,

    CONSTRAINT pk_erasure_request PRIMARY KEY (id),
    CONSTRAINT fk_erasure_request_customer FOREIGN KEY (customer_id) REFERENCES customer (id),
    CONSTRAINT ck_erasure_request_status
        CHECK (status IN ('SCHEDULED', 'CANCELLED', 'COMPLETED')),
    CONSTRAINT ck_erasure_request_reference CHECK (length(request_reference) BETWEEN 1 AND 256),
    CONSTRAINT ck_erasure_request_cancelled_consistent
        CHECK ((status = 'CANCELLED') = (cancelled_at IS NOT NULL)),
    CONSTRAINT ck_erasure_request_completed_consistent
        CHECK ((status = 'COMPLETED') = (completed_at IS NOT NULL))
);

COMMENT ON TABLE erasure_request IS
    'POPIA s24 erasure requests. Rows survive cancellation and completion as the record of the request.';

ALTER TABLE erasure_request OWNER TO app_migrator;

-- At most one live request per customer. Partial, so history does not block a new request.
CREATE UNIQUE INDEX IF NOT EXISTS uq_erasure_request_active
    ON erasure_request (customer_id)
    WHERE status = 'SCHEDULED';

-- ---------------------------------------------------------------------------------------------
-- restore_request: the honest asynchronous contract for archived statements.
--
-- A restore from cold storage takes hours in production. The API answers 202 with this row's id
-- and estimate; a worker job completes it and publishes statement.restored through the outbox.
-- No FK to statement: it is partitioned by period_start and the pair is carried instead,
-- matching how statement_run_item addresses statements (V017).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS restore_request
(
    id            UUID        NOT NULL,
    statement_id  UUID        NOT NULL,
    period_start  DATE        NOT NULL,
    customer_id   UUID        NOT NULL,

    requested_by  TEXT        NOT NULL,
    requested_at  TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- The estimate given to the caller. Locally this is the simulated latency (ADR-0038); in
    -- production it comes from the storage class's published retrieval time.
    due_at        TIMESTAMPTZ NOT NULL,

    status        TEXT        NOT NULL DEFAULT 'PENDING',
    available_at  TIMESTAMPTZ,

    -- Restored copies are temporary in real Glacier; the local simulation honours that shape.
    expires_at    TIMESTAMPTZ,

    failure_note  TEXT,

    CONSTRAINT pk_restore_request PRIMARY KEY (id),
    CONSTRAINT ck_restore_request_status
        CHECK (status IN ('PENDING', 'AVAILABLE', 'FAILED')),
    CONSTRAINT ck_restore_request_available_consistent
        CHECK ((status = 'AVAILABLE') = (available_at IS NOT NULL))
);

COMMENT ON TABLE restore_request IS
    'Asynchronous archive-restore requests: 202 now, statement.restored through the outbox later.';

ALTER TABLE restore_request OWNER TO app_migrator;

-- The completion job: "what is due?". Partial on PENDING - completed requests are history.
CREATE INDEX IF NOT EXISTS idx_restore_request_pending
    ON restore_request (due_at)
    WHERE status = 'PENDING';

-- The download path: "does this statement have a live restore?".
CREATE INDEX IF NOT EXISTS idx_restore_request_statement
    ON restore_request (statement_id, status);

-- ---------------------------------------------------------------------------------------------
-- storage_tombstone: what happened to bytes that no statement row points at any more.
--
-- Purge nulls statement.storage_key (V006's ck_statement_purged_has_no_storage demands it), and
-- erasure leaves undecryptable objects in the bucket that nothing references. Without this
-- ledger the orphan sweep cannot tell "a write failed and leaked an object" from "we erased
-- that customer and the ciphertext lawfully remains under Object Lock" - the first is a finding,
-- the second is the system working as designed.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS storage_tombstone
(
    storage_key   TEXT        NOT NULL,
    statement_id  UUID        NOT NULL,
    period_start  DATE        NOT NULL,
    customer_id   UUID        NOT NULL,

    -- PURGED: the versions were deleted; the key existing in storage again would be a finding.
    -- ERASED: the object remains, permanently undecryptable, until its lock expires and a later
    --         purge sweep deletes it.
    kind          TEXT        NOT NULL,
    recorded_at   TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT pk_storage_tombstone PRIMARY KEY (storage_key),
    CONSTRAINT ck_storage_tombstone_kind CHECK (kind IN ('PURGED', 'ERASED'))
);

COMMENT ON TABLE storage_tombstone IS
    'Ledger of storage keys the statement table no longer references, and why. Read by the orphan sweep.';

ALTER TABLE storage_tombstone OWNER TO app_migrator;

-- ---------------------------------------------------------------------------------------------
-- orphan_report and the sweep's resumable cursor. Report-only, permanently (ADR-0039): objects
-- under Compliance lock cannot be deleted anyway, and inventory-driven automatic deletion is
-- precisely the job that destroys real data when the comparison has a bug.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS orphan_report
(
    id           BIGINT GENERATED ALWAYS AS IDENTITY,
    storage_key  TEXT        NOT NULL,
    size_bytes   BIGINT      NOT NULL,

    -- NO_ROW: no statement row matches the key. KEY_MISMATCH: a row exists but points elsewhere.
    reason       TEXT        NOT NULL,
    seen_at      TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT pk_orphan_report PRIMARY KEY (id),
    CONSTRAINT ck_orphan_report_reason CHECK (reason IN ('NO_ROW', 'KEY_MISMATCH'))
);

COMMENT ON TABLE orphan_report IS
    'Objects in storage that no statement row accounts for. Written by the weekly sweep; never acted on automatically.';

ALTER TABLE orphan_report OWNER TO app_migrator;

CREATE INDEX IF NOT EXISTS idx_orphan_report_seen ON orphan_report (seen_at);

-- Single-row cursor so the sweep resumes where it stopped instead of restarting a 2.5-billion
-- object walk. The boolean primary key is the standard one-row-table trick.
CREATE TABLE IF NOT EXISTS orphan_sweep_state
(
    singleton          BOOLEAN     NOT NULL DEFAULT TRUE,
    shard_prefix       TEXT,
    continuation_token TEXT,
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT pk_orphan_sweep_state PRIMARY KEY (singleton),
    CONSTRAINT ck_orphan_sweep_state_singleton CHECK (singleton)
);

ALTER TABLE orphan_sweep_state OWNER TO app_migrator;

-- ---------------------------------------------------------------------------------------------
-- reconciliation_run and reconciliation_finding: the daily proof the parts still agree.
-- The API enqueues (REQUESTED); the leader-elected worker executes; findings are rows, not logs,
-- because GET /v1/admin/reconciliation/latest has to show them.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS reconciliation_run
(
    id            UUID        NOT NULL,
    requested_by  TEXT,
    requested_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    started_at    TIMESTAMPTZ,
    completed_at  TIMESTAMPTZ,
    status        TEXT        NOT NULL DEFAULT 'REQUESTED',

    CONSTRAINT pk_reconciliation_run PRIMARY KEY (id),
    CONSTRAINT ck_reconciliation_run_status
        CHECK (status IN ('REQUESTED', 'RUNNING', 'COMPLETED', 'FAILED'))
);

COMMENT ON TABLE reconciliation_run IS
    'One row per reconciliation pass. requested_by NULL means the daily schedule.';

ALTER TABLE reconciliation_run OWNER TO app_migrator;

CREATE INDEX IF NOT EXISTS idx_reconciliation_run_requested
    ON reconciliation_run (requested_at)
    WHERE status = 'REQUESTED';

CREATE TABLE IF NOT EXISTS reconciliation_finding
(
    id        BIGINT GENERATED ALWAYS AS IDENTITY,
    run_id    UUID        NOT NULL,

    -- MISSING_OBJECT | ORPHANED_OBJECT | LEGAL_HOLD_DRIFT | RETENTION_DRIFT | AUDIT_CHAIN
    -- | INCOMPLETE_ERASURE - the six checks of Part G.
    check_name TEXT       NOT NULL,
    severity  TEXT        NOT NULL,
    subject   TEXT        NOT NULL,
    detail    TEXT        NOT NULL,
    found_at  TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT pk_reconciliation_finding PRIMARY KEY (id),
    CONSTRAINT fk_reconciliation_finding_run
        FOREIGN KEY (run_id) REFERENCES reconciliation_run (id),
    CONSTRAINT ck_reconciliation_finding_severity
        CHECK (severity IN ('CRITICAL', 'WARNING', 'INFO'))
);

ALTER TABLE reconciliation_finding OWNER TO app_migrator;

CREATE INDEX IF NOT EXISTS idx_reconciliation_finding_run ON reconciliation_finding (run_id);

-- ---------------------------------------------------------------------------------------------
-- GRANTS. V009's matrix, extended. Where an endpoint's host role and V009's original assignment
-- disagree, the endpoint wins - V017 set that precedent for statement_run, and the legal-hold
-- API repeats it: the staff API at :8081 runs as app_delivery, so app_delivery places and
-- releases holds; app_retention keeps its grants for the sweep's reads and reconciliation.
-- ---------------------------------------------------------------------------------------------
GRANT INSERT, UPDATE ON legal_hold TO app_delivery;

-- Erasure requests come in through the API (DPO scope), so app_delivery writes them - and flips
-- customer_key into (or out of) the cooling-off state, BUT ONLY via the scheduling columns.
-- Column-scoped, so the role that schedules can never touch wrapped_cek: destruction itself
-- remains app_retention's alone.
GRANT SELECT, INSERT, UPDATE ON erasure_request TO app_delivery;
GRANT UPDATE (status, destruction_due_at, destruction_reason) ON customer_key TO app_delivery;

GRANT SELECT, UPDATE ON erasure_request TO app_retention;

-- Restores: requested by the API, completed by the worker, read back by both.
GRANT SELECT, INSERT ON restore_request TO app_delivery;
GRANT SELECT         ON restore_request TO app_download;
GRANT SELECT, UPDATE ON restore_request TO app_retention;

-- Tombstones are written by whichever job removed the reference: purge and erasure both run as
-- app_retention. The sweep reads them; nobody updates or deletes - a tombstone is history.
GRANT SELECT, INSERT ON storage_tombstone TO app_retention;
REVOKE UPDATE, DELETE, TRUNCATE ON storage_tombstone FROM PUBLIC;

-- Orphan reporting is the retention worker's.
GRANT SELECT, INSERT         ON orphan_report      TO app_retention;
GRANT SELECT                 ON orphan_report      TO app_delivery;
GRANT SELECT, INSERT, UPDATE ON orphan_sweep_state TO app_retention;

-- Reconciliation: the API enqueues and reads; the worker executes.
GRANT SELECT, INSERT         ON reconciliation_run     TO app_delivery;
GRANT SELECT                 ON reconciliation_finding TO app_delivery;
GRANT SELECT, UPDATE         ON reconciliation_run     TO app_retention;
GRANT SELECT, INSERT         ON reconciliation_finding TO app_retention;

-- The worker publishes statement.restored through the outbox: INSERT joins its existing
-- SELECT/UPDATE/DELETE from V004.
GRANT INSERT ON outbox TO app_retention;

-- PostgreSQL 17: MAINTAIN lets app_retention VACUUM customer_key after nulling wrapped_cek.
-- MVCC keeps the old row version - wrapped CEK bytes included - on the page until vacuumed, so
-- an erasure that skips this has not removed the key material from disk (ADR-0035).
GRANT MAINTAIN ON customer_key TO app_retention;
