# ADR-0040: Every legal_hold row carries its customer_id

**Status:** Accepted · **Date:** 2026-08-31

## Context

V008 modelled hold scope as "exactly one of statement_id / customer_id is set". Under that
model, the erasure gate's question — *does any hold affect this customer?* — was answered with
`WHERE customer_id = @customerId`, which statement-scoped holds (customer_id NULL) never match.
The Prompt 6 audit's CRITICAL: a litigation hold on one statement did not block the erasure
that would destroy that statement's readability. The engine was right; the data model made the
right query impossible to write simply, so the simple query was wrong.

## Decision

**Denormalise: every hold row carries `customer_id`, always** (V021 backfills and enforces NOT
NULL). Scope is expressed by `statement_id` alone — NULL means customer-scoped, set means
statement-scoped. One indexed predicate now finds every hold affecting a customer, whatever its
scope, and the defect is unrepresentable rather than merely patched.

Why not an `OR EXISTS` against `statement`? Three reasons. The subquery by `customer_id` alone
cannot prune partitions and misses the partial index (`WHERE status='AVAILABLE'`, and a held
statement may be ARCHIVED). It fixes ONE query while every future hold query starts with the
same blindness. And the codebase already settled this argument: ADR-0009 denormalised
`customer_id` onto `statement` because a security-critical lookup should be one indexed column,
not a join. A hold lookup gating irreversible destruction is exactly that case.

The denormalisation has one hazard of its own, caught and pinned in the same change: the
per-statement gate's customer branch must now require `statement_id IS NULL`, or a
statement-scoped hold would block purging its *siblings* — over-protection that silently
repeals retention for the whole customer (`Purge_NotBlockedBy_SiblingStatementHold`).

## Consequences

- `ActiveCaseReferenceForCustomerAsync` became correct without changing: the data changed.
- The cost is the standard denormalisation cost — a hold's customer can never be re-parented
  without touching the row — and holds are rare, short-lived rows.
- Defence in depth behind it: the decision engine now blocks an erased-under-active-hold
  combination outright and `retention_erased_under_hold_total` pages if that state is ever
  observed (remediation A4).

## Revisit when

- **Account-scoped holds arrive** (a hold on one account's statements): the same rule applies —
  the row carries every id a gate will ever filter by, and scope is expressed by the narrowest
  one.
