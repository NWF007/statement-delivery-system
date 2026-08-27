# ADR-0007: Range-partition on time and require bounded date ranges in the API
**Status:** Accepted   **Date:** 2026-08-26

## Context
Retrofitting partitions onto a 2.5-billion-row table is a multi-day migration; the scheme is fixed while the tables are empty.

| Table | Partition key | Granularity | Retention mechanism |
|---|---|---|---|
| statement | period_start (RANGE) | monthly | drop after retention expiry |
| audit_event | occurred_at (RANGE) | monthly | archive, then drop |
| download_token | expires_at (RANGE) | daily | drop daily; never DELETE |
| outbox | created_at (RANGE) | daily | drop after publish + grace |

Tokens are transient: at 2 million live rows `DELETE FROM download_token WHERE expires_at < now()` means bloat, WAL churn and vacuum pressure, while `DROP TABLE download_token_2026_08_26` is instant and writes almost no WAL. Cleanup becomes a no-op, not a hazard.

## Options considered
| Option | Pros | Cons |
|---|---|---|
| No partitioning | Simplest schema | Retention is a bulk DELETE; vacuum storms |
| Hash on customer_id | Hottest query prunes unaided | Retention becomes DELETE on every partition |
| Range on time (chosen) | Retention is metadata-only DROP | Prunes only with a date predicate |

## Decision
Range-partition on time. Pruning needs a predicate on the partition key, yet the hottest query, this customer's statements, is keyed on `customer_id`, not `period_start`: without one, every partition is scanned.

The API therefore mandates a bounded range: default 24 months, hard maximum 84, HTTP 400 otherwise. Bounding the query surface beats an endpoint that works in dev and times out in production. An integration test asserts `EXPLAIN` prunes, so deleting the constraint turns the build red.

## Consequences
A missing partition is an outage, not a warning: an insert with no matching range fails outright. `PartitionMaintenanceService` pre-creates partitions N periods ahead (3 months monthly, 7 days daily) under a distributed lease; a partitions-ready health check fails readiness if the partition covering `now() + 1 period` is missing; `partition_missing_total` feeds alerting. `pg_partman` may replace the mechanism; the check stays, verifying the outcome rather than trusting the mechanism.

## Revisit when
- A monthly `statement` partition exceeds 300 million rows or 100 GB.
- Over 1% of catalogue reads are rejected for a missing or over-84-month range.
- `partition_missing_total` is non-zero in production even once.
- PostgreSQL prunes on non-key columns, or `customer_id` sub-partitioning benchmarks faster.
