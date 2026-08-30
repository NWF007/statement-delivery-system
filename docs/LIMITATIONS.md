# Limitations

What this system does not yet do, and what has not yet been proven about it. Kept separate from the
ADRs: an ADR records a decision, this records a gap.

## Test coverage that has never executed

**As of 2026-08-30, on commit `41048ce` plus the Prompts 1–4 audit remediation.**

The suite is 451 tests, 0 failed. **105 of them have never run on the development host**, because they
need a container runtime that host cannot provide:

| Gate | Count | Why |
|---|---|---|
| `DockerAvailability.SkipReason` | 101 | Docker Desktop is installed but its Linux engine cannot start: WSL is not installed and the Hyper-V `vmcompute` service does not exist. This is a nested-virtualisation guest, so enabling either is not a quick fix. `docker info` returns HTTP 500. |
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

### The obligation

**Before the Prompt 7 fresh-clone gate, the full suite must execute somewhere** — a working Docker
host or a CI job with a daemon. A submission whose schema tests have never run is not one worth
defending, and the two highest-value tests in the repository are both in the unexecuted set.

`HIGH 2` from the Prompts 1–4 audit is the concrete argument for this. It was a replication-routing
bug on the redemption hot path that would have presented as a ciphertext-integrity alert, and a real
concurrent-redemption test against a lagging replica would have caught it. Nothing static did.

## Deferred by design

These are scheduled, not missing. Listed so the two categories do not get confused.

| Item | Scheduled for |
|---|---|
| `IChainAnchor` is a no-op — chain heads live in the same database as the events they attest | Deferred; ADR-0010 names the February 2027 external audit as the deadline |
| PDF generation and `statement_run` execution | Prompt 5 |
| Outbox rows accumulate with no relay | Prompt 5 |
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
