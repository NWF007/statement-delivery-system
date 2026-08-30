-- =============================================================================================
-- V015  An AVAILABLE statement must carry the digest of its own plaintext.
--
-- WHAT THIS CLOSES. FramedDecryptingStream verifies content_sha256 only when it is GIVEN one, and
-- the column has been nullable since V006. The read path used to pass it through as
-- "envelope.ContentSha256 ?? default", so a NULL column silently skipped the check.
--
-- WHY THAT MATTERS MORE THAN IT LOOKS. The adversary this design names is one with WRITE ACCESS TO
-- THIS DATABASE (see docs/adr/0021-envelope-encryption-over-sse-kms.md). Per-frame authentication
-- already defeats their obvious moves - repointing a storage_key, editing a ciphertext byte,
-- dropping a frame. What it does NOT catch is an object replaced by an OLDER VERSION OF ITSELF:
-- every frame is genuine, every tag verifies, the AAD identity matches, because it really is that
-- statement - just last month's copy of it. The plaintext digest is the only check that catches it.
--
-- And under the old nullable-and-skip behaviour, the same attacker could disable that one check
-- from the same place they mounted the attack, by setting one column to NULL. A control the
-- attacker can switch off is not a control.
--
-- MIRRORS ck_statement_available_has_key_material from V013: an AVAILABLE statement must be
-- READABLE (it has key material) and must be VERIFIABLE (it has a digest). Forward-only, so this is
-- a new script rather than an edit to V006 or V013.
-- =============================================================================================

-- NOT VALID: enforced on every new and updated row immediately; the scan of existing rows is
-- V016's job, in its own transaction. See rule 4 of Scripts/README.md and the note in V013.
ALTER TABLE statement
    ADD CONSTRAINT ck_statement_available_has_digest
        CHECK (status <> 'AVAILABLE' OR content_sha256 IS NOT NULL) NOT VALID;

-- Exactly 32 bytes or nothing. A digest of the wrong length cannot have come from SHA-256, and
-- catching it here is cheaper than a FixedTimeEquals that fails at the end of a 200 MB download.
ALTER TABLE statement
    ADD CONSTRAINT ck_statement_content_sha256_length
        CHECK (content_sha256 IS NULL OR octet_length(content_sha256) = 32) NOT VALID;

COMMENT ON COLUMN statement.content_sha256 IS
    'SHA-256 of the PLAINTEXT, computed during encryption in the same pass and verified on every read. Required for AVAILABLE rows: a nullable digest is a verification an attacker can switch off.';
