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

## Capacity model

Assumptions stated so a reader who disagrees with one can still follow the arithmetic:

| Assumption | Value | Source |
| --- | --- | --- |
| Customers | 26,000,000 | brief |
| Statements generated | ~30M/month, one burst window | brief |
| Generation window | 6 hours target | brief |
| Statement size | ~200 KB typical PDF; ~1 MB ciphertext envelope ceiling used for sizing | measured locally (renderhash) |
| Delivery traffic | ~0.5 req/s steady, ~30 req/s peak | brief |
| Downloads per statement | ≤ 1.2 (most are never downloaded) | domain estimate |
| Hot-access window | 90 days, then near-zero | domain estimate |
| Retention | 7 years → ~2.52B live objects at steady state | statute |

Derived: generation must sustain ~1,400 items/s across the fleet in the window; the statement
table grows ~30M rows/month into monthly partitions; the delivery path is small in absolute
terms and is engineered for correctness and auditability, not RPS.

## Bottleneck hypotheses — written BEFORE measuring

Predictions first, then the measured comparison lands beside them; being wrong in a documented
prediction is fine, not predicting is not.

1. **The audit chain heads are the write-path ceiling.** Every issue/redeem appends an audit
   event, taking `FOR UPDATE` on one of 16 chain-head rows — 16 serialisation points. Prediction:
   the link-issue ramp knees when `pg_stat_activity` shows `Lock:transactionid`/`tuple` waits
   concentrated on `audit_chain_head`, well before CPU saturates. The chain count is
   **configurable** (`Audit:ChainCount`) precisely because this was anticipated — the fix is more
   chains, and the next constraint after that is PgBouncer's write pool.
2. **The PgBouncer write pool is the second ceiling.** Transaction pooling multiplexes a small
   server-side pool; prediction: `SHOW POOLS` reports non-zero `cl_waiting` and rising `avg_wait`
   on the delivery write pool within one stage of the chain-head knee.
3. **The 50 ms denial floor dominates the denial path.** Uniform-timing padding caps per-
   connection denial throughput at ~20/s by construction. Prediction: replayed-link denials show
   a p50 pinned at ~50 ms regardless of load until connection concurrency saturates — a
   DIFFERENT shape from the success path, and the difference is the security control working.

## Measured results

> **Provenance: NOT YET MEASURED.** This host cannot run the stack (no Docker —
> see LIMITATIONS.md); the harness in `load/` is committed and the tables below are filled in on
> the Docker-capable measurement host, stage by stage, alongside the hypothesis verdicts.

### Delivery path — link issue (`load/01-link-issue.js`)

| Stage (VUs) | RPS | p50 | p95 | p99 | Errors | Notes |
|---|---|---|---|---|---|---|
| 50 | | | | | | |
| 200 | | | | | | |
| 500 | | | | | | |
| 1000 | | | | | | |
| 2000 | | | | | | |

**Bottleneck at the knee:** _to be named from evidence_
**Evidence:** _pg_stat_activity wait events / SHOW POOLS output, captured per load/README.md_
**Fix:** _e.g. raise Audit:ChainCount — then re-measure_
**Next constraint after that fix:** _predicted: PgBouncer write pool (hypothesis 2)_

### Redemption incl. denial split (`load/02-redemption.js`)

| Stage (VUs) | Redeem RPS | redeem p99 | denial p50 | denial p99 | Errors |
|---|---|---|---|---|---|
| 50 | | | | | |
| 200 | | | | | |
| 500 | | | | | |

### Catalogue browse (`load/03-catalogue-browse.js`) and mixed (`load/04`)

| Scenario | Stage | RPS | p95 | p99 | Errors |
|---|---|---|---|---|---|
| browse | | | | | |
| mixed 70/25/5 | | | | | |

### Generation throughput (`load/05-generation.js`, B7)

| Metric | Value |
|---|---|
| Items/s sustained (per worker) | |
| Stage split ledger / render / encrypt+upload / finalize | |
| Peak worker RSS | |
| Workers needed for 30M in 6h = 30,000,000 / (rate × 21,600) | |

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

Superseded in place: the exact commands and acceptance criteria now live in
[Query plans](#query-plans) below, updated for V019's widened list index
(`idx_statement_customer_period_visible`, three visible statuses) — the earlier draft here
referenced the pre-Prompt-6 index and single-status filter.

## Index sizes at seeded volume

*Fill from `pg_total_relation_size` and `pg_indexes_size` at the seeded volume, then extrapolate linearly to 2.52B rows in a second table.*

| Table | Rows | Heap | Index | Total |
| --- | --- | --- | --- | --- |
|  |  |  |  |  |

*State the extrapolation assumption explicitly here (bytes per row and index bytes per row held constant from the seeded sample), and say why it is or is not safe at 2.52B rows.*

## Connection budget

*Paste PgBouncer `SHOW POOLS` output under load, then the per-service arithmetic: pool size x instances, summed, against server `max_connections`.*

## Query plans

> **Provenance: NOT YET CAPTURED** — same Docker constraint. The commands are exact; run them on
> the measurement host against the 100k-customer seed and paste the plans beneath each. The
> acceptance bar: the list shows **partition pruning** (`Subplans Removed` / only the bounded
> months scanned), the token lookup shows an **Index Scan** on `idx_token_hash`, the sweep uses
> `idx_statement_retain_until`. A `Seq Scan` on a 2.4M-row table is a stop-reading defect.

```bash
docker compose exec -T postgres psql -U postgres -d statements <<'SQL'
EXPLAIN (ANALYZE, BUFFERS)
SELECT id, account_id, customer_id, period_start, period_end, version, status
  FROM statement
 WHERE customer_id = (SELECT id FROM customer LIMIT 1)
   AND status IN ('AVAILABLE','ARCHIVED','PURGED')
   AND period_start >= '2025-06-01' AND period_start < '2025-09-01'
 ORDER BY period_start DESC, id DESC LIMIT 20;

EXPLAIN (ANALYZE, BUFFERS)
SELECT id, statement_id FROM download_token
 WHERE token_hash = decode(md5('probe'), 'hex') AND issued_at >= now() - interval '2 days';

EXPLAIN (ANALYZE, BUFFERS)
SELECT id, period_start, customer_id, status, storage_key, retain_until
  FROM statement
 WHERE retain_until < current_date AND status IN ('AVAILABLE','ARCHIVED')
 ORDER BY retain_until LIMIT 10000;
SQL
```

_Plans land here._

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

## Where it breaks

| Tier | What breaks first | Why | What I would change |
|---|---|---|---|
| 1× (26M customers) | Nothing structural — the design point | — | Measure, then tune chain count and pool sizes to the tables above |
| 10× | The monthly generation window | 14,000 items/s needs ~10× workers; the claim queue's SKIP LOCKED contention and PgBouncer's generation pool become the fight | Shard the claim queue by run partition; dedicate a pooler tier to the fleet |
| 100× | Single-writer PostgreSQL for the statement catalogue | 3B rows/month of inserts exceeds one primary's write bandwidth regardless of partitioning | Shard the CATALOGUE by customer hash (the storage layer already shards); ADR-0005's partition-not-shard decision is explicitly revisited here |
| 1000× | The single-region, single-database audit chain model | 16 chains × any count still funnels one region | Regional chains with periodic cross-anchoring; the IChainAnchor seam becomes load-bearing rather than optional |

## Bounded by design

The mandatory date range on every statement list (ADR-0013) is what makes the read path flat at
any scale: a query that cannot name its months cannot be written, so every plan prunes to a
handful of monthly partitions no matter how many years accumulate. The same shape governs the
purge (`retain_until` partial index, bounded batches), the orphan sweep (4,096 resumable shard
prefixes, never a bucket walk), and reconciliation (bounded samples). Unbounded work is not
slow here; it is unrepresentable.

## Known limits and next measurements

- *Not measured: sustained month-end burst at the full 1,400 inserts/sec target.*
- *Not measured: anything past the 24-month seed window; larger volumes are extrapolation only.*
- *Not measured: token store behaviour at ~2M live download tokens.*
- *Not measured: audit event growth and partition detach/archive cost at 7-year retention.*
- *Not measured: backup and restore time at full volume.*
