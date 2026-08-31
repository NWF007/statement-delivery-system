-- =============================================================================================
-- V022  Blocked-erasure bookkeeping: audit on TRANSITION, not on evaluation.
--
-- The executor re-evaluates every blocked request each pass, and each pass appended
-- ERASURE_BLOCKED - roughly 288 chain entries per day per blocked request at the old
-- every-tick cadence, each one crossing the audit chain's FOR UPDATE serialisation point.
-- A blocked erasure is ONE fact until something about it changes; these columns are how the
-- executor knows whether anything did. It appends when the blocking reason CHANGES, or once
-- per 24 hours as a heartbeat, whichever comes first.
-- =============================================================================================

ALTER TABLE erasure_request
    ADD COLUMN IF NOT EXISTS last_blocked_reason TEXT,
    ADD COLUMN IF NOT EXISTS last_blocked_at     TIMESTAMPTZ;

COMMENT ON COLUMN erasure_request.last_blocked_reason IS
    'The decision type that most recently blocked execution. Audit appends only when this changes (or daily).';
COMMENT ON COLUMN erasure_request.last_blocked_at IS
    'When the block was last audited, for the daily heartbeat.';
