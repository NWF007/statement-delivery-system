# ADR-0037: Legal holds live in two layers, and storage goes first

**Status:** Accepted · **Date:** 2026-08-31

## Context

A legal hold is a promise to a court. Enforcing it with only a database row means one purge-
worker bug deletes held evidence; enforcing it with only an object-store flag means the policy
engine, the audit trail and the API have nothing to read.

## Decision

Both layers, always:

1. **The object-store legal hold** (`PutObjectLegalHold`, status On) — physical enforcement.
   S3 legal holds are independent of retention: no expiry, they remain until explicitly removed,
   and they stack with Compliance-mode retention — an object under both stays locked until the
   hold is lifted *and* the retention expires.
2. **The `legal_hold` row** — the system of record: case reference (mandatory — a hold nobody
   can trace to a matter is a hold nobody will ever dare release), who placed it, why, and the
   release record when it ends.

**Placement order: storage first, database second.** Work the crash through both orders —
storage hold set but DB insert failed leaves an over-protected object that reconciliation
reports and a human releases (harmless); DB inserted but storage hold failed leaves the system
*believing* an object is held that is physically deletable — the dangerous direction, because
every read of the policy layer now reports protection that does not exist. Release inverts the
order (database first) for the same reason. In both flows the failure mode is "over-protected
and visible", never "unprotected and invisible".

**Customer-scoped holds cover future statements** because hold status is checked at *decision
time* (purge, erasure), never at generation time — a statement generated after the hold was
placed is covered by the same customer-scoped row
(`CustomerHold_AppliesToStatementsGeneratedAfterPlacement`).

**Drift is inevitable and detected, never auto-repaired.** The two layers can diverge — S3
unavailable mid-placement, a partial failure in the customer-scope loop, manual intervention.
Reconciliation check 3 compares DB-active holds against store holds and reports any mismatch;
the purge pass treats a store-side hold with no DB record as a blocking drift with a synthetic
case reference (`OBJECT-STORE-HOLD-NO-DB-RECORD`) in the audit. A divergence between a legal
record and a physical control is something a human must look at; automatic repair would just
pick a side silently — ADR-0033's forbidden move.

## Consequences

- The delivery API (which hosts the staff endpoints, per V017's precedent) now holds storage
  credentials scoped, in production IAM, to Get/PutObjectLegalHold — and nothing else; it still
  cannot read or write statement content.
- A mistake in either mechanism is caught by the other. That redundancy is the feature, not
  overhead.

## Revisit when

- **Hold volume grows** past the reconciliation sample size: raise the sample or move the drift
  check to an S3-Inventory diff.
- **A second physical store arrives** (multi-region): each replica's hold state joins check 3.
