-- =============================================================================================
-- V007  Tamper-evident audit trail.
--
-- ~470 million events a year. Every record's hash covers the previous record's hash, so removing
-- or altering one breaks every hash after it.
--
-- WHY SIXTEEN CHAINS AND NOT ONE. A hash chain needs strict ordering; ordering needs
-- serialisation; serialisation needs a lock. ONE global chain would put a single hot row in front
-- of every write path in the system, and at this volume that row IS the throughput ceiling.
-- Sixteen independent chains give sixteen independent locks. What is given up is total ordering
-- ACROSS chains; what is kept is the property that matters - no record can be deleted or altered
-- without detection. See docs/adr/0010-sharded-audit-hash-chains.md.
--
-- NOTE ON DOLLAR QUOTING: bare $$ only. DbUp would eat a named tag such as $body$.
-- =============================================================================================

CREATE TABLE IF NOT EXISTS audit_event
(
    -- Which chain this record belongs to. Assigned from the SUBJECT ENTITY (statement, else
    -- customer), never round-robin, so every event about one statement lands in one chain and
    -- verifying that statement's history is a single-chain walk.
    chain_id           SMALLINT    NOT NULL,

    -- Position within the chain. Strictly increasing, no gaps. A gap IS a deletion.
    chain_seq          BIGINT      NOT NULL,

    id                 UUID        NOT NULL,

    statement_id       UUID,
    customer_id        UUID,

    -- Unused until the download path lands. Present now because adding a column to a table holding
    -- 470 million rows a year is a rewrite, and adding one to an empty table is instant.
    token_id           UUID,

    actor_type         TEXT        NOT NULL,
    actor_id           TEXT,
    action             TEXT        NOT NULL,
    outcome            TEXT        NOT NULL,

    -- INTERNAL ONLY. Never returned to a caller. "Not yours" and "does not exist" are deliberately
    -- indistinguishable over the wire, and returning this column would undo that in one line.
    -- See docs/adr/0012-404-not-403-for-unowned-resources.md.
    denial_reason_code TEXT,

    source_ip          INET,

    -- A HASH of the user agent, never the value. The raw string is a fingerprinting vector and has
    -- no diagnostic power the hash lacks: comparing hashes still answers "same client as before?".
    user_agent_hash    TEXT,

    context            JSONB       NOT NULL DEFAULT '{}'::jsonb,
    occurred_at        TIMESTAMPTZ NOT NULL,

    -- The chain links. prev_hash is the preceding record's hash, or the chain's genesis for the
    -- first record.
    prev_hash          BYTEA       NOT NULL,
    hash               BYTEA       NOT NULL,

    -- A partitioned table's primary key must include the partition key, so this enforces
    -- uniqueness of (chain_id, chain_seq, occurred_at) rather than of (chain_id, chain_seq)
    -- globally. Duplicate sequence numbers are prevented by the writer instead: it takes
    -- FOR UPDATE on the single audit_chain_head row before allocating one, and that row is not
    -- partitioned.
    CONSTRAINT pk_audit_event PRIMARY KEY (chain_id, chain_seq, occurred_at),

    CONSTRAINT ck_audit_actor_type
        CHECK (actor_type IN ('CUSTOMER', 'SYSTEM', 'STAFF', 'ANONYMOUS')),
    CONSTRAINT ck_audit_outcome
        CHECK (outcome IN ('SUCCESS', 'DENIED', 'ERROR')),

    -- action is deliberately FREE TEXT with no CHECK constraint. New actions are something features
    -- add routinely; a constrained vocabulary would make every one of them a migration, and a
    -- migration in the same deploy as the code that writes the new value is a rollout ordering
    -- problem nobody needs.
    CONSTRAINT ck_audit_action_not_empty CHECK (length(action) BETWEEN 1 AND 128),
    CONSTRAINT ck_audit_chain_seq_positive CHECK (chain_seq >= 1),
    CONSTRAINT ck_audit_hash_length CHECK (length(hash) = 32 AND length(prev_hash) = 32)
)
PARTITION BY RANGE (occurred_at);

COMMENT ON TABLE audit_event IS
    'Append-only, hash-chained audit trail. Sharded across N chains for write throughput.';
COMMENT ON COLUMN audit_event.denial_reason_code IS
    'INTERNAL ONLY. Never returned to a caller: it would distinguish "not yours" from "not found".';
COMMENT ON COLUMN audit_event.user_agent_hash IS
    'Hash of the user agent, never the raw value.';

ALTER TABLE audit_event OWNER TO app_migrator;

-- "Everything that happened to this statement", newest first. The per-statement history is a
-- single-chain walk precisely because chain assignment is by entity.
CREATE INDEX IF NOT EXISTS idx_audit_statement
    ON audit_event (statement_id, occurred_at DESC);

-- "Everything this customer did", newest first. This is the query an investigation starts from.
CREATE INDEX IF NOT EXISTS idx_audit_customer
    ON audit_event (customer_id, occurred_at DESC);

-- The verifier walks one chain in sequence order. Without this it would sort 470 million rows.
CREATE INDEX IF NOT EXISTS idx_audit_chain_seq
    ON audit_event (chain_id, chain_seq);

-- Denials are the interesting data - an audit log recording only successes cannot detect
-- enumeration. PARTIAL, so the index holds only the refusals rather than every event ever written.
CREATE INDEX IF NOT EXISTS idx_audit_denials
    ON audit_event (occurred_at DESC, action)
    WHERE outcome = 'DENIED';

-- ---------------------------------------------------------------------------------------------
-- Chain heads. NOT partitioned: one row per chain, forever, and the row the writer locks.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS audit_chain_head
(
    chain_id   SMALLINT    NOT NULL,
    last_seq   BIGINT      NOT NULL,
    last_hash  BYTEA       NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT pk_audit_chain_head PRIMARY KEY (chain_id),
    CONSTRAINT ck_audit_chain_head_seq CHECK (last_seq >= 0),
    CONSTRAINT ck_audit_chain_head_hash CHECK (length(last_hash) = 32)
);

COMMENT ON TABLE audit_chain_head IS
    'Terminal hash per chain. Locked FOR UPDATE during append; this is the serialisation point.';

ALTER TABLE audit_chain_head OWNER TO app_migrator;

-- ---------------------------------------------------------------------------------------------
-- APPEND-ONLY ENFORCEMENT. Both mechanisms, and they are INDEPENDENT.
--
-- The trigger catches an application bug: a stray UPDATE in code that had every right to be
-- connected to the database. The revoked grant catches a COMPROMISED APPLICATION USER: someone
-- holding app_delivery's credentials who wants to erase their tracks.
--
-- An attacker must defeat BOTH, and defeating the grant requires a separate credential - one that
-- no running service ever holds. Either mechanism alone leaves a gap the other covers.
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION deny_mutation()
RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS
$$
BEGIN
    RAISE EXCEPTION 'audit_event is append-only (attempted %)', TG_OP
        USING ERRCODE = 'restrict_violation';
END;
$$;

ALTER FUNCTION deny_mutation() OWNER TO app_migrator;

DROP TRIGGER IF EXISTS trg_audit_no_update ON audit_event;
CREATE TRIGGER trg_audit_no_update
    BEFORE UPDATE ON audit_event
    FOR EACH ROW EXECUTE FUNCTION deny_mutation();

DROP TRIGGER IF EXISTS trg_audit_no_delete ON audit_event;
CREATE TRIGGER trg_audit_no_delete
    BEFORE DELETE ON audit_event
    FOR EACH ROW EXECUTE FUNCTION deny_mutation();

-- TRUNCATE bypasses row triggers entirely, so it needs its own statement-level trigger. Without
-- this, "append-only" is one TRUNCATE away from an empty table.
DROP TRIGGER IF EXISTS trg_audit_no_truncate ON audit_event;
CREATE TRIGGER trg_audit_no_truncate
    BEFORE TRUNCATE ON audit_event
    FOR EACH STATEMENT EXECUTE FUNCTION deny_mutation();

REVOKE UPDATE, DELETE, TRUNCATE ON audit_event FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------
-- Initial partitions: current month plus three either side. An INSERT with no matching partition
-- FAILS, and an audit insert that fails rolls back the business operation it was recording.
-- ---------------------------------------------------------------------------------------------
SELECT ensure_range_partitions('audit_event'::regclass, 'month', 6, now() - interval '3 months');

-- ---------------------------------------------------------------------------------------------
-- GENESIS. Seed all sixteen chain heads.
--
-- genesis = SHA256(UTF8('statement-delivery:audit:chain:' || chain_id))
--
-- NOT ZEROS, and the difference is the whole point. With a shared genesis, record 1 of chain 3 and
-- record 1 of chain 7 both hash over the same predecessor - so a record can be lifted out of one
-- chain and replayed into another and BOTH chains still verify. A chain-specific genesis makes the
-- hashes fail to line up.
--
-- Computed in SQL so the seed cannot drift from the C# definition by being maintained twice; an
-- integration test asserts the two agree byte for byte.
--
-- Raising the configured chain count REQUIRES A NEW MIGRATION to seed the additional heads. The
-- audit writer fails loudly on a missing head rather than inventing one, because inventing a
-- genesis would silently start a second, unverifiable chain.
-- ---------------------------------------------------------------------------------------------
INSERT INTO audit_chain_head (chain_id, last_seq, last_hash, updated_at)
SELECT
    chain_id::smallint,
    0,
    sha256(convert_to('statement-delivery:audit:chain:' || chain_id::text, 'UTF8')),
    now()
FROM generate_series(0, 15) AS chain_id
ON CONFLICT (chain_id) DO NOTHING;
