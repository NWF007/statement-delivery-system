# ADR-0013: Require a bounded date range on every statement query
**Status:** Accepted   **Date:** 2026-08-27

## Context
`statement` is RANGE-partitioned monthly on `period_start` (ADR-0007), and pruning needs a filter on that key. The hottest query — this customer's statements — filters `customer_id` (ADR-0009). Without a date predicate the planner keeps all 84 partitions, and the scan that looks instant on six months of dev data becomes 84 index scans.

Worse, the key `(id, period_start)` means a lookup by `id` alone cannot tell which partition holds the row, and visits all 84 to return one.

## Options considered
| Option | Pros | Cons |
| --- | --- | --- |
| Optional range, server-side default | No client changes | The bound is invisible; `from=1900-01-01` silently restores the 84-partition scan |
| Mandatory bounded range | Pruning guaranteed by contract; the cost is visible in the schema | No "give me everything" call |
| No range, global `statement_timeout` | Zero API surface | Turns a planning defect into cancelled queries; a timeout is an outage |

## Decision
Make the bound part of the contract, not an optimisation the caller may skip.

`GET /v1/customers/{id}/statements` requires `from` and `to`; `GET /v1/statements/{id}` requires `?period=`. Missing either is a 400 before any query runs. Clients default to 24 months; spans over 84 are rejected — that is full retention, so a wider range returns no more statements, only scans more partitions.

Bounding a listing endpoint is ordinary API design, not a leaky abstraction, and beats one that times out only in production. The OpenAPI description says why.

## Consequences
- Nobody can ask for everything. A client needing full history pages through ranges — correct, because that is not one query.
- Pagination is keyset, never OFFSET, and the cursor carries `period_start` beside `id`, so page two prunes like page one.
- Download.Gateway tokens must encode `period_start`, or the unauthenticated lookup scans all 84.
- An integration test asserts via `EXPLAIN` that the bounded plan touches strictly fewer partitions than the unbounded one; make the range optional and the build goes red, not the p99.

## Revisit when
- PostgreSQL ships a global index spanning all `statement` partitions on `customer_id`.
- Missing-range 400s exceed 2% of Delivery.Api responses over a rolling 7 days.
- Retention stops being 7 years, leaving the 84-month cap out of step.
