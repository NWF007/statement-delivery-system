# ADR-0035: Crypto-erasure is scheduled seven days out, never executed immediately

**Status:** Accepted · **Date:** 2026-08-31

## Context

Crypto-erasure is the one operation in this system that cannot be undone by any means. There is
no backup that helps, because every backup holds ciphertext under the key being destroyed. A
mistaken API call — wrong customer id, a mis-filed DSR, a compromised DPO token — would destroy
a customer's records forever, instantly.

## Decision

**Irreversible operations get a reversal window.** `POST /v1/customers/{id}/erasure` schedules,
never executes:

1. `customer_key.status = SCHEDULED_DESTRUCTION`, `destruction_due_at = now() + 7 days`
   (configurable, `Retention:ErasureCoolingOffDays`), one transaction with the
   `erasure_request` row and the `ERASURE_SCHEDULED` audit.
2. `DELETE /v1/customers/{id}/erasure` cancels while the window is open — the key returns to
   ACTIVE, the request row survives as CANCELLED (the sequence "requested, cancelled" is itself
   evidence).
3. A daily executor destroys keys whose windows have closed — after **re-evaluating** the legal
   position through the decision engine, because a legal hold may have been placed during the
   window and the decision made seven days ago is not trusted
   (`Erasure_ReEvaluatesConflicts_AtExecutionTime`).

Execution order inside the executor: **destroy the key first, then the bookkeeping.** A crash
between them leaves a destroyed key with statements still marked AVAILABLE — those downloads
already fail closed (`CryptoErasedException`), and the next pass finishes idempotently. The
reverse order could mark a customer erased while their key still exists, which is a lie with a
seven-year audit trail.

The destruction itself nulls `wrapped_cek` and then runs `VACUUM customer_key` (app_retention
holds PostgreSQL 17's MAINTAIN privilege for exactly this): MVCC keeps the old row version —
key material included — on the page until vacuumed, so a NULL update alone has not removed the
bytes from disk. Honest limit: freed space is reused, not zeroed, and filesystem journals are
beyond SQL's reach; full-disk encryption is the defence at those layers.

## Consequences

- "A mistaken API call destroys records forever" becomes "a mistaken API call gets noticed and
  cancelled within a week."
- Erasure latency is seven days by design. POPIA does not require instantaneous erasure; it
  requires erasure, demonstrably done — which the audit trail and the reconciliation job's
  check 6 provide.

## Revisit when

- **A regulator sets a hard erasure deadline** shorter than the window: shrink the
  configuration, not the mechanism.
- **Erasure volume grows** beyond the executor's daily batch bound: raise the bound before
  shortening the window.
