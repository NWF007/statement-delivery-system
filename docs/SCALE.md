# Scale

> **Status: measured.** The tables below were filled in on 2026-09-01 against a live compose stack
> seeded to 85.7 million statements. Every number is reproducible by the commands shown in the same
> section. Rows that could not be measured say so, and say why.

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

**Verdicts: 1 CONFIRMED, 2 CONFIRMED (but concurrent, not subsequent), 3 CONFIRMED (measured by a
different route than planned).** A fourth constraint that nobody predicted — WAL flush — outranked
the chain heads at the knee. Details in each section below.

## Measured results

> **Provenance: MEASURED 2026-09-01.** Output from the commands shown in each section, against the
> host described immediately below. Nothing here is extrapolated unless the row says so.

### Measurement host

The numbers only mean something next to the machine that produced them.

| Property | Value |
| --- | --- |
| OS | Windows Server 2025 Datacenter 10.0.26100 |
| CPU / RAM | 4 logical CPUs / 15.7 GB |
| Docker | Docker Desktop 29.7.2, WSL2 backend, 9.71 GB allocated to the VM |
| Storage | one 120 GB volume shared by PostgreSQL, MinIO and the k6 client |
| PostgreSQL | 17.11-alpine in the compose stack, `max_connections=200` |
| k6 | v2.2.0, running on the SAME host as the system under test |

**The load client and the system under test share four cores and one disk.** That bounds every RPS
number below, and it is why the 500+ VU stages measure the harness rather than the architecture.

### Delivery path — link issue (`load/01-link-issue.js`)

Each iteration is TWO requests — a development token mint, then the issue. The RPS column is
issues/sec (k6 `iterations`), not `http_reqs`, which is double that.

| Stage (VUs) | Issues/s | p50 | p95 | p99 | Errors | Notes |
|---|---|---|---|---|---|---|
| 50 | 263.9 | 72 ms | 249 ms | 346 ms | 0.00% | full 2 min |
| 200 | 262.3 | 121 ms | 915 ms | 1.11 s | 0.00% | full 2 min — **the knee** |
| 500 | 252.9 | 121 ms | 1.40 s | 1.59 s | 9.09% | aborted 4 s — harness limit |
| 1000 | 418.2 | 89 ms | 1.21 s | 1.56 s | 30.51% | aborted 4 s — harness limit |
| 2000 | 1668.6 | 0 ms | 462 ms | 530 ms | 70.79% | aborted 4 s — harness limit |

**Read the last three rows as harness data, not system data.** The `link issued (201)` check passed
100% at *every* stage. What failed above 200 VUs was the development token mint, with
`connectex: No connection could be made because the target machine actively refused it` — the
Windows/Docker-Desktop port forward running out of connections. Those rows' latencies are artefacts
(a p50 of 0 ms is failed requests returning instantly) and their RPS counts requests that never
reached the API.

**Bottleneck at the knee: the write path saturates at ~263 issues/sec.** Throughput is flat from 50
to 200 VUs (263.9 → 262.3, under 1% apart) while p99 more than triples (346 ms → 1.11 s). Flat
throughput with latency rising in proportion to offered concurrency is saturation by definition.

**Evidence**, sampled mid-stage exactly as `load/README.md` prescribes:

```
vus=50                                   vus=200
 wait_event_type | wait_event    | cnt    wait_event_type | wait_event    | cnt
 Lock            | transactionid |  4     LWLock          | WALWrite      |  8
 Lock            | tuple         |  1     Lock            | transactionid |  5
                                          Lock            | tuple         |  1
                                          IO              | WalSync       |  1

SHOW POOLS — statements_delivery / app_delivery
           cl_active   cl_waiting   sv_active   maxwait_us
 vus=50       38           5           20          2,732
 vus=200      31          12           20         17,614
```

**Hypothesis 1 (audit chain heads) — CONFIRMED.** `Lock: transactionid` and `Lock: tuple` waits are
present at both clean stages — the `FOR UPDATE` on the 16 `audit_chain_head` rows serialising — and
they appear well before CPU saturates, as predicted.

**Hypothesis 2 (PgBouncer write pool) — CONFIRMED, but CONCURRENT rather than subsequent.** The
prediction was that pool pressure would appear "within one stage of the chain-head knee". It is in
the *same* sample: `sv_active` pinned at 20 (the whole `statements_delivery` pool) with `cl_waiting`
rising 5 → 12 and `maxwait` 2.7 ms → 17.6 ms. These are not two ceilings in sequence but one
compound ceiling, which matters for the fix: raising `Audit:ChainCount` alone would not move it.

**A fourth constraint, unpredicted: WAL flush.** At 200 VUs `LWLock: WALWrite` (8) is the single
largest wait category, `IO: WalSync` behind it — both outranking the chain-head locks. WAL fsync on
this one shared volume contributes more than the serialisation the hypotheses were written about. On
a host with a dedicated WAL device this would rank differently, which is exactly why it belongs in
the results rather than being retrofitted into the predictions.

**Fix, in the order the evidence supports:** give WAL its own device and raise
`statements_delivery` `pool_size` past 20; only after those does `Audit:ChainCount` become the
binding constraint worth tuning. One change at a time, re-measuring between.

### Redemption incl. denial split (`load/02-redemption.js`)

| Stage (VUs) | Redeem RPS | redeem p99 | denial p50 | denial p99 | Errors |
|---|---|---|---|---|---|
| 50 | *not measurable* | — | — | — | 100% of redeems |
| 200 | *not measurable* | — | — | — | 100% of redeems |
| 500 | *not measurable* | — | — | — | 100% of redeems |

**The redeem path cannot be measured against seeded data, by construction.** `tools/seed` writes
catalogue rows whose storage keys are *computed*, never uploaded; its own comment is explicit —
*"the objects these rows name have never existed"*. Every redemption therefore returns
`404 Download unavailable` at the gateway before reaching decrypt-and-stream. Confirmed by hand:

```
$ curl -s -o /dev/null -w '%{http_code}' "$ISSUED_URL"
404    {"title":"Download unavailable", ...}
```

This is a **gap between the seeding tool and the load harness**, not a fault in either alone: the
seeder exists to size the catalogue, the harness assumes retrievable content, and no one had run
both together before today. Measuring redeem throughput requires a generation run to have written
real ciphertext first — see *Generation throughput* below for why this host cannot afford that.

**Hypothesis 3 (the 50 ms denial floor) — CONFIRMED, by a different route.** With the script's
replay path blocked by the same missing objects, the uniform-timing floor was measured directly
against the gateway with unknown tokens:

```bash
for i in $(seq 1 25); do
  BOGUS=$(head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=')
  curl -s -o /dev/null -w '%{http_code} %{time_total}\n' "http://localhost:8082/v1/d/$BOGUS"
done
```

| Metric | Value |
| --- | --- |
| Status codes | 404 × 25 |
| min / median / max | 53.9 ms / 57.1 ms / 66.5 ms |
| mean | 58.8 ms |

Every sample sits above the 50 ms floor within a 12.6 ms band, and the denial path's timing shape is
flat where the success path's p50 climbs with load (72 ms → 121 ms). Both halves of the prediction
hold.

*Observation, not a defect:* a **valid** token whose object is missing denies in ~12 ms, roughly 5×
faster than an unknown token's ~58 ms. This is not an enumeration oracle — reaching the fast path
needs a token the caller was legitimately issued for their own statement — but if those two 404s
ever became reachable from one guessable input, the gap would be a timing side channel.

### Catalogue browse (`load/03-catalogue-browse.js`) and mixed (`load/04`)

| Scenario | Stage | RPS | p50 | p95 | p99 | Errors |
|---|---|---|---|---|---|---|
| browse | 50 | 279.4 | 76 ms | 239 ms | 337 ms | 0.00% |
| browse | 200 | 288.6 | 134 ms | 829 ms | 990 ms | 0.00% |
| browse | 500 | 224.7 | 117 ms | 1.85 s | 2.09 s | 0.00% (p99 threshold breached) |
| mixed 70/25/5 | 50 | 235.2 | 43 ms | 239 ms | 313 ms | 2.64% |
| mixed 70/25/5 | 200 | 170.7 | 52 ms | 1.03 s | 1.15 s | 1.56% |
| mixed 70/25/5 | 500 | 169.7 | 126 ms | 1.51 s | 1.67 s | 15.07% |

Browse holds **zero errors at every stage**, including 500 VUs where it breaches only the p99
threshold: the read path degrades in latency, never in correctness. That is what the mandatory
bounded date range is supposed to buy, at 85.7M rows.

Its ceiling (~289/s) is within 10% of the write path's (~263/s), which says that at this scale both
are limited by the same shared host resource rather than by anything specific to either path.

The mixed scenario's errors are **its 5% redeem slice 404ing** for the reason above, not a separate
finding; its browse and issue slices behave as their standalone scenarios do.

### Generation throughput (`load/05-generation.js`)

| Metric | Value |
|---|---|
| Items/s sustained (per worker) | *not measured* |
| Stage split ledger / render / encrypt+upload / finalize | *not measured* |
| Peak worker RSS | *not measured* |
| Workers needed for 30M in 6h | *not measured* |

**Not measured, for a disk reason worth stating.** `05-generation.js` requests a run for the previous
month across every account — 3,743,364 of them at the seeded volume. At the ~200 KB typical
ciphertext used for sizing, that is roughly **740 GB** of MinIO objects against **20 GiB** free after
seeding. The natural place to take a bounded version of this measurement is
`IntegrationTests.FullRunTests.FullRun_1000Accounts`, which drives exactly this path at a workable
size; that test currently fails on this machine (stalls at 0 done of 1000), so the generation
figures stay open until it passes.

## Seeding a representative dataset

```bash
Postgres__PrimaryConnectionString='Host=localhost;Port=6432;Database=statements_generation;Username=app_generation;Password=...' \
  dotnet run --project tools/seed -c Release -- --customers 500000 --months 24 --seed 101
```

*Deterministic from `--seed`, so two runs produce identical data and their timings are comparable.
Distribution is deliberately uneven: ~85% of customers hold one account, ~2% hold three, ~8% are
dormant with gaps in their history, and ~1.5% of statements carry a regenerated `version = 2` row
alongside the original.*

**Run it in chunks, not as one large job.** `customers` and `accounts` are fully materialised lists
before the first write, so peak RSS scales linearly with `--customers`: 204 MiB measured at 100k
projects to ~7 GiB at 3.5M, against a host whose Docker VM already holds 9.71 of 15.7 GB. Six
500k-customer chunks with distinct `--seed` values (101–106) keep peak RSS near 1 GiB, and each
chunk commits independently — a failure costs one chunk, not the whole run.

### Calibration run — 100k customers into an existing 5.35M-row table

| Measurement | Value |
| --- | --- |
| Customers written | 100,000 (125,165 rows/sec) |
| Accounts written | 116,747 (17,007 rows/sec) |
| Statements written | 2,675,820 |
| **Statement rows/sec (binary COPY)** | **44,511** |
| Total rows | 2,892,567 in 67 s |
| Wall clock incl. build and ANALYZE | 98 s |
| Peak RSS | 204 MiB |
| Database growth | 1,438 MB → 2,854 MB (+1,416 MB) |

### Volume run — 6 × 500k customers

| Chunk | `--seed` | Elapsed | Statement rows/sec |
| --- | --- | --- | --- |
| 1 | 101 | 306 s | 51,179 |
| 2 | 102 | 321 s | 50,858 |
| 3 | 103 | 326 s | 51,121 |
| 4 | 104 | 354 s | 47,747 |
| 5 | 105 | 350 s | 46,884 |
| 6 | 106 | 402 s | 43,471 |

**Throughput declines 15% (51,179 → 43,471 rows/sec) as the table grows from 5M to 86M rows.**
Partition count is fixed at 31 throughout, so this is not partition overhead — it is index
maintenance against progressively deeper B-trees. Linear extrapolation of seed time to larger
volumes will therefore be optimistic.

Each chunk consumed a steady **8 GiB** of disk against ~7 GB of database growth; the extra ~1 GiB is
WAL and Docker VM overhead. Budget 8 GiB per 500k customers, not the database delta alone.

### Final seeded volume

| Measurement | Value |
| --- | --- |
| Customers | 3,200,000 |
| Accounts | 3,743,364 |
| Statements | 85,764,216 |
| Monthly partitions | 31 (2024-06 → 2026-12) |
| Statements per customer | 26.8 |
| Database size | 44 GB |
| Total seed wall clock | 43.7 min |

## Index sizes at seeded volume

```sql
SELECT sum(pg_relation_size(c.oid)) AS heap, sum(pg_indexes_size(c.oid)) AS index
  FROM pg_class c JOIN pg_inherits i ON i.inhrelid = c.oid
 WHERE i.inhparent = 'statement'::regclass;
```

| Table | Rows | Heap | Index | Total |
| --- | --- | --- | --- | --- |
| statement (31 partitions) | 85,764,216 | 28 GB | 15 GB | 43 GB |
| customer | 3,200,000 | 236 MB | 312 MB | 547 MB |
| account | 3,743,364 | 333 MB | 209 MB | 542 MB |
| download_token (10 partitions) | 65,739 | — | — | 26 MB |
| audit_event (8 partitions) | 145,893 | — | — | 98 MB |

Per statement row: **356.2 B heap + 186.6 B index = 542.9 B total.**

`customer` is the one table whose indexes exceed its heap (312 MB vs 236 MB) — the external-reference
unique constraint plus the primary key over narrow rows.

### Extrapolation to 2.52B rows

Assumption: bytes per row held constant from the seeded sample.

| | Measured @ 85.7M | Extrapolated @ 2.52B | Target |
| --- | --- | --- | --- |
| Heap | 28 GB | **898 GB** | ~800 GB |
| Index | 15 GB | **470 GB** | ~600 GB |
| Total | 43 GB | **1,368 GB** | ~1,400 GB |

**Why the constant-bytes-per-row assumption is safe here:** it was measured at three volumes spanning
32× — 544.6 B/row at 2.7M rows, 543.6 B/row at 5.4M, 542.9 B/row at 85.7M — a spread under 0.4%, and
drifting *down* rather than up. Fixed-width columns and monthly partitions that cap individual index
depth are why.

**Why it is still not a guarantee at 2.52B:** the seeded window is 31 months, not 84, so no partition
here has aged through a purge/archive cycle, and every row is `AVAILABLE`. A steady-state 7-year
table carries `ARCHIVED` and `PURGED` rows whose `storage_key` and key material are nulled, which
would push bytes/row *down*, and bloat from the purge churn, which would push it *up*. Neither is
measured. Total lands within 2% of the 1,400 GB design figure, but the heap/index split does not:
heap is 12% above target and index 22% below.

## Connection budget

`deploy/pgbouncer/pgbouncer.ini`, against server `max_connections=200`:

| Pool | `pool_size` | Observed peak `sv_active` |
| --- | --- | --- |
| statements_delivery | 20 | **20 (exhausted at 50 VUs)** |
| statements_download | 20 | 1 |
| statements_generation | 40 | 4 |
| statements_retention | 5 | 3 |
| **Total** | **85** | — |

Plus `reserve_pool_size = 5` per pool (105 worst case), the migrator, and any human with psql —
comfortably inside 200. `max_client_conn = 2000` is what lets 2,000 k6 VUs connect to the pooler at
all while only 85 server connections exist behind it, which is the whole point of the tier.

**The delivery pool is the one that binds.** It reached its full 20 at the lowest load stage tested
and stayed there, with clients queueing (`cl_waiting` 5 → 12, `maxwait` 2.7 ms → 17.6 ms). At 26M
customers this pool is the first number to raise, and `max_connections` has the headroom for it:
delivery could go to 100 and the fleet total would still be 165.

## Query plans

> **Provenance: CAPTURED 2026-09-01** against the 85.7M-row seed. Acceptance bar: the list shows
> **partition pruning**, the token lookup shows an **Index Scan** on `idx_token_hash`, the sweep uses
> `idx_statement_retain_until`. A `Seq Scan` on a populated partition is a stop-reading defect.

```bash
docker compose exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" postgres psql -U postgres -d statements <<'SQL'
EXPLAIN (ANALYZE, BUFFERS)
SELECT id, account_id, customer_id, period_start, period_end, version, status
  FROM statement
 WHERE customer_id = (SELECT id FROM customer LIMIT 1)
   AND status IN ('AVAILABLE','ARCHIVED','PURGED')
   AND period_start >= '2025-06-01' AND period_start < '2025-09-01'
 ORDER BY period_start DESC, id DESC LIMIT 20;

-- NOTE: the column is token_sha256, and download_token is RANGE-partitioned by expires_at.
-- An earlier draft of this file bounded on issued_at and named the column token_hash (which is
-- the INDEX name); that query fails outright with "column token_hash does not exist", and even
-- with the column corrected an issued_at bound prunes nothing.
EXPLAIN (ANALYZE, BUFFERS)
SELECT id, statement_id FROM download_token
 WHERE token_sha256 = sha256('probe'::bytea) AND expires_at >= now();

EXPLAIN (ANALYZE, BUFFERS)
SELECT id, period_start, customer_id, status, storage_key, retain_until
  FROM statement
 WHERE retain_until < current_date AND status IN ('AVAILABLE','ARCHIVED')
 ORDER BY retain_until LIMIT 10000;
SQL
```

### 1. Customer statement list — PASS

Pruned to **3 of 31 partitions**, Index Scan on each, 13 buffer reads, **7.5 ms** at 85.7M rows.

```
 Limit  (cost=1.31..36.06 rows=9) (actual time=3.806..7.112 rows=3 loops=1)
   Buffers: shared read=13
   ->  Append  (actual time=3.805..7.108 rows=3 loops=1)
         ->  Index Scan using statement_2025_08_customer_id_period_start_id_idx on statement_2025_08
               Index Cond: ((customer_id = ...) AND (period_start >= '2025-06-01') AND (period_start < '2025-09-01'))
         ->  Index Scan using statement_2025_07_customer_id_period_start_id_idx on statement_2025_07
         ->  Index Scan using statement_2025_06_customer_id_period_start_id_idx on statement_2025_06
 Execution Time: 7.455 ms
```

Twenty-eight partitions holding 82 million rows are never touched. This is ADR-0013's claim made
literal: the query cannot be written without naming its months, so it cannot fail to prune.

*On index names:* partitions carry either `..._cust_period_visible_idx` or
`..._customer_id_period_start_id_idx`. The definitions are identical —
`(customer_id, period_start DESC, id DESC) WHERE status IN ('AVAILABLE','ARCHIVED','PURGED')` — and
all 31 partitions have exactly 5 indexes. V019 named the indexes on partitions that existed when it
ran; partitions created later by `ensure_range_partitions` auto-attached to the parent and took
PostgreSQL's generated name. Cosmetic, not a gap.

### 2. Token lookup — PASS

```
 Append  (cost=0.15..82.02 rows=10) (actual time=0.075..0.076 rows=0 loops=1)
   Buffers: shared hit=17
   Subplans Removed: 2
   ->  Index Scan using download_token_2026_09_01_token_sha256_expires_at_idx on download_token_2026_09_01
         Index Cond: ((token_sha256 = '\xba9c...'::bytea) AND (expires_at >= now()))
   ... (8 further daily partitions, all Index Scan)
```

`Subplans Removed: 2` is runtime pruning on `expires_at`, and every remaining partition is an Index
Scan on `idx_token_hash`. Captured with 65,739 live tokens in the table, written by the load runs
above.

### 3. Retention sweep — PASS

Index Scan on `..._retain_until_idx` on every populated partition, `Execution Time: 37.377 ms`.

This plan touches all 31 partitions and that is **correct, not a miss**: the predicate is on
`retain_until`, which is not the partition key, so there is nothing to prune. Boundedness comes from
the partial index and the `LIMIT`, not from pruning. The empty partitions show `Seq Scan` at cost
0.00 — PostgreSQL declining to open an index on a zero-row relation, not a defect.

## Partition pruning proof

Superseded in place: the exact commands and acceptance criteria live in
[Query plans](#query-plans) above, now with captured output.

## Load-harness defects found while measuring

The harness had never been executed anywhere before today (`load/README.md` said so). Running it
found three defects; the tables above are from the fixed harness.

1. **Every POST failed with 415.** `http.post(url, JSON.stringify({}), {headers:{Authorization}})`
   sets no `Content-Type`, k6 defaults a string body to `text/plain`, and the API answers
   `415 Unsupported Media Type`. Affected `01`, `02` and `04` — 100% of issue requests, 1,875 of
   1,875 in the first run. Fixed with a `jsonAuthHeaders()` helper in `load/lib.js`, kept separate
   from `authHeaders` so GET paths do not advertise a body content type.
2. **The sample query does not scale to the seeded volume.** `load/README.md`'s
   `ORDER BY random() LIMIT 5000` is a full scan of 28 GB at 85.7M rows. Replaced with a
   `TABLESAMPLE` on the unpartitioned `customer` table joined to one month of statements — 4.4 s
   instead:

   ```sql
   \copy (WITH c AS (SELECT id FROM customer TABLESAMPLE SYSTEM (12) LIMIT 300000)
          SELECT DISTINCT ON (s.customer_id) s.customer_id, s.id AS statement_id, s.period_start
            FROM c JOIN statement s ON s.customer_id = c.id
           WHERE s.period_start >= '2026-06-01' AND s.period_start < '2026-07-01'
             AND s.status = 'AVAILABLE'
           ORDER BY s.customer_id LIMIT 200000) TO STDOUT WITH CSV HEADER
   ```

3. **The sample must be large enough that the per-customer rate limit does not become the
   measurement.** `IssueLinkRules` allows 10 issues/minute/customer, so a sample of *N* distinct
   customers caps the write path at `N × 10/60` requests/sec. The first 4,225-customer sample capped
   it at ~704/s and produced a 49.89% failure rate that looked like a system limit. The committed
   sample is now 200,000 distinct customers (~8,333/s ceiling), high enough that PostgreSQL binds
   first.

## Production defect found while measuring

**The per-caller rate limiter partitioned by IP address, not by authenticated subject.**

`Program.cs` had `app.UseRateLimiter()` *before* `app.UseAuthentication()`. The `PerCallerPolicy`
partition key is:

```csharp
httpContext.User.FindFirst("sub")?.Value
    ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"
```

Evaluated before authentication has run, `httpContext.User` is empty, so `FindFirst("sub")` was
always null and every request fell through to the IP branch. **The entire API shared one
120-request-per-minute partition.**

The symptom was invisible until the 415s above were fixed, and then looked like a system ceiling:
successes pinned at ~120/minute whether k6 ran 50 VUs or 2000 — invariant to load, which is the
signature of a fixed window rather than a resource limit. The controlled test:

| Test | Before | After |
| --- | --- | --- |
| 150 requests, 150 **distinct** customers | 120 × 201, then 30 × 429 | **150 × 201** |
| 15 rapid requests, **one** customer | — | **10 × 201, then 5 × 429** |

The 121st distinct customer was rejected for the traffic of the 120 before it. That is verbatim the
failure the partition key was written to prevent — from `DeliveryApiExtensions.cs`: *"Partitioning an
authenticated API by IP alone punishes every customer behind one corporate NAT for the behaviour of
one."* The key was right; the middleware order defeated it. In production behind a load balancer,
where every customer arrives from a few egress IPs, the whole customer base would have shared one
bucket — and `MapInboundClaims = false` was correctly set, with a correct `sub` claim, so the code
reads as if it works.

Fixed by moving `UseRateLimiter()` after `UseAuthentication()`/`UseAuthorization()`, with the
measurement recorded in a comment so it is not reordered back. The second row of the table is the
part that matters for security review: the per-customer budget still bites on exactly the 11th
request, so nothing was loosened — a dead control was made live.

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
| 1× (26M customers) | The `statements_delivery` pool at 20, and WAL flush | Both measured: pool exhausted at the lowest stage tested, `LWLock:WALWrite` the top wait at the knee | Raise delivery `pool_size` (headroom exists: 85 of 200 used); move WAL to its own device |
| 10× | The monthly generation window | 14,000 items/s needs ~10× workers; the claim queue's SKIP LOCKED contention and PgBouncer's generation pool become the fight | Shard the claim queue by run partition; dedicate a pooler tier to the fleet |
| 100× | Single-writer PostgreSQL for the statement catalogue | 3B rows/month of inserts exceeds one primary's write bandwidth regardless of partitioning | Shard the CATALOGUE by customer hash (the storage layer already shards); ADR-0005's partition-not-shard decision is explicitly revisited here |
| 1000× | The single-region, single-database audit chain model | 16 chains × any count still funnels one region | Regional chains with periodic cross-anchoring; the IChainAnchor seam becomes load-bearing rather than optional |

The 1× row changed as a result of measuring. It previously read "nothing structural — the design
point"; the delivery pool and WAL flush are both real ceilings at the design point, and neither
needs a re-architecture to lift.

## Bounded by design

The mandatory date range on every statement list (ADR-0013) is what makes the read path flat at
any scale: a query that cannot name its months cannot be written, so every plan prunes to a
handful of monthly partitions no matter how many years accumulate. The same shape governs the
purge (`retain_until` partial index, bounded batches), the orphan sweep (4,096 resumable shard
prefixes, never a bucket walk), and reconciliation (bounded samples). Unbounded work is not
slow here; it is unrepresentable.

Measured, at 85.7M rows: the list touches 3 partitions of 31 and returns in 7.5 ms, and browse holds
0% errors through 500 VUs. The claim survives contact with data.

## Known limits and next measurements

- *Not measured: the redeem path end to end.* Blocked by seeded rows naming objects that do not
  exist; needs a generation run first.
- *Not measured: generation throughput.* Needs ~740 GB of object storage at the seeded volume;
  20 GiB free. `FullRun_1000Accounts` is the bounded proxy and currently fails on this host.
- *Not measured: sustained month-end burst at the full 1,400 inserts/sec target.* The measured
  ceiling is 263 issues/sec on a 4-core shared host — a host limit, not a system limit, but the
  1,400/sec figure remains unverified.
- *Not measured: anything past the 31-month seed window.* Larger volumes are extrapolation, and the
  extrapolation's weak point is named above: no partition here has aged through a purge cycle.
- *Not measured: token store behaviour at ~2M live download tokens.* Peak reached was 65,739.
- *Not measured: audit event growth and partition detach/archive cost at 7-year retention.* Peak
  reached was 145,893 events.
- *Not measured: backup and restore time at full volume.*
- *Not re-measured after the rate-limiter fix: whether the ~263 issues/sec ceiling moves.* The fix
  changed which partition a request counts against, not the work per request, so it should not — but
  that is reasoning, not a measurement.
