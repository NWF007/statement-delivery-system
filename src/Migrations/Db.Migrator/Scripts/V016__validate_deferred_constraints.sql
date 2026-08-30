-- =============================================================================================
-- V016  Validate the constraints V013 and V015 added as NOT VALID.
--
-- WHY THIS IS A SEPARATE SCRIPT AND NOT THE LINE AFTER EACH ADD CONSTRAINT.
--
-- Rule 4 of Scripts/README.md splits adding a constraint into two steps: ADD CONSTRAINT ... NOT
-- VALID takes a brief ACCESS EXCLUSIVE lock and applies to new rows immediately, then VALIDATE
-- CONSTRAINT scans existing rows under SHARE UPDATE EXCLUSIVE, which does NOT block reads or
-- writes.
--
-- The half that is easy to get wrong: A LOCK TAKEN INSIDE A TRANSACTION IS HELD UNTIL THAT
-- TRANSACTION COMMITS. The migrator runs one transaction per script, so putting both statements in
-- one script would hold the ACCESS EXCLUSIVE lock across the validation scan anyway and buy
-- precisely nothing. The split has to be a script boundary, because that is where the transaction
-- boundary is.
--
-- ⚠ ON A POPULATED DATABASE THIS SCRIPT IS THE SLOW ONE. It scans `statement` - up to 2.5 billion
-- rows at full retention - and the migrator's default 30-second statement_timeout will kill it.
-- Raise Migration:StatementTimeoutSeconds for the deployment that first applies it, and do not run
-- it during the month-end generation window (rule 5). On an empty or small database it is instant,
-- which is every environment this has run in so far.
--
-- IF THIS SCRIPT FAILS it means existing rows violate a constraint that is already being enforced
-- on new ones. The constraint stays NOT VALID and the offending rows must be fixed before a re-run;
-- nothing is silently accepted either way.
-- =============================================================================================

ALTER TABLE customer_key VALIDATE CONSTRAINT ck_customer_key_active_has_material;
ALTER TABLE customer_key VALIDATE CONSTRAINT ck_customer_key_material_is_wrapped;
ALTER TABLE customer_key VALIDATE CONSTRAINT ck_customer_key_destroyed_has_no_material;

ALTER TABLE statement VALIDATE CONSTRAINT ck_statement_dek_is_wrapped;
ALTER TABLE statement VALIDATE CONSTRAINT ck_statement_available_has_key_material;
ALTER TABLE statement VALIDATE CONSTRAINT ck_statement_available_has_digest;
ALTER TABLE statement VALIDATE CONSTRAINT ck_statement_content_sha256_length;
