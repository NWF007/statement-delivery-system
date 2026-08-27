-- =============================================================================================
-- V002  Distributed lease table.
--
-- Leader election for scheduled work. This replaces the usual pg_advisory_lock recipe, which is
-- UNUSABLE here: every service connects through PgBouncer in transaction pooling mode, so a
-- session advisory lock is taken on one backend and released against another, and leaks forever
-- with nothing to report it. See docs/adr/0008-pgbouncer-transaction-pooling.md.
--
-- Three properties this has that an advisory lock does not:
--   * it works through any pooler, because acquire-and-renew is one atomic statement;
--   * it is observable - SELECT the table and see who holds what, until when;
--   * the monotonic fence token lets a downstream resource reject a holder that was paused by a
--     long GC or a hypervisor stall and resumed after its lease was already taken over.
--
-- Deliberately NOT partitioned: it holds one row per named lease, forever. Partitioning it would
-- add maintenance to a table that will never exceed a handful of rows.
-- =============================================================================================

CREATE TABLE IF NOT EXISTS distributed_lease
(
    -- The name of the work being guarded, e.g. 'retention-sweep'. Primary key, so the atomic
    -- INSERT ... ON CONFLICT DO UPDATE has exactly one row to contend over.
    lease_name   TEXT        NOT NULL,

    -- Who holds it. Host name plus process id; in Kubernetes the host name is the pod name, so
    -- this table names the leader without correlating anything.
    holder_id    TEXT        NOT NULL,

    acquired_at  TIMESTAMPTZ NOT NULL,

    -- Past this instant any other holder may take over. The only thing that bounds how long the
    -- system runs leaderless after a holder dies without releasing.
    expires_at   TIMESTAMPTZ NOT NULL,

    -- Strictly increasing per lease name, across every acquisition by every holder, for all time.
    -- A time-to-live cannot stop a stalled leader from waking up and acting: it has no way to know
    -- it was stalled. Rejecting a token lower than the highest already seen is what closes that
    -- window, and it is the reason this column is not merely a counter for humans to look at.
    fence_token  BIGINT      NOT NULL,

    CONSTRAINT pk_distributed_lease PRIMARY KEY (lease_name),
    CONSTRAINT ck_distributed_lease_expiry CHECK (expires_at > acquired_at),
    CONSTRAINT ck_distributed_lease_fence CHECK (fence_token > 0)
);

COMMENT ON TABLE distributed_lease IS
    'Leader election leases. Supersedes pg_advisory_lock, which is incompatible with PgBouncer transaction pooling.';
COMMENT ON COLUMN distributed_lease.fence_token IS
    'Monotonic per lease_name. Pass to downstream writes so a stalled former holder cannot act on a lease it no longer owns.';

ALTER TABLE distributed_lease OWNER TO app_migrator;

-- Operational view: which leases are live right now. Cheap, and the first thing anybody wants at
-- 3am when a scheduled job has not run.
CREATE INDEX IF NOT EXISTS ix_distributed_lease_live
    ON distributed_lease (expires_at DESC);

-- ---------------------------------------------------------------------------------------------
-- Grants.
--
-- Only the two workers contend for leadership. The APIs get SELECT so an operator (or a
-- diagnostic endpoint) can see who the leader is, and nothing more: an API replica that could
-- take a lease could quietly become the thing that runs destructive scheduled work.
--
-- No DELETE for anyone. Releasing a lease expires the row rather than removing it, which is what
-- keeps fence_token monotonic - a deleted row restarts the sequence at 1 and every fencing check
-- downstream silently stops working.
-- ---------------------------------------------------------------------------------------------
GRANT SELECT, INSERT, UPDATE ON distributed_lease TO app_generation;
GRANT SELECT, INSERT, UPDATE ON distributed_lease TO app_retention;
GRANT SELECT                 ON distributed_lease TO app_delivery;
GRANT SELECT                 ON distributed_lease TO app_download;
