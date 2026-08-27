# ADR-0012: Return 404, never 403, for resources the caller does not own
**Status:** Accepted   **Date:** 2026-08-27

## Context
Delivery.Api serves `/customers/{customerId}/statements`. The route `customerId` is untrusted input, compared against the JWT `sub` claim; the query runs against `sub` regardless, so ownership is enforced twice — once by the comparison, once by the `WHERE` clause.

403 Forbidden confirms the resource exists and belongs to someone else. That confirmation is precisely the oracle an enumeration attack needs: walk the identifier space, keep the 403s, and you hold valid customer and statement identifiers without reading a byte of anyone's data. Which identifiers exist is itself disclosure — customer counts, growth rates, whether a named person banks here. UUIDv7 keys (ADR-0006) are time-ordered: unguessability is not the control here.

## Options considered
| Option | Pros | Cons |
|---|---|---|
|403 for unowned|Honest HTTP semantics; trivial to support|Publishes an existence oracle; enumeration needs no data access|
|404 unowned, 403 for insufficient scope|Keeps scope failures distinct|No scope tier exists today; that 403 still confirms existence|
|404 for both (chosen)|No oracle; one query, one response|Less RESTful; four failure modes share one status|

## Decision
Return 404 for both, indistinguishably: same status, same `application/problem+json` body, no `detail`. Timing counts — a "not yours" costing a database round trip while "does not exist" returns instantly reintroduces the oracle as a side channel. The owner predicate sits in the `WHERE` clause, so both answers come from the same query.

401 stays distinct for an absent or invalid token: it leaks nothing about which resources exist.

The denial reason is recorded internally in `audit_event.denial_reason_code` (`SUBJECT_MISMATCH`, `NOT_OWNER`, `NOT_FOUND`, `NO_SUBJECT_CLAIM`) and never returned.

## Consequences
Debuggability suffers: "the customer cannot see their statement" now yields a 404 meaning one of four things. The answer exists — behind authentication and authorisation, for people entitled to it — in `denial_reason_code`.

It is less RESTful than 403, and a future reader will want to fix it. The endpoint carries a comment saying so, and `ListStatements_ForAnotherCustomer_Returns404NotForbidden` fails if they do.

## Revisit when
- p99 latency for the "not yours" path diverges from "does not exist" by more than 5 ms over a month of production histograms.
- Support closes more than 10 tickets a quarter only by hand-querying `denial_reason_code` — add a staff-only diagnostic endpoint instead.
- Delivery.Api gains a scope or role tier where the caller may legitimately know a resource exists; those denials can then be 403.
