# Limitations

What this system does not yet do, and what has not yet been proven about it. Kept separate from the
ADRs: an ADR records a decision, this records a gap.

## Deliberate non-goals

Things chosen not to build, each with its reasoning on record:

- **HTTP Range / resumable downloads** — irreconcilable with single-use tokens; the tokens won
  (ADR-0016).
- **Automatic orphan deletion** — report-only, permanently: Compliance locks forbid it anyway,
  and inventory-driven deletion converts a comparison bug into data loss (ADR-0039).
- **Multi-region active-active** — designed at the 1000× tier in SCALE.md's "where it breaks";
  not built, and single-region is stated as a limit rather than papered over.
- **KMS key rotation** — `kek_id` per object and the cohort index mean rotation never rewrites
  history; the rotation job itself is future work.
- **An idempotency key on link issue** — re-issue mints a new link on purpose; replaying an old
  one is the attack, not the feature (ADR-0014).

## Simulated in local development

Honest about what is real and what stands in:

- **Archive tier**: MinIO has no Glacier. Locally nothing moves — the tier flag, the async
  restore contract, the outbox event and the expiring restored copy are all real; the latency
  is a configured delay and the in-place storage-class transition is production-only (ADR-0038).
- **KMS**: `LocalKeyProvider` derives cohort keys from a Development-only secret and refuses to
  start elsewhere; `AwsKmsKeyProvider` is implemented and has KMS-gated tests, but has never run
  against real AWS KMS from this project.
- **Outbox transport**: the transactional outbox, relay, and at-least-once semantics are real;
  the sink is a structured log until a broker exists (`TODO(transport)` in OutboxRelayService
  points here).
- **The core-banking ledger**: a deterministic mock behind a real HTTP boundary, so the
  resilience pipeline is exercised for real even though the data is synthetic.

## Known limitations

Things the design genuinely cannot do, named precisely, with why they were accepted:

- **A privileged insider can forge a self-consistent audit chain.** Chain heads live in the
  same database as the events; rewrite both and verification passes. Truncation from the tail
  is likewise invisible from inside. External anchoring is the fix — `IChainAnchor` is the seam,
  no-op today (`TODO(security)` at its registration points here). Accepted for now because the
  chain still defeats application bugs and opportunistic tampering, and the anchor needs an
  independent trust root that local dev cannot provide (ADR-0010).
- **Truncation of a stream is detected only at stream end.** Per-frame authentication means a
  frame-boundary truncation surfaces when the terminal frame is missed, not mid-download. Full
  protection needs the total length under the AAD of frame one, which is incompatible with
  streaming writes of unknown length — O(1) memory won (ADR-0019).
- **The customer CEK tier is protected by database access controls, not an HSM.** The
  alternative is $26M/month (COST.md, Finding 1). Compensations: CEKs exist only wrapped under
  KMS-held KEKs, column-scoped grants, and destruction VACUUMs the dead tuples.
- **Erasure has a bounded read-side propagation window.** No cross-process cache invalidation
  exists (needs a bus); the write path is closed by the database-side guard, rows go PURGED
  transactionally (downloads 410 immediately), and the residual is a ≤5-minute decrypt window
  on gateway replicas with a warm CEK — see "The warm-cache erasure window" below.
- **The consume scans ~8 future daily token partitions where 2 would do.** A clock-skew
  correctness argument beat a partition-pruning optimisation; the reasoning is above
  `ConsumeSql` and the fix needs a shared clock source or a monitored skew budget.
- **A failed finalize can orphan a Compliance-locked object.** Upload-before-commit is the
  crash-safe ordering (the inverse loses data instead of money); the orphan sweep reports the
  residue and SCALE.md prices it (~$0.30/month).

## Test coverage, and where it runs

The suite is **689 tests** across four projects. **163 of them need a container runtime** — real
PostgreSQL and real MinIO through Testcontainers — and are gated behind
`[Fact(SkipUnless = ...)]` with a stated reason, so a machine without Docker reports them as
*skipped* rather than silently not having them.

| Where | What executes |
|---|---|
| A developer host without Docker | 526 pass, 163 skip, 0 fail |
| CI (GitHub Actions, Linux) | **All of it, green.** The full integration suite (173 tests) against real PostgreSQL and MinIO; a separate job that builds every image, boots the entire compose stack, proves `db-migrator` exits 0 and both services answer `/health/ready`; and a twenty-iteration loop over the concurrent-redemption invariant |

Everything the gated tests cover — migrations applying to a real database, the GRANT matrix
actually refusing, leader election and fencing, partition pruning proven by `EXPLAIN`, Object
Lock genuinely refusing a delete, and a 200 MB encrypted download staying O(1) in memory —
executes on every push.

What that leaves genuinely unproven is small, and specific:

- **Real AWS KMS.** Four tests are gated on live KMS credentials. `AwsKmsKeyProvider` is
  implemented and is exercised against the key-provider contract, but has never run against real
  AWS KMS from this project.
- **Production-scale behaviour.** The measured figures in SCALE.md come from a single four-core
  host that also ran the load generator. That is stated there, and it bounds every number in it.

### The warm-cache erasure window

`DataKeyCache` holds CEKs for up to `Crypto:DekCache:MaxAge` (default five minutes) and there is
no cross-process invalidation - eviction on destroy covers only the erasure executor's own
process, because invalidation across replicas needs a bus this system does not yet have. What
closes the dangerous path is the DATABASE-side write guard: `MarkAvailableAsync`
refuses to publish for a customer whose key is `DESTROYED` or `SCHEDULED_DESTRUCTION`, in the
same transaction as the publish itself, so a worker with a warm cached CEK cannot land a
statement that is about to become unreadable. The residual gap is READ-side only - a gateway
replica could decrypt for up to MaxAge after destruction - and it is closed at the row level
first: the erasure marks every statement PURGED in the same pass, and the gateway answers 410
from the row before it ever touches a key. Eventual fix when a message bus exists: an eviction
event fanned out to every cache.

## Deferred by design

These are scheduled, not missing. Listed so the two categories do not get confused.

| Item | Scheduled for |
|---|---|
| `IChainAnchor` is a no-op — chain heads live in the same database as the events they attest | Deferred; ADR-0010 names the February 2027 external audit as the deadline |
| KMS key rotation — `kek_id` per object and the cohort index mean rotation never rewrites history; the job itself is not built | When a rotation policy is set |
| Outbox transport: the relay publishes to a logging sink behind `TODO(transport)` — the pattern (atomic write, at-least-once drain, lag metric) is real; the send swaps in when a consumer exists | When any consumer arrives; ADR-0026 |

## Known-and-accepted

**The atomic consume scans more partitions than it needs to.** `ConsumeSql` bounds `expires_at` from
below but not above, so it visits roughly eight future daily partitions where two would do.
`RevokeSql` carries the upper bound; the consume cannot, because `issued_at` is supplied by the
application clock while `now()` is the database clock, and a token issued at the 3600-second maximum
TTL by a host whose clock runs ahead would fall outside `now() + INTERVAL '1 hour'` and return a 404
while still perfectly valid. The reasoning is written out in full above `ConsumeSql`. Fixing it
properly means either sourcing both timestamps from the same clock or adopting an explicit,
monitored skew budget.

**A failed finalize can orphan an encrypted object under a seven-year Compliance lock.** The render
pipeline uploads before the metadata transaction commits (uploading after would invert into the worse
failure: a row promising bytes that do not exist). The deterministic object key means every retry
overwrites the same key — orphans do not multiply per attempt — but an item abandoned after upload
leaves exactly one. Quantified: at a 0.01% commit-failure rate on a 30M/month run, ~3,000
orphans/month at ~200 KB ≈ **600 MB/month of unreclaimable storage** until the lock expires.
Two-phase locking (retention applied only after commit) is not available: the bucket's default
Object Lock retention applies at PUT. The orphan sweep covers this residue (report-only, ADR-0039) — the
other half of reconciliation CHECK 1.

**"Run PAUSED" reflects one replica's circuit breaker.** Enforcement is distributed (every replica's
claim loop stops on its own breaker), but the status flip is performed by the lease-holding
orchestrator observing its own breaker. A partition that isolates only the orchestrator replica could
mislabel the run. Accepted, with the reasoning and revisit trigger in ADR-0030.

## Next, in priority order

1. **A real `IChainAnchor`** — terminal hashes written to append-only storage under Object Lock,
   closing the insider-forgery limitation above. This is the most valuable remaining item.
2. **Validate `AwsKmsKeyProvider` against real AWS KMS**, and build the key-rotation job the
   `kek_id`-per-object design already accommodates.
3. **Audit partition detach/archive** for months older than N — the biggest measured cost lever
   (COST.md).
4. **Broker transport for the outbox** — the relay's `TODO(transport)`; the pattern is real, only
   the send is a logging sink.
5. **Cross-process CEK cache invalidation**, which closes the read-side warm-cache window above
   and needs the same bus as item 4.
