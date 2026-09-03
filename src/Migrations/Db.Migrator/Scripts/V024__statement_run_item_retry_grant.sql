-- =============================================================================================
-- V024  The staff API may fully reset a FAILED run item on retry.
--
-- V017 gave app_delivery a column-scoped UPDATE on statement_run_item limited to
-- (status, attempts, last_error) - "the three item columns the retry path touches". The retry
-- statements (StatementRunRepository.RetryAllSql / RetrySomeSql) have since grown to also clear
-- claimed_at, claimed_by, started_at and finished_at, so a re-queued item does not carry the
-- ghost of its failed attempt into reporting. Under V017's grant every call to
-- POST /v1/statement-runs/{runId}/failures/retry died with 42501 - found live by the Postman
-- collection, not by a test, because the integration suite runs retry as the superuser.
--
-- Still column-scoped, still FAILED-rows-only by the SQL's predicate. app_delivery cannot claim
-- an item for itself in any meaningful sense: it can only NULL the claim columns of a row that is
-- already FAILED, which is a reset, not a claim. total_items and run status stay out of reach.
-- =============================================================================================

GRANT UPDATE (claimed_at, claimed_by, started_at, finished_at) ON statement_run_item TO app_delivery;
