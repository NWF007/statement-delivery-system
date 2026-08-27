# ADR-0009: Denormalise customer_id onto the statement table
**Status:** Accepted   **Date:** 2026-08-27

## Context
A statement's owner is derivable: statement -> account -> customer. Delivery.Api must answer "does this caller own this row?" on every request. Resolving that by join puts a multi-table plan over a 2.52-billion-row partitioned table (ADR-0007) on the hot path of a security decision.

## Options considered
| Option | Pros | Cons |
| --- | --- | --- |
| Join through account per request | Fully normalised; an account move is one UPDATE | Authorisation depends on a join plan over 2.52B rows; ownership is a result, not a filter |
| Materialised authorisation view | Keeps statement narrow; owner still indexable | A second 2.52B-row structure to index, refresh and reconcile; staleness is a breach window |
| Denormalise customer_id onto statement | Ownership is one equality predicate; duplication is bounded | Duplicated fact; account moves need a backfill; ~16 bytes/row |

## Decision
Carry customer_id on statement. The check is a single-column predicate served by the partial index `idx_statement_customer_period (customer_id, period_start DESC, id DESC) WHERE status = 'AVAILABLE'`.

Shape matters more than speed here. The repository signature is `FindAsync(StatementId, DateOnly periodStart, CustomerId owner)`: the owner goes into the query. No path loads a row then compares identifiers, because such a path is one forgotten `if` from a data breach and the forgotten version looks identical in review. A predicate cannot be forgotten; omit the owner and it does not compile.

The cost, stated honestly: moving an account between customers backfills tens of thousands of rows across dozens of partitions. That is rare, batchable and offline. Authorisation is none of those. The width costs roughly 40 GB against 800 GB of rows plus 600 GB of indexes.

## Consequences
- The backfill must commit in the same transaction as the account move, or the two disagree silently.
- Retention.Worker's periodic sweep gains an integrity check comparing statement.customer_id against account.customer_id; mismatches page.
- No foreign key from statement to account or customer. Enforcement costs a lookup per row at the 1,400 inserts/sec month-end burst, and the column is authoritative for authorisation regardless.

## Revisit when
- Account-to-customer moves exceed 50/month, or one backfill exceeds 15 minutes.
- p99 for the index lookup exceeds 25 ms at 30 req/sec peak delivery load.
- Any sweep reports a statement.customer_id/account.customer_id mismatch in production.
