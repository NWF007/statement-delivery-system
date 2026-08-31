-- =============================================================================================
-- V021  Every legal_hold row carries its customer_id, always.
--
-- THE DEFECT THIS REMOVES (the Prompt 6 audit's CRITICAL): erasure asked "does this customer
-- have an active hold?" with `WHERE customer_id = @customerId` - and a STATEMENT-scoped hold
-- leaves customer_id NULL under V008's exactly-one-scope-column model, so it was invisible to
-- the one query gating the only irreversible operation in the system. A hold on one statement
-- did not block the erasure that would destroy that statement's readability.
--
-- The fix is the data model, not the query. An `OR EXISTS` against statement by customer_id
-- cannot prune partitions, does not match the partial index, and - decisively - patches ONE
-- query while every future hold query starts with the same blindness. Denormalising customer_id
-- onto every hold row makes the defect unrepresentable: a single indexed predicate finds every
-- hold affecting a customer regardless of scope. This mirrors ADR-0009's precedent (customer_id
-- denormalised onto statement for the authorisation hot path). See ADR-0040.
--
-- Scope is now expressed by statement_id ALONE:
--   statement_id IS NULL      -> customer-scoped (all statements, present and future)
--   statement_id IS NOT NULL  -> statement-scoped (that statement only)
-- =============================================================================================

-- Backfill customer_id for statement-scoped holds.
-- Note: joins statement by id alone, so this scans partitions. Acceptable as a one-off against
-- a small table; do not repeat this shape at runtime.
UPDATE legal_hold h
SET    customer_id = s.customer_id
FROM   statement s
WHERE  h.statement_id IS NOT NULL
  AND  h.customer_id IS NULL
  AND  s.id = h.statement_id;

-- Any row that could not be backfilled is a data-integrity problem, and a hold row whose
-- customer cannot be resolved must stop the migration rather than silently losing protection.
DO $$
DECLARE orphans INT;
BEGIN
    SELECT count(*) INTO orphans FROM legal_hold WHERE customer_id IS NULL;
    IF orphans > 0 THEN
        RAISE EXCEPTION 'V021: % legal_hold rows have no resolvable customer', orphans;
    END IF;
END $$;

-- legal_hold is small by construction (holds are rare), so SET NOT NULL's full-table scan under
-- ACCESS EXCLUSIVE is momentary - the NOT VALID / VALIDATE split is for the billion-row tables.
ALTER TABLE legal_hold ALTER COLUMN customer_id SET NOT NULL;

-- V008's exactly-one-scope-column rule is retired: both columns are now set on statement-scoped
-- rows. (The brief's sketch names this constraint ck_hold_target; V008 named it
-- ck_legal_hold_scope - dropping the name that actually exists.)
ALTER TABLE legal_hold DROP CONSTRAINT IF EXISTS ck_legal_hold_scope;

COMMENT ON COLUMN legal_hold.customer_id IS
    'Always populated. Denormalised for statement-scoped holds so that a single indexed predicate finds every hold affecting a customer. See ADR-0040.';
COMMENT ON COLUMN legal_hold.statement_id IS
    'NULL = customer-scoped (applies to all statements, present and future). NOT NULL = statement-scoped.';

-- V008's idx_legal_hold_active_customer already serves the always-populated column:
--   ON legal_hold (customer_id) WHERE released_at IS NULL AND customer_id IS NOT NULL
-- The second predicate is now vacuously true and harmless; the index matches every
-- released_at IS NULL AND customer_id = @x lookup unchanged.
