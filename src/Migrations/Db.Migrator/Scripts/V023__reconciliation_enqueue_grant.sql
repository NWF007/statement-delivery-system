-- =============================================================================================
-- V023  The retention worker may enqueue reconciliation runs.
--
-- V018 split reconciliation_run by actor: the API (app_delivery) INSERTs on-demand runs, the
-- worker (app_retention) claims and UPDATEs them. That matrix missed the worker's OWN cadence:
-- RetentionSweepService enqueues the nightly run itself (requested_by NULL), and on the first
-- real execution the enqueue died with 42501. A worker that cannot start its own nightly
-- reconciliation silently never reconciles - the drift detector goes dark, which is exactly the
-- failure reconciliation exists to catch.
--
-- INSERT only. The worker still cannot delete or rewrite a run's history; findings remain
-- INSERT-only from V018.
-- =============================================================================================

GRANT INSERT ON reconciliation_run TO app_retention;
