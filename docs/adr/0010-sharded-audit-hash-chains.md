# ADR-0010: Shard the audit hash chain across sixteen independent chains
**Status:** Accepted   **Date:** 2026-08-27

## Context
Each record's hash covers its predecessor's, so appends serialise: `FOR UPDATE` on a chain head, then a sequence number. Under transaction pooling (ADR-0008) that lock spans the transaction. At ~470M events/year with month-end peaks, one global chain is one hot row in front of every write path - 400 Generation.Worker replicas and every delivery request queue behind it.

## Options considered
| Option | Pros | Cons |
| --- | --- | --- |
| One global chain | Total order; one verification walk | One serialised row caps every write |
| One chain per entity | No contention; trivial per-statement proof | 2.5B chain heads; unverifiable at scale |
| No chain (WAL, backups) | No write cost | Not tamper-evident |
| Sixteen sharded chains | Contention split sixteen ways; proof intact | No proven order across chains |

## Decision
Sixteen chains, N configurable. Chain = `FNV-1a(big-endian subject-id bytes) mod N` - statement id, else customer id, else a constant. Not round-robin: every event about a statement lands in one chain, so verification is a single-chain walk, not a sixteen-way merge, and a gap reads as a chain break, not a smear across sixteen chains that still verify. Not `Guid.GetHashCode()`: unstable across processes, so a statement changes chain after a restart and fragments.

Chain `c` seeds from `SHA256(UTF8("statement-delivery:audit:chain:" + c))`, not zeros: under a shared genesis, record 1 of chain 3 and record 1 of chain 7 cover the same predecessor, so a record replays from one into the other and still verifies.

## Consequences
Given up: ordering across chains. A global timeline is a sixteen-way merge on `occurred_at` - wall-clock order, not proven order.

Kept: within a chain, nothing is altered or deleted undetected - *given a trusted terminal hash*. But the heads live in the same database as the events: anyone able to rewrite both produces a self-consistent forgery, and tail truncation is invisible from inside. Closing that needs terminal hashes anchored outside PostgreSQL: append-only object storage under Object Lock, or a separately credentialed account. `IChainAnchor` ships as a no-op with `TODO(security)`; the real one is deferred. Residual: every writing role holds SELECT and UPDATE on `audit_chain_head`, which a `SECURITY DEFINER audit_append()` would remove.

## Revisit when
- p99 head-lock wait exceeds 25 ms in a month-end burst, or one chain sustains >150 appends/sec.
- `IChainAnchor` is still the no-op at the February 2027 external audit.
- Any chain holds >12% of a month's events against an expected 6.25%.
