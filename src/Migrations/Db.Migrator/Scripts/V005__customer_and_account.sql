-- =============================================================================================
-- V005  Customer and account.
--
-- Small, unpartitioned reference tables. At 100k customers and ~115k accounts these fit in memory
-- and stay there; partitioning them would add maintenance for no benefit.
--
-- NOTE ON DOLLAR QUOTING: bare $$ only, never a named tag. DbUp substitutes $name$ variables in
-- these scripts and would eat $body$ or $do$.
-- =============================================================================================

CREATE TABLE IF NOT EXISTS customer
(
    -- APPLICATION-SUPPLIED UUIDv7. There is deliberately NO DEFAULT gen_random_uuid() here, and
    -- adding one back "for convenience" is the exact regression this comment exists to prevent.
    -- Two reasons it matters:
    --   1. gen_random_uuid() is UUIDv4 - random, so every insert lands at a random point in the
    --      B-tree. See docs/adr/0006-uuidv7-primary-keys.md.
    --   2. A database-assigned key cannot be known before the INSERT, so an aggregate and its
    --      outbox message cannot share an identity inside one transaction without a round trip.
    id           UUID        NOT NULL,

    -- The identifier the upstream system of record uses. Unique, because two customer rows for one
    -- real customer would silently split their statement history in half.
    external_ref TEXT        NOT NULL,

    status       TEXT        NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT pk_customer PRIMARY KEY (id),
    CONSTRAINT uq_customer_external_ref UNIQUE (external_ref),
    CONSTRAINT ck_customer_status CHECK (status IN ('ACTIVE', 'DORMANT', 'CLOSED'))
);

COMMENT ON TABLE customer IS
    'Customers. The subject of every authorisation decision in the delivery path.';
COMMENT ON COLUMN customer.id IS
    'Application-supplied UUIDv7. No DEFAULT: the application always provides the value.';

ALTER TABLE customer OWNER TO app_migrator;

CREATE TABLE IF NOT EXISTS account
(
    -- Application-supplied UUIDv7. See the note on customer.id.
    id                    UUID        NOT NULL,
    customer_id           UUID        NOT NULL,

    -- MASKED, NEVER THE FULL NUMBER. The full account number is a payment credential in most
    -- contexts and has no use on the delivery path: everything here is keyed by account id, and
    -- the only consumer of this column is a display label the customer already knows.
    -- Storing the full number would put a credential in a table read by two internet-facing
    -- services in exchange for nothing.
    account_number_masked TEXT        NOT NULL,

    product_type          TEXT        NOT NULL,
    status                TEXT        NOT NULL,
    opened_at             TIMESTAMPTZ NOT NULL,
    closed_at             TIMESTAMPTZ,

    CONSTRAINT pk_account PRIMARY KEY (id),
    CONSTRAINT fk_account_customer FOREIGN KEY (customer_id) REFERENCES customer (id),
    CONSTRAINT ck_account_status CHECK (status IN ('ACTIVE', 'DORMANT', 'CLOSED')),
    CONSTRAINT ck_account_closed_after_opened CHECK (closed_at IS NULL OR closed_at >= opened_at)
);

COMMENT ON TABLE account IS
    'Accounts. A customer may hold several; a statement belongs to exactly one.';
COMMENT ON COLUMN account.account_number_masked IS
    'Masked display form only. The full account number is never stored in this system.';

ALTER TABLE account OWNER TO app_migrator;

-- PARTIAL on status = 'ACTIVE'. The listing path only ever asks for a customer's live accounts, so
-- the closed ones - which accumulate forever and are never queried this way - stay out of the
-- index entirely.
CREATE INDEX IF NOT EXISTS idx_account_customer
    ON account (customer_id)
    WHERE status = 'ACTIVE';
