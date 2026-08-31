# ADR-0038: The archive tier is simulated locally, and labelled as a simulation

> Numbering note: the Prompt 6 brief calls this ADR-0034; the sequence continues from 0032.

**Status:** Accepted · **Date:** 2026-08-31

## Context

MinIO has no Glacier-equivalent tier with genuine restore latency. Pretending otherwise —
copying objects between buckets and calling it archival — would demo well and mislead everyone
who read the code. A simulation that is labelled as a simulation is fine; a simulation presented
as the real thing is not.

## What is REAL in local development

- The database lifecycle: `status = ARCHIVED`, `storage_tier = GLACIER` (V006's tier vocabulary;
  the brief's "COLD" maps to it), audited `STATEMENT_ARCHIVED`, all driven by the daily
  leader-elected archive pass.
- The asynchronous restore contract: `POST .../restore` answers 202 with an estimate; a worker
  job completes the request; `statement.restored` publishes through the transactional outbox;
  restored copies **expire** (`Retention:RestoredCopyHours`), after which the statement is cold
  again — the temporary-copy shape real Glacier restores have.
- The download behaviour: an ARCHIVED statement 409s (with the restore endpoint in the payload)
  unless an unexpired completed restore exists, in which case it serves.

## What is SIMULATED, and how the real thing differs

- **No object moves, locally.** MinIO keeps the bytes exactly where they were; only the
  database's view changes. This is closer to reality than a bucket-copy would be: an S3
  lifecycle transition changes the storage class *in place* — key unchanged, Object Lock
  preserved — and a copy would break both.
- **Restore latency is a configured delay** (`Retention:Restore:SimulatedDelayMinutes`, minutes
  locally because a demo cannot wait four hours). In production the delay is the storage
  class's real retrieval time.
- **In production**: an S3 lifecycle rule transitions objects to Glacier Instant Retrieval (or
  Flexible Retrieval for the oldest cohorts) after N months; `RestoreObject` initiates
  retrieval; the completion is detected by polling `HeadObject` for the `x-amz-restore` marker
  or by consuming the S3 restore-completed event. The `RestoreCompleter` job is where that
  polling slots in — the database contract on either side of it is identical in both worlds,
  which is what makes the simulation honest.

## Consequences

- Local acceptance runs exercise every state transition, audit event and API behaviour the real
  tier produces, with none of the latency.
- The gap is confined to one worker job's trigger condition (timer elapsed vs. restore marker
  present), documented here and at the `RestoreCompleter`.

## Revisit when

- **Deploying against real S3**: replace the timer trigger with the `x-amz-restore` poll, add
  the lifecycle rule to infrastructure, and delete nothing — the contract stays.
