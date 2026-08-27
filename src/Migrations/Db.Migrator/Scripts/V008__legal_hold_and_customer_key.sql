-- =============================================================================================
-- V008  Legal hold and customer key.
--
-- CREATED NOW, UNUSED UNTIL THE RETENTION WORK LANDS. They are here because adding them alongside
-- the rest of the schema costs one migration, whereas adding them later costs another - and
-- because the retention worker's readiness check should be able to see them from the day it exists
-- rather than reporting healthy while the tables it depends on are absent.
--
-- Neither table is partitioned. Legal holds are rare by construction, and there is exactly one key
-- row per customer.
-- =============================================================================================

CREATE TABLE IF NOT EXISTS legal_hold
(
    -- Application-supplied UUIDv7. No DEFAULT, deliberately - see V005.
    id             UUID        NOT NULL,

    -- EXACTLY ONE of these is set. A hold is scoped either to one statement or to everything
    -- belonging to one customer; a hold scoped to both is ambiguous about what it protects, and a
    -- hold scoped to neither protects nothing while still blocking the purge that trips over it.
    statement_id   UUID,
    customer_id    UUID,

    -- The matter this hold belongs to. Required: a hold nobody can trace to a case is a hold
    -- nobody will ever dare release, and holds that are never released defeat retention entirely.
    case_reference TEXT        NOT NULL,

    placed_by      TEXT        NOT NULL,
    placed_at      TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- NULL means active. Releasing sets the timestamp rather than deleting the row: the record
    -- that data was preserved, by whom, and for how long, is itself evidence.
    released_at    TIMESTAMPTZ,
    released_by    TEXT,

    CONSTRAINT pk_legal_hold PRIMARY KEY (id),
    CONSTRAINT ck_legal_hold_scope CHECK (num_nonnulls(statement_id, customer_id) = 1),
    CONSTRAINT ck_legal_hold_case_reference CHECK (length(case_reference) BETWEEN 1 AND 256),
    CONSTRAINT ck_legal_hold_release_consistent
        CHECK ((released_at IS NULL AND released_by IS NULL)
            OR (released_at IS NOT NULL AND released_by IS NOT NULL)),
    CONSTRAINT ck_legal_hold_released_after_placed
        CHECK (released_at IS NULL OR released_at >= placed_at)
);

COMMENT ON TABLE legal_hold IS
    'Suspends retention purge for one statement or one customer. Released, never deleted.';

ALTER TABLE legal_hold OWNER TO app_migrator;

-- PARTIAL on active holds. The purge path asks "is anything holding this?" on every candidate, and
-- released holds accumulate forever without ever being the answer.
CREATE INDEX IF NOT EXISTS idx_legal_hold_active_statement
    ON legal_hold (statement_id)
    WHERE released_at IS NULL AND statement_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_legal_hold_active_customer
    ON legal_hold (customer_id)
    WHERE released_at IS NULL AND customer_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_legal_hold_case_reference
    ON legal_hold (case_reference);

-- ---------------------------------------------------------------------------------------------
-- Per-customer key encryption key.
--
-- One KEK per customer is what makes erasure feasible at this scale. Destroying the key
-- CRYPTO-ERASES every statement for that customer at once, without rewriting or deleting a single
-- object - and object storage under a seven-year Object Lock cannot be deleted anyway.
--
-- The KEK itself is NEVER in this table. Only its identifier lives here; the key material stays in
-- the key management service. A table that held key material would make the database a single
-- point of compromise for every statement in the system.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS customer_key
(
    customer_id  UUID        NOT NULL,

    -- Reference into the key management service. An identifier, never key material.
    kek_id       TEXT        NOT NULL,

    status       TEXT        NOT NULL DEFAULT 'ACTIVE',
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    rotated_at   TIMESTAMPTZ,

    -- Once set, every statement encrypted under this key is permanently unreadable. The row
    -- survives as the record that the erasure happened.
    destroyed_at TIMESTAMPTZ,

    CONSTRAINT pk_customer_key PRIMARY KEY (customer_id),
    CONSTRAINT fk_customer_key_customer FOREIGN KEY (customer_id) REFERENCES customer (id),
    CONSTRAINT ck_customer_key_status CHECK (status IN ('ACTIVE', 'ROTATING', 'DESTROYED')),

    -- destroyed_at and the DESTROYED status must agree. A key marked destroyed with no timestamp
    -- cannot be evidenced; a timestamp with an ACTIVE status invites code to keep using it.
    CONSTRAINT ck_customer_key_destroyed_consistent
        CHECK ((status = 'DESTROYED') = (destroyed_at IS NOT NULL))
);

COMMENT ON TABLE customer_key IS
    'Per-customer KEK reference. Destroying the key crypto-erases that customer''s statements.';
COMMENT ON COLUMN customer_key.kek_id IS
    'Identifier only. Key material never enters this database.';

ALTER TABLE customer_key OWNER TO app_migrator;

CREATE INDEX IF NOT EXISTS idx_customer_key_active
    ON customer_key (customer_id)
    WHERE status = 'ACTIVE';
