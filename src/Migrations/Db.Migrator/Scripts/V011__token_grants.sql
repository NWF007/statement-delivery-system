-- =============================================================================================
-- V011  Grants on download_token.
--
--   ROLE            GRANT                    WHY
--   ------------------------------------------------------------------------------------------
--   app_delivery    SELECT, INSERT, UPDATE   Issues links and revokes them.
--   app_download    SELECT, UPDATE           Consumes them. NO INSERT - see below.
--   app_generation  (nothing)                Rendering a statement has no business minting access.
--   app_retention   SELECT                   Reads for reporting; the daily partition DROP is DDL.
--
-- THE ABSENT INSERT IS THE POINT OF THIS FILE.
--
-- app_download runs the PUBLIC, UNAUTHENTICATED gateway. It is the most exposed process in the
-- system - reachable from the open internet, holding a credential that validates bearer tokens.
-- Without INSERT, an attacker who fully compromises it can consume tokens that already exist but
-- CANNOT MINT ONE. They cannot forge access to an arbitrary statement, because forging access
-- means writing a row, and that credential simply does not permit it.
--
-- Issuing lives behind JWT authentication in a different process with a different credential. An
-- attacker needs both to fabricate a download.
-- =============================================================================================

-- Issue and revoke. No DELETE: a spent token is evidence, and its partition is dropped wholesale
-- when the whole day expires.
GRANT SELECT, INSERT, UPDATE ON download_token TO app_delivery;

-- Consume only. SELECT for the failure diagnosis that classifies a zero-row consume into
-- CONSUMED / REVOKED / EXPIRED for the audit trail; UPDATE for the atomic consume itself.
GRANT SELECT, UPDATE ON download_token TO app_download;

-- Reporting only.
GRANT SELECT ON download_token TO app_retention;

-- app_generation gets nothing at all. Explicit, so the omission reads as a decision rather than an
-- oversight to be "fixed" by the next person who sees the gap.

-- Nobody deletes rows. Expired tokens leave with their partition.
REVOKE DELETE, TRUNCATE ON download_token FROM PUBLIC;
REVOKE DELETE, TRUNCATE ON download_token FROM app_delivery, app_download, app_generation, app_retention;

-- Belt and braces on the control this file exists for.
REVOKE INSERT ON download_token FROM app_download, app_generation, app_retention;
