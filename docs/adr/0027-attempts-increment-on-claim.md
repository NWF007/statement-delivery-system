# ADR-0027: Attempts increment on claim, not on completion

**Status:** Accepted · **Date:** 2026-08-30

## Context

Every queue with retries needs a poison-item ceiling: an item that fails N times is quarantined
so one unrenderable account cannot stall thirty million others. The ceiling needs a counter, and
the counter can be incremented in one of two places — when a worker **takes** the item, or when
the attempt **finishes** (in the failure handler).

The failure-handler version reads more naturally: "the attempt failed, count it." It is also
wrong in a way that only shows up in production.

## Why completion-time increment loops forever

The failure handler runs only when the worker **survives** the failure. The poison items that
matter most are precisely the ones that kill the worker: a payload that OOMs the renderer, a
document that segfaults the PDF library's native code, a corrupted account that wedges a thread
until the pod is SIGKILLed. In every one of those, the process dies before any completion code
runs. The counter never moves. The reaper returns the item to QUEUED — pristine, at attempt zero
forever — and the next worker picks it up and dies the same way. One poison item consumes worker
after worker, indefinitely, while the run's progress numbers look merely slow.

## Decision

`attempts` increments **inside the claim UPDATE itself** — the same statement that flips the row
to RENDERING (`SET attempts = attempts + 1`). The attempt is burned the moment the item is
handed over, before a single line of render code runs. Consequences that make the rest of the
design work:

- **The reaper resets status and claim columns but never attempts.** An item whose workers keep
  dying arrives back at QUEUED with attempts 1, then 2, then 3 — and the claim query's
  `attempts < @maxAttempts` quarantines it by exclusion. The reaper needs no knowledge of why
  the claim went stale.
- **Graceful shutdown releases claims without touching attempts.** The claim happened; the
  attempt is honestly spent. A replica bounced during a deploy costs each of its claimed items
  one attempt — the price of not being able to distinguish "deploy" from "crash" from inside
  the database, and deliberately paid.
- **Operator retry resets attempts to zero** (`POST .../failures/retry`) — a human judgement
  that the underlying cause is fixed, granting a fresh round rather than one more try.

## Options considered

| Option | Why not |
|---|---|
| Increment in the failure handler | Loops forever on worker-killing items — see above. |
| Increment in both places | Double-counts the survivable failures, so transient ledger blips quarantine items at half the intended ceiling. |
| Heartbeat + supervisor counts deaths | Rebuilds the claim counter as distributed state with its own failure modes; the row already is the state. |

## Revisit when

- Deploy-time claim releases burn enough attempts to quarantine healthy items (visible as
  `generation_item_failed_total` spiking with reason=released after rollouts) — the fix is a
  distinct released-not-failed state, not moving the increment.
- Max attempts changes from 3: the ceiling and the stale-claim window together bound how long a
  poison item can occupy workers; re-derive both, not one.
