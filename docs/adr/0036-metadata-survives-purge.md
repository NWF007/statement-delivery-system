# ADR-0036: The statement row survives its own purge

> Numbering note: the Prompt 6 brief calls this ADR-0032; the sequence continues from 0032.

**Status:** Accepted · **Date:** 2026-08-31

## Context

When retention expires and the bytes are deleted, what happens to the row? Deleting it feels
like finishing the job. It is actually destroying the evidence that the job was done.

## Decision

The row stays, permanently: `status = PURGED`, `purged_at` set, the audit trail intact. What
goes is the pointers and the crypto material — `storage_key`, `wrapped_dek`, `iv`, `auth_tag`
are nulled (V006's `ck_statement_purged_has_no_storage` enforces the shape). `content_sha256`
deliberately survives: a digest is not key material, and it is the one artefact that can later
prove *which* bytes were destroyed.

You must be able to demonstrate **that** you deleted, **when**, and **under what authority** —
the accountability condition. A vanished row proves nothing: it is indistinguishable from a row
that never existed, or one an attacker removed. Erasing the evidence of an erasure defeats the
point of doing it lawfully.

The same rule extends across the lifecycle:

- A released legal hold keeps its row (released_at set, never deleted) — the record that data
  was preserved, by whom, for how long.
- A cancelled or completed erasure request keeps its row.
- `storage_tombstone` records every key the statement table stops referencing, and why —
  which is also what lets the orphan sweep tell a leaked object from a lawful erasure remnant.

One named tension, resolved and cited rather than hidden: POPIA requires erasure of personal
information, and the audit trail contains the customer's identifier. Retaining it is justified
by the accountability condition — you cannot demonstrate lawful erasure without a record of it.
The trail records THAT statements existed and what was done to them; the content those events
described is unrecoverable.

## Consequences

- PURGED rows accumulate for the life of the system. They are small (no blobs, no keys) and
  they are the cheapest audit defence the system has.
- The customer list shows PURGED statements with their status and no download offered — "this
  existed and was destroyed" is a fact the customer is entitled to see (410, not 404, on a
  download attempt).

## Revisit when

- **Row volume becomes a cost** at the 2.5-billion scale: PURGED rows can move to a cheaper
  archive table — moved, never dropped.
