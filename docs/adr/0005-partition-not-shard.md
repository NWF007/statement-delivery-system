# ADR-0005: Partition PostgreSQL by range, do not shard
**Status:** Accepted   **Date:** 2026-08-26

## Context
The numbers:

```
statements / month              30,000,000
statements / year              360,000,000
@ 7-year retention           2,520,000,000
rows ~800 GB + indexes ~600 GB   = ~1.4 TB
audit events / year  470,000,000 (~190 GB/yr)
live download tokens             2,000,000
peak write  1,400 inserts/sec (month-end burst)
peak read   ~30 req/sec (delivery path)
```

That is a large table, not a large database. One NVMe primary absorbs 1,400 inserts/sec (~4 MB/sec of WAL); 30 req/sec of indexed lookups is noise. The working set is the trailing 90 days (~90M rows); 96% is cold.

Unpartitioned, maintenance breaks first: autovacuum, REINDEX and the 7-year purge become table-wide work over 1.4 TB. Partitioning by month turns purge into DETACH + DROP, keeps B-trees per partition, and prunes 96% of rows per query.

The real pressure is connection count and read fan-out: 400 Generation.Worker replicas against a primary sized for ~200 backends. That is PgBouncer (ADR-0008) and read/write splitting, not sharding.

## Options considered
| Option | Pros | Cons |
| --- | --- | --- |
| Single unpartitioned table | Simplest schema; no DDL automation | Purge rewrites 1.4 TB; unbounded vacuum; no pruning |
| Monthly range partitioning, one primary | O(1) purge; per-partition indexes; pruning; stock PostgreSQL | Partition DDL job; queries must carry the key |
| Horizontal sharding (Citus) | Headroom far beyond forecast | Cross-shard joins, distributed transactions, rebalancing — for a non-problem |

## Decision
Range-partition statements and audit events by month on the period column. Designate `customer_id` the shard key now — every hot query already filters on it — and ship no sharding infrastructure. Partition now, shard never, unless the numbers below move.

## Consequences
- Catalogue queries must carry a period range; the API enforces this.
- Partition DDL is scheduled; no DEFAULT partition, so a gap fails loudly rather than collecting rows silently.
- ~168 partitions by year seven — keep counts in hundreds, not thousands.
- One primary is one failure domain; HA is replication and failover, not shard redundancy.

## Revisit when
- Sustained write rate exceeds 7,000 inserts/sec, or the burst window exceeds 12 hours.
- Hot working set outgrows primary RAM — buffer cache hit ratio sustained below 99%.
- Any single partition's monthly VACUUM/REINDEX exceeds the 2-hour maintenance window.
- A data-residency mandate forces a jurisdiction's statements onto separate infrastructure.
