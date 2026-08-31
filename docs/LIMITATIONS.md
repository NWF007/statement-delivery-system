# Limitations

What this system does not yet do, and what has not yet been proven about it. Kept separate from the
ADRs: an ADR records a decision, this records a gap.

## Test coverage that has never executed

**As of 2026-08-30, covering commit `41048ce`, the Prompts 1–4 audit remediation, and Prompt 5.**

The suite is 457 tests, 0 failed. **109 of them have never run on the development host**, because they
need a container runtime that host cannot provide:

| Gate | Count | Why |
|---|---|---|
| `DockerAvailability.SkipReason` | 105 | Docker Desktop is installed but its Linux engine cannot start: WSL is not installed and the Hyper-V `vmcompute` service does not exist. This is a nested-virtualisation guest, so enabling either is not a quick fix. `docker info` returns HTTP 500. |
| `AwsKmsAvailability.SkipReason` | 4 | Need real AWS KMS credentials. |

Both gates are `[Fact(SkipUnless = ...)]` with a stated reason, so they report as skipped rather than
silently not existing. That is the right mechanism. It does not make them coverage.

### What that leaves unproven

Of the forty Prompt 1–3 acceptance checks, **13 have never been executed anywhere**. Nine passed by
execution, seventeen were verified statically by reading migrations, compose and source, one is
partial. The ones that have never run:

- Cold start from a clean state, and `db-migrator` exiting 0
- Telemetry: traces and structured logs in the Aspire dashboard, and a `traceId` in a JSON log line
  matching a trace
- Partition maintenance having actually run
- The seed tool, and its throughput
- **Partition pruning.** `WithDateRange_PrunesPartitions` asserts on `EXPLAIN` output and has never
  executed. The index and the partition key were read and are right; the plan has not been seen.
- The read path end to end
- **Chain verification under concurrency.** `FOR UPDATE` is present and correct by inspection; the
  concurrent-writer test has never run.
- Happy-path download and replay
- **Concurrent redemption.** `ConcurrentRedemption_ExactlyOneSucceeds` is described in its own
  comment as the most persuasive test in the repository. It has never executed.
- 200 MB streaming without heap growth
- Chain verification after everything else

Every schema test is behind the Docker gate. That is the single worst concentration: a migration
change can break a seeder, and a green local run will not notice. This has already happened once —
V013's `ck_statement_available_has_key_material` broke three seeders that insert `AVAILABLE` rows
directly, and the run stayed green because all three are Docker-gated.

### The tests added on 2026-08-30, and their status here

The audit remediation added five Docker-gated tests. They are written and they compile; **none has
executed on this host.**

| Test | Proves |
|---|---|
| `Redemption_SucceedsEvenWhenReplicaLags` | A lagging replica cannot deny a statement that exists on the primary (ADR-0024) |
| `Redemption_WhenAuditWriteFails_DoesNotConsumeToken` | A failed audit append rolls the consume back (ADR-0025) |
| `MarkAvailable_WithFullEnvelope_SatisfiesAllCheckConstraints` | The publish path satisfies V006, V013 and V015 together |
| `MarkAvailable_WithoutCryptoColumns_IsRejectedBy23514` | The database refuses a status-only transition to `AVAILABLE` |
| `MarkFailed_LeavesTheStatementRetryable` | A failed render does not invent or erase crypto columns |

Two architecture tests added at the same time — `DownloadGateway_MustNotCall_TransactionlessStatementRead`
and `TheTransactionalOverload_IsTheOneTheGatewayUses` — **do** run here, and were confirmed red before
the fix and green after.

### Prompt 5's tests, and their status here

The batch subsystem's proof splits the same way. **Runs on this host, every build:** byte-determinism
(in-process AND across real child-process restarts), 800-line pagination, zero-transaction rendering,
forward-only streaming, the money IL rule, the mock ledger's determinism/faults (hosted in-process),
and the ledger client's retry/no-retry/poison behaviour through the REAL resilience pipeline.
**Docker-gated, never executed here:** the claim queue under 50 concurrent workers, attempt semantics,
the reaper, planning idempotency/resumability/streaming, poison quarantine, circuit-open
pause-and-resume, worker-crash recovery, generation-vs-delivery pool isolation, and
`FullRun_1000Accounts_AllStatementsAvailableAndDownloadable` — the complete loop. The single most
important test in the subsystem (`ConcurrentWorkers_NeverClaimSameItem` — a claim bug means duplicate
statements under a seven-year Compliance lock) is in the unexecuted set. CI is where all of these run
first.

### The obligation

**Before the Prompt 7 fresh-clone gate, the full suite must execute somewhere** — a working Docker
host or a CI job with a daemon. A submission whose schema tests have never run is not one worth
defending, and the two highest-value tests in the repository are both in the unexecuted set.

`HIGH 2` from the Prompts 1–4 audit is the concrete argument for this. It was a replication-routing
bug on the redemption hot path that would have presented as a ciphertext-integrity alert, and a real
concurrent-redemption test against a lagging replica would have caught it. Nothing static did.

### Escalation, 2026-08-30: Docker is now the critical path

Last round this was a note. This round it demonstrably cost the project: the Prompt 5 audit found
a CRITICAL — every batch-pipeline upload failed with `Could not determine content length`, so a
full production run would have quarantined all 30 million items — and it was found only because
the auditor wrote a ten-line probe by hand. `FullRun_1000Accounts` would have caught it on its
first CI run with 1,000 quarantined items. Six Prompt 5 acceptance checks (56, 57, 59, 62, 69,
70) that read ENV-BLOCKED **would have failed at that moment**, not passed: "unexecuted" and
"green" had silently diverged, which is exactly the gap this file exists to keep visible.

The current numbers, as of the RED remediation:

- **This host:** Windows Server 2025 nested-virtualisation guest, no WSL, no Hyper-V
  `vmcompute` — the Docker Linux engine cannot start (see the table at the top of this file).
- **Never executed here:** 131 of 505 tests (every `[Fact(SkipUnless = ...)]` behind the Docker
  or KMS gate), including `ConcurrentWorkers_NeverClaimSameItem`, the reaper/claim/poison
  suite, all schema tests, the MinIO storage round-trips, the new port-shape sweep's gated
  rows, `Completion_IsScopedToTheClaimant`, and `FullRun_1000Accounts` — the complete loop.
- **The plan to close it:** before Prompt 6 begins, run the full suite on a Docker-capable host
  — the GitHub Actions CI job (has a daemon), a cloud dev box, or any Linux machine with
  `docker compose`. `FullRun_1000Accounts` runs first, watched, per the remediation brief.
  **This is now the highest-priority non-code task in the project**, ahead of any Prompt 6
  feature work, and Prompt 6 does not start until `FullRun_1000Accounts` has executed
  somewhere and passed.

## Deferred by design

These are scheduled, not missing. Listed so the two categories do not get confused.

| Item | Scheduled for |
|---|---|
| `IChainAnchor` is a no-op — chain heads live in the same database as the events they attest | Deferred; ADR-0010 names the February 2027 external audit as the deadline |
| Outbox transport: the relay publishes to a logging sink behind `TODO(transport)` — the pattern (atomic write, at-least-once drain, lag metric) is real; the send swaps in when a consumer exists | When any consumer arrives; ADR-0026 |
| `legal_hold` and `customer_key` exist but are unused by any sweep | Prompt 6 |
| `POST /v1/statements/{id}/restore`, referenced in 409 payloads | Prompt 6 |
| `CustomerKeyService.DestroyCekAsync` throws `NotImplementedException` — crypto-erasure needs retention checks, legal-hold enforcement and an audit record first | Prompt 6 |
| Reconciliation between `statement` rows and object storage | Prompt 6; `statement_content_missing_total` is emitted now so the signal predates the job |

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
Object Lock retention applies at PUT. The orphan sweep is `TODO(prompt6)` in `RenderPipeline` — the
other half of reconciliation CHECK 1.

**"Run PAUSED" reflects one replica's circuit breaker.** Enforcement is distributed (every replica's
claim loop stops on its own breaker), but the status flip is performed by the lease-holding
orchestrator observing its own breaker. A partition that isolates only the orchestrator replica could
mislabel the run. Accepted, with the reasoning and revisit trigger in ADR-0030.
