-- =============================================================================================
-- V013  Customer key material: the middle tier of the key hierarchy.
--
-- ⚠ NUMBERED V013, NOT V012 AS THE BRIEF SPECIFIED. V012__audit_verify_grant.sql already exists
-- and has already run. Migrations here are forward-only and are never edited once applied, so a
-- second V012 is not an option: DbUp keys its journal on the script name, and two scripts claiming
-- the same version would either be silently skipped or applied out of order depending on which
-- name sorted first. The content is exactly what was asked for; only the number moved.
--
-- WHAT THIS ADDS. V008 created customer_key holding a KEK IDENTIFIER and nothing else, on the
-- assumption of one KMS key per customer. That assumption did not survive contact with the price
-- list: at roughly $1 per customer-managed key per month and 26 million customers, it is $26
-- million a month. See docs/adr/0020-three-tier-key-hierarchy.md.
--
-- So the customer's key stops being a KMS key and becomes a KEY WE HOLD, WRAPPED - encrypted by
-- one of 1,024 cohort KEKs that do live in KMS. The wrapped bytes go in this table. The cohort
-- assignment goes in this table. The KEK material never does, and never did.
--
-- THE COLUMN THAT MATTERS IS wrapped_cek, AND THE GRANT ON IT MATTERS AS MUCH AS THE COLUMN.
-- See the privileges section at the bottom: app_delivery loses table-level SELECT here.
-- =============================================================================================

-- IF NOT EXISTS on created_at because V008 already created it. Repeating the column is how the
-- brief described the change and it is harmless to state twice; ADD COLUMN without the guard would
-- abort the whole script on a table that is already correct.
ALTER TABLE customer_key
    ADD COLUMN IF NOT EXISTS cohort_id     SMALLINT,
    ADD COLUMN IF NOT EXISTS wrapped_cek   BYTEA,
    ADD COLUMN IF NOT EXISTS cek_algorithm TEXT        NOT NULL DEFAULT 'AES-256-GCM',
    ADD COLUMN IF NOT EXISTS created_at    TIMESTAMPTZ NOT NULL DEFAULT now();

COMMENT ON COLUMN customer_key.cohort_id IS
    'Which of the 1,024 cohort KEKs wraps this CEK. FIXED FOR THE LIFE OF THE ROW: recomputing it under a different cohort count makes the CEK unwrappable and crypto-erases the customer by accident.';
COMMENT ON COLUMN customer_key.wrapped_cek IS
    'The customer encryption key, wrapped by the cohort KEK. Never plaintext. Useless without KMS.';
COMMENT ON COLUMN customer_key.cek_algorithm IS
    'How wrapped_cek is wrapped, so a future algorithm change leaves old rows decodable.';

-- ---------------------------------------------------------------------------------------------
-- Constraints. Each is about a state that must be impossible.
--
-- ⚠ EVERY ONE IS "NOT VALID", AND THAT IS RULE 4 OF Scripts/README.md, NOT A WEAKENING.
--
-- NOT VALID means "enforce this on every new and updated row, but do not scan what is already
-- there". The enforcement people care about is immediate and total. What is deferred is only the
-- proof about EXISTING rows, and it is deferred to V016, which runs in its own transaction.
--
-- A plain ADD CONSTRAINT does both in one statement, holding ACCESS EXCLUSIVE for the length of a
-- full table scan. `statement` reaches 2.5 billion rows; that lock blocks every read and every
-- write on it for the duration, and the migration that looked like three lines has taken the
-- platform down. The two-step has to be two SCRIPTS rather than two statements, because a lock
-- taken inside a transaction is held until that transaction commits - splitting them within one
-- script would buy nothing at all.
-- ---------------------------------------------------------------------------------------------
-- An ACTIVE key row with no wrapped CEK is a row that claims a customer has usable key material
-- when they do not. Every statement written against it would be encrypted under a key nothing
-- records, and the failure would surface months later as an undecryptable download with no cause.
ALTER TABLE customer_key
    ADD CONSTRAINT ck_customer_key_active_has_material
        CHECK (status <> 'ACTIVE' OR (wrapped_cek IS NOT NULL AND cohort_id IS NOT NULL)) NOT VALID;

-- A wrapped CEK shorter than the wrapping overhead is a plaintext key somebody has persisted.
-- The envelope is version(1) || nonce(12) || ciphertext(32) || tag(16) = 61 bytes, so a raw 32-byte
-- key cannot satisfy this. Enforced in the database rather than in application code because there
-- are four services and only one of them needs the bug.
ALTER TABLE customer_key
    ADD CONSTRAINT ck_customer_key_material_is_wrapped
        CHECK (wrapped_cek IS NULL OR octet_length(wrapped_cek) >= 40) NOT VALID;

-- DESTROYED means the material is gone, not merely flagged. A row marked destroyed that still
-- carries wrapped_cek has not erased anything - it has only changed a string.
ALTER TABLE customer_key
    ADD CONSTRAINT ck_customer_key_destroyed_has_no_material
        CHECK (status <> 'DESTROYED' OR wrapped_cek IS NULL) NOT VALID;

-- The cohort index is NOT created here. customer_key already exists and may already hold rows, so
-- a plain CREATE INDEX would hold a lock for the whole build - rule 1 of Scripts/README.md. It is
-- built CONCURRENTLY in V014__customer_key_cohort_index.notx.sql, which runs outside a transaction.

-- ---------------------------------------------------------------------------------------------
-- The same envelope rule for the statement table's data keys.
-- ---------------------------------------------------------------------------------------------
-- V006 created wrapped_dek nullable and unused. It is used from here on, and the same "is it
-- actually wrapped" floor applies. The wrapped-key invariant is expressed as a constraint rather
-- than as a query someone has to remember to run.
ALTER TABLE statement
    ADD CONSTRAINT ck_statement_dek_is_wrapped
        CHECK (wrapped_dek IS NULL OR octet_length(wrapped_dek) >= 40) NOT VALID;

-- An AVAILABLE statement with a storage key but no data key is unreadable. V006 already requires
-- the storage key; this requires the means to decrypt what it points at.
ALTER TABLE statement
    ADD CONSTRAINT ck_statement_available_has_key_material
        CHECK (status <> 'AVAILABLE' OR (wrapped_dek IS NOT NULL AND kek_id IS NOT NULL)) NOT VALID;

COMMENT ON COLUMN statement.wrapped_dek IS
    'The per-object data key, wrapped by the customer CEK. The plaintext DEK is never persisted anywhere.';
COMMENT ON COLUMN statement.kek_id IS
    'The cohort KEK alias in force when this object was written. Recorded per object so rotation does not rewrite history.';

-- ---------------------------------------------------------------------------------------------
-- PRIVILEGES. The point of this section is a REVOKE, not a GRANT.
--
-- V009 gave app_delivery table-level SELECT on customer_key, at a time when the table held nothing
-- but an identifier. It now holds wrapped key material, and a table-level grant covers every column
-- INCLUDING ONES ADDED AFTERWARDS - which is exactly how a privilege that was correct when written
-- becomes a hole later, silently, with no commit that looks wrong.
--
-- app_delivery is the catalogue and link-issue service. It lists statements and mints download
-- links. It never decrypts anything, so under least privilege it must not be able to read the
-- material that would let it - not even wrapped, because "wrapped is safe" is an argument that
-- depends on KMS staying out of reach, and defence in depth means not resting on that.
--
-- Column-level SELECT is granted back for everything except wrapped_cek, so the readiness checks
-- and the catalogue keep working.
-- ---------------------------------------------------------------------------------------------
REVOKE SELECT ON customer_key FROM app_delivery;

GRANT SELECT (customer_id, kek_id, status, created_at, rotated_at, destroyed_at, cohort_id, cek_algorithm)
    ON customer_key TO app_delivery;

-- The three roles that DO decrypt keep table-level SELECT, wrapped_cek included. Re-asserted here
-- rather than left implicit from V009, so this file states the whole access picture in one place.
GRANT SELECT ON customer_key TO app_download, app_generation, app_retention;

-- Generation mints a customer's CEK on first use. It is the only role that creates one: the
-- download path reads keys and the retention path destroys them, and neither has a reason to make
-- one appear. No UPDATE - a CEK is written once and changed only by rotation, which is retention's.
GRANT INSERT ON customer_key TO app_generation;

-- Nobody deletes. Re-asserted for the same reason V012 re-asserted the audit trail's: a script that
-- widens one privilege should restate the boundary it is not crossing.
REVOKE DELETE, TRUNCATE ON customer_key FROM PUBLIC;
REVOKE DELETE, TRUNCATE ON customer_key FROM app_delivery, app_download, app_generation, app_retention;
