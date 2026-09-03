# ADR-0039: The orphan sweep reports and never deletes — permanently

**Status:** Accepted · **Date:** 2026-08-31

## Context

Write failures leak objects: a PUT that succeeded whose transaction then rolled back, a crash
between upload and MarkAvailable. Those objects cost money forever and nothing references them.
The obvious fix — enumerate storage, diff against the database, delete the difference — is
precisely the kind of job that destroys real data when the comparison logic has a bug.

## Decision

**Report-only, permanently.** The weekly sweep walks shard prefixes with a resumable cursor
(`orphan_sweep_state`), classifies each key against the statement table and the
`storage_tombstone` ledger, writes findings to `orphan_report`, and emits
`storage_orphan_total` / `storage_orphan_bytes`. It deletes nothing, and "add deletion later"
is explicitly out of scope, twice over:

1. Objects under a Compliance lock cannot be deleted anyway — an automatic deleter would spend
   its life collecting AccessDenied.
2. Inventory-driven automatic deletion converts a comparison bug into permanent data loss. The
   report converts the same bug into a wrong number a human questions.

The tombstone ledger is what makes classification honest: an object whose key is tombstoned
ERASED is a lawful crypto-erasure remnant (the system working as designed), not a finding; a
key tombstoned PURGED that still exists means a delete silently failed — reported.

**Never the whole bucket in one pass.** At 2.5 billion objects, full enumeration is S3
Inventory's job in production — a daily manifest diffed offline. Locally the sweep walks the
256 `statements/xx/` shard prefixes a bounded number of pages per tick, cursor persisted, so a
restart resumes rather than starting over.

## The quantified exposure

At a 0.01% write-failure rate against 30 million statements a month: ~3,000 orphans/month at
~200 KB each ≈ **600 MB/month** of unreclaimable storage, ≈ **$0.30/month** at Glacier IR
rates (see docs/SCALE.md). Small — but a number is worth more than a shrug, and the metric
existing is what keeps it a number.

## Consequences

- Orphans accumulate until a human acts on the report (post-lock-expiry deletion is a manual,
  reviewed operation, out of scope by design).
- The sweep's read load is bounded and tunable (`Retention:OrphanPagesPerTick`).

## Revisit when

- **The orphan rate departs from the estimate** — a rising `storage_orphan_total` is a write-
  path bug to fix at the source, not a cleanup problem.
- **Production adoption of S3 Inventory** replaces the local listing walk; the classification
  and the report stay.
