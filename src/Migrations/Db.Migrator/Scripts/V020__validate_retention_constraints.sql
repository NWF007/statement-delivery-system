-- =============================================================================================
-- V020  Validate the constraints V018 added as NOT VALID.
--
-- Rule 4 of Scripts/README.md, same shape as V016: ADD CONSTRAINT ... NOT VALID applies to new
-- rows immediately under a brief lock; VALIDATE scans existing rows under SHARE UPDATE
-- EXCLUSIVE, which blocks neither reads nor writes - and the split must be a script boundary,
-- because that is where the migrator's transaction boundary is.
--
-- customer_key holds one row per customer (up to 26 million); the scans are quick relative to
-- V016's statement scans, but the same timeout note applies on a populated estate.
-- =============================================================================================

ALTER TABLE customer_key VALIDATE CONSTRAINT ck_customer_key_status;
ALTER TABLE customer_key VALIDATE CONSTRAINT ck_customer_key_scheduled_has_due;
ALTER TABLE customer_key VALIDATE CONSTRAINT ck_customer_key_destroyed_has_reason;
