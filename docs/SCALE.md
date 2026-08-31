# Scale

> **Status: heading skeleton.** Only the target table is filled in; everything below it is a placeholder for measured output. Every number added here must be reproducible by the commands shown in the same section.

## Target scale

Design requirements, not measurements.

| Dimension | Target |
| --- | --- |
| Statements ingested | 30M / month |
| Statements ingested | 360M / year |
| Retained statement rows (7-year retention) | 2.52B |
| Statement row storage | ~800 GB |
| Statement index storage | ~600 GB |
| Audit events | ~470M / year (~190 GB / year) |
| Live download tokens | ~2M |
| Month-end write burst | 1,400 inserts / sec |
| Peak delivery reads | ~30 req / sec |

## Seeding a representative dataset

```bash
Postgres__PrimaryConnectionString='Host=localhost;Port=6432;Database=statements_generation;Username=app_generation;Password=...' dotnet run --project tools/seed -- --customers 100000 --months 24 --seed 42
```

*Produces ~100k customers, ~115k accounts and ~2.4M statements through Npgsql binary COPY,
pre-creating every monthly partition it needs and running `ANALYZE` at the end. Deterministic from
`--seed`, so two runs produce identical data and their timings can be compared. Distribution is
deliberately uneven: ~85% of customers hold one account, ~2% hold three, ~8% are dormant with gaps
in their history, and ~1.5% of statements carry a regenerated `version = 2` row alongside the
original.*

| Measurement | Value |
| --- | --- |
| Customers written | *pending* |
| Accounts written | *pending* |
| Statements written | *pending* |
| Statement rows/sec (binary COPY) | *pending* |
| Total elapsed | *pending* |
| Monthly partitions created | *pending* |

> **Not yet measured.** The authoring environment has no usable Docker daemon (no WSL, Hyper-V not
> installed, nested-virt guest), so no figure above has been observed. Run the command on a
> Docker-capable host and paste the tool's own output — it reports rows/sec per table.

## Partition pruning proof

*The `EXPLAIN (ANALYZE, BUFFERS)` output for the customer list query goes here verbatim. It must
show the plan touching only the partitions covered by the date range.*

The query under test is the hot path — "my statements, newest first":

```sql
EXPLAIN (ANALYZE, BUFFERS)
SELECT id, account_id, customer_id, period_start, period_end, version, status,
       storage_key, storage_tier, size_bytes, retain_until, generated_at, purged_at
  FROM statement
 WHERE customer_id = '<pick one>'
   AND status = 'AVAILABLE'
   AND period_start >= '2026-01-01'
   AND period_start <  '2026-09-01'
 ORDER BY period_start DESC, id DESC
 LIMIT 51;
```

Three things the plan must show:

1. **Only eight monthly partitions scanned**, not all of them. That is the whole point of the
   mandatory date range — see [ADR-0013](adr/0013-mandatory-date-range-on-statement-queries.md).
2. **`idx_statement_customer_period` used**, not a sequential scan.
3. **No `Sort` node.** The index is `(customer_id, period_start DESC, id DESC)`, which matches the
   `ORDER BY` exactly. A `Sort` in the plan means the index and the sort have drifted apart.

```text
pending -- paste EXPLAIN (ANALYZE, BUFFERS) output here
```

*`IntegrationTests.StatementQueryIntegrationTests.WithDateRange_PrunesPartitions` asserts the
bounded query touches strictly fewer partitions than the unbounded one, and writes the plan to the
test trace output, so this block can be pasted rather than retyped.*

## Index sizes at seeded volume

*Fill from `pg_total_relation_size` and `pg_indexes_size` at the seeded volume, then extrapolate linearly to 2.52B rows in a second table.*

| Table | Rows | Heap | Index | Total |
| --- | --- | --- | --- | --- |
|  |  |  |  |  |

*State the extrapolation assumption explicitly here (bytes per row and index bytes per row held constant from the seeded sample), and say why it is or is not safe at 2.52B rows.*

## Connection budget

*Paste PgBouncer `SHOW POOLS` output under load, then the per-service arithmetic: pool size x instances, summed, against server `max_connections`.*

## Load test results

*Fill from the k6 summary output. Name the script, the target environment, and the run duration alongside the table.*

| Scenario | VUs | RPS | p50 | p95 | p99 | Error rate |
| --- | --- | --- | --- | --- | --- | --- |
|  |  |  |  |  |  |  |

*Bottleneck identified: name the component, the metric that saturated first, and the evidence for it.*

## Orphaned-object exposure

Write failures leak objects (a PUT whose transaction rolled back, a crash between upload and
MarkAvailable). The weekly sweep reports them and deletes nothing (ADR-0039). The exposure,
quantified rather than shrugged at: a 0.01% write-failure rate against 30 million statements a
month is roughly **3,000 orphans/month**; at ~200 KB each that is about **600 MB/month** of
unreclaimable storage, or **~$0.30/month** at Glacier Instant Retrieval rates. The
`storage_orphan_total` / `storage_orphan_bytes` metrics are the live version of this estimate —
a rate that departs from it is a write-path bug to fix at the source.

### The sweep's own cost

The local prefix walk is 4,096 `LIST` calls per full cycle (one per shard, more where a shard
exceeds a page) — acceptable against MinIO, unacceptable as a production pattern at 2.5 billion
objects. Production replaces the walk with **S3 Inventory**: a daily manifest diffed offline
against the statement table, with the same classification (referenced / tombstoned / orphan)
and the same report-only rule. The walk stays in the codebase for local development, resumable
at any prefix via its persisted cursor.

## Known limits and next measurements

- *Not measured: sustained month-end burst at the full 1,400 inserts/sec target.*
- *Not measured: anything past the 24-month seed window; larger volumes are extrapolation only.*
- *Not measured: token store behaviour at ~2M live download tokens.*
- *Not measured: audit event growth and partition detach/archive cost at 7-year retention.*
- *Not measured: backup and restore time at full volume.*
