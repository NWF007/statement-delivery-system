# ADR-0034: The purge deletes storage first, then marks the row

**Status:** Accepted · **Date:** 2026-08-31

## Context

The purge is two effects in two systems — delete the object versions in S3, mark the row PURGED
in PostgreSQL — and no transaction spans them. The worker can crash between the two. The
ordering decides what the crash costs.

## Decision

```
1. Delete all object versions   (idempotent — a missing object is success)
2. UPDATE statement SET status='PURGED', purged_at=now(),
                        storage_key=NULL, wrapped_dek=NULL, iv=NULL, auth_tag=NULL
3. INSERT audit_event RETENTION_PURGED (with the deleted version ids) — same transaction as 2
```

Work the crash through both orders:

- **Delete succeeds, mark fails** → the retry re-deletes (a no-op, because a missing object is
  success by contract) and marks. Benign, self-healing, no human involved.
- **Mark succeeds, delete fails** → a paid-for object exists that no database row points at: an
  orphan, possibly still under a Compliance lock — unreachable, undeletable, and invisible until
  the orphan sweep trips over it.

The first ordering's failure mode is recoverable by doing nothing; the second's leaks money and
bytes. So: storage first. `Purge_CrashAfterDelete_IsSafeToRetry` pins the recovery.

The metadata survives on purpose (ADR-0036), and the audit entry carries the deleted version
ids: the proof of *what* went, alongside the row's proof of *that* it went.

## Consequences

- Purge is idempotent end to end; the crash-and-retry path is the same code as the happy path.
- `DeleteObjectVersionsAsync` must keep its missing-object-is-success contract — it is
  documented on the port and is what the ordering's safety rests on.

## Revisit when

- **Deletion latency starts to matter** (millions eligible per day): batching deletes across
  statements changes the crash-point analysis and this ADR must be redone, not assumed.
