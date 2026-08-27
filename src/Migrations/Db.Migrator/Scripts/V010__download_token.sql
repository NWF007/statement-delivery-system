-- =============================================================================================
-- V010  Download tokens. The security core of the system.
--
-- A token is a BEARER CREDENTIAL: whoever holds the URL can download the statement. Everything
-- about this table exists to bound the damage that follows from that:
--   * only the SHA-256 is stored, never the plaintext
--   * the lifetime is capped by the SCHEMA, not just by application code
--   * a redemption is a single atomic UPDATE, so it cannot happen twice
--   * revocation is a column, so a link can be killed instantly
--
-- Partitioned by expires_at, DAILY. See the note on cleanup below - it is the reason.
--
-- NOTE ON DOLLAR QUOTING: bare $$ only. DbUp would eat a named tag such as $body$.
-- =============================================================================================

CREATE TABLE IF NOT EXISTS download_token
(
    -- The LINK identifier. Safe to return to the customer and safe to log: it is not the
    -- credential. It exists so a link can be revoked by name without anybody holding the secret.
    -- Application-supplied UUIDv7; no DEFAULT gen_random_uuid(), deliberately - see V005.
    id                  UUID        NOT NULL,

    statement_id        UUID        NOT NULL,

    -- THE STATEMENT PARTITION KEY, DENORMALISED ONTO THE TOKEN.
    --
    -- Not in the original sketch, and it is load-bearing. `statement` is RANGE-partitioned monthly
    -- on period_start with a primary key of (id, period_start), so a lookup by statement_id alone
    -- cannot prune and must visit EVERY monthly partition - eighty-four at full retention.
    --
    -- Redemption is the hottest, most latency-sensitive path in the system and it needs the
    -- statement immediately after consuming the token. Without this column that lookup would be a
    -- full partition fan-out on every single download, which is exactly what
    -- docs/adr/0013-mandatory-date-range-on-statement-queries.md exists to forbid.
    --
    -- The value costs nothing at issue time: the issue endpoint already requires ?period= for the
    -- same reason. The atomic consume returns it, so the statement fetch that follows is a pruned
    -- point lookup.
    statement_period    DATE        NOT NULL,

    -- BINDING. A token is useless for any other customer. Without this a leaked token would be a
    -- capability against whatever statement it names with no owner to reason about; with it, every
    -- redemption is attributable and a stolen token is worthless outside its own account.
    customer_id         UUID        NOT NULL,

    -- THE HASH, NEVER THE PLAINTEXT.
    --
    -- SHA-256, not bcrypt or argon2, and that is correct rather than lazy. Slow password hashes
    -- exist to resist brute force against LOW-ENTROPY secrets. This is 256 bits of CSPRNG output:
    -- there is no brute-force exposure to resist, so slowness would buy nothing and would cost
    -- latency on every redemption. Salting is pointless for the same reason - nobody precomputes
    -- dictionaries of random 32-byte values.
    token_sha256        BYTEA       NOT NULL,

    issued_at           TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- The RANGE partition key.
    expires_at          TIMESTAMPTZ NOT NULL,

    -- NULL until redeemed. The atomic consume sets this and only succeeds while it is NULL.
    consumed_at         TIMESTAMPTZ,

    revoked_at          TIMESTAMPTZ,
    revoked_reason      TEXT,

    single_use          BOOLEAN     NOT NULL DEFAULT TRUE,

    -- Forensics. Who asked for the link, and who actually followed it. A mismatch between the two
    -- is the signal that a link was forwarded or intercepted.
    issued_to_ip        INET,
    consumed_by_ip      INET,
    consumed_by_ua_hash TEXT,

    CONSTRAINT pk_download_token PRIMARY KEY (id, expires_at),

    -- -----------------------------------------------------------------------------------------
    -- THE SECURITY POLICY, ENFORCED BY THE SCHEMA.
    --
    -- Even with an application bug - a mis-parsed TTL, a unit confusion between seconds and
    -- minutes, a config value nobody validated - the DATABASE refuses to store a token that lives
    -- longer than an hour. Defence in depth at the data layer: the ceiling holds even when the code
    -- that was supposed to enforce it does not.
    --
    -- Both halves matter. Without the lower bound a token could be born already expired; without
    -- the upper bound a bug could mint a credential that outlives the session that asked for it.
    -- -----------------------------------------------------------------------------------------
    CONSTRAINT ck_token_ttl
        CHECK (expires_at > issued_at
               AND expires_at <= issued_at + INTERVAL '1 hour'),

    -- A short or truncated hash would silently weaken matching. SHA-256 is exactly 32 bytes.
    CONSTRAINT ck_token_hash_length
        CHECK (octet_length(token_sha256) = 32),

    -- A revoked token must say why. "Revoked by whom and for what" is the question asked after an
    -- incident, and a NULL there is an answer nobody can reconstruct later.
    CONSTRAINT ck_token_revocation_consistent
        CHECK ((revoked_at IS NULL AND revoked_reason IS NULL)
            OR (revoked_at IS NOT NULL AND revoked_reason IS NOT NULL))
)
PARTITION BY RANGE (expires_at);

COMMENT ON TABLE download_token IS
    'Single-use, short-lived, customer-bound download capabilities. Stores hashes only; daily partitions are DROPPED, never DELETEd.';
COMMENT ON COLUMN download_token.token_sha256 IS
    'SHA-256 of the 32-byte plaintext. The plaintext exists in exactly one place: the HTTP response body of the issue request.';
COMMENT ON COLUMN download_token.id IS
    'Link identifier - NOT the credential. Safe to return and to log.';

ALTER TABLE download_token OWNER TO app_migrator;

-- ---------------------------------------------------------------------------------------------
-- THE REDEMPTION LOOKUP. Unique, so a hash collision or a duplicated insert is a constraint
-- violation rather than an ambiguous match that silently redeems the wrong row.
--
-- expires_at is in the index because a partitioned table's unique index MUST include the partition
-- key. That has a consequence worth knowing: uniqueness is enforced per partition, not globally.
-- Two tokens with the same hash could coexist in different days - which is fine, because the
-- probability of a SHA-256 collision on CSPRNG input is not a risk anybody manages.
-- ---------------------------------------------------------------------------------------------
CREATE UNIQUE INDEX IF NOT EXISTS idx_token_hash
    ON download_token (token_sha256, expires_at);

-- "Which links were issued for this statement, newest first" - the query an investigation starts
-- from when a customer reports a statement they did not download.
CREATE INDEX IF NOT EXISTS idx_token_statement
    ON download_token (statement_id, issued_at DESC);

-- "Which links has this customer been issued" - for support, and for spotting a session that has
-- started generating links in bulk.
CREATE INDEX IF NOT EXISTS idx_token_customer
    ON download_token (customer_id, issued_at DESC);

-- ---------------------------------------------------------------------------------------------
-- DAILY PARTITIONS EXIST SO THAT CLEANUP IS `DROP TABLE`, NOT `DELETE`.
--
-- Around two million tokens are live at any moment and every one of them expires within the hour,
-- so the churn is enormous relative to the resident set. `DELETE FROM download_token WHERE
-- expires_at < now()` at that rate produces dead tuples faster than autovacuum reclaims them:
-- table bloat, index bloat, WAL churn, and vacuum competing for I/O with the very traffic that
-- created the rows.
--
-- `DROP TABLE download_token_2026_08_27` is a catalogue update. It is instantaneous, generates
-- almost no WAL, and reclaims the space immediately. Cleanup stops being an operational hazard and
-- becomes a no-op.
--
-- Seven days ahead, matching Partitioning:PeriodsAhead. A missing partition means EVERY link issue
-- fails, so the partitions-ready health check treats one as a readiness failure rather than a
-- warning.
-- ---------------------------------------------------------------------------------------------
SELECT ensure_range_partitions('download_token'::regclass, 'day', 8, now() - interval '1 day');
