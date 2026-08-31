# ADR-0030: Pause the run when the ledger circuit opens — never fail it

**Status:** Accepted · **Date:** 2026-08-30

## Context

At 30 million items, a run is hours of paid work. The ledger — the one hard external dependency
per item — will have bad minutes at month-end, which is exactly when the run executes. The
circuit breaker (50% failure over 20 requests, 30s break) exists so the fleet stops hammering a
struggling dependency; the question is what the RUN does while the breaker is open.

Failing the run answers a transient outage with the loss of every completed item's bookkeeping —
and, worse, invites the operator reflex of "just restart it", which re-plans and re-walks
millions of DONE rows to rediscover the work already finished. A five-minute ledger wobble must
cost five minutes, not the run.

## Decision

**Pause, monitor, resume.** Two mechanisms at two scopes, honestly distinct:

1. **The enforcement is per-replica and local.** Each replica's claim loop checks its own
   breaker (`CircuitBreakerStateProvider`) and simply stops claiming while it is open. No claims
   → no attempts burned against a dead dependency → no items quarantined by an outage that was
   nobody's poison. In-flight items ride their retry budget (with jitter — 360 workers that fail
   together must not retry together).
2. **The run status is operator signalling, flipped by the orchestrator.** The lease-holding
   replica observes ITS breaker and transitions RUNNING → PAUSED (emitting
   `generation_run_paused_total{reason="ledger_circuit_open"}` and an alert), then PAUSED →
   RUNNING when the breaker closes, clearing the throughput window so the deadline projection
   restarts clean rather than averaging across the outage.

The honesty note: breakers are per-process, so "the run is paused" is the orchestrator replica's
view of the ledger, not a fleet consensus. That is acceptable because the *enforcement* is
already distributed — every replica protects itself — and the status row exists for humans, who
need one answer, not four hundred.

Nothing in a pause touches completed work. DONE rows stay done; the queue holds its position;
resume is the absence of the pause.

## Options considered

| Option | Why not |
|---|---|
| Fail the run | Throws away bookkeeping and invites destructive restarts. An outage becomes an incident. |
| Keep claiming, let items fail | Three ledger-down ticks per item quarantines the entire queue; the "failures" endpoint fills with 30M rows that were never poison. |
| Fleet-consensus pause (shared breaker state in Redis) | Distributed state to solve a problem local enforcement already solves; the shared breaker becomes its own dependency with its own failure modes. |

## Revisit when

- Pauses flap (open/close cycling visible in `generation_run_paused_total`) — lengthen the break
  or add hysteresis to the orchestrator's resume, not to the per-replica enforcement.
- A second external dependency joins the per-item path (e.g. a real KMS call per statement) —
  each needs its own breaker and its own pause reason; one shared reason hides which dependency
  is down.
- The orchestrator's single-replica view proves misleading in practice (its replica's network
  partitioned while the fleet was fine): consider quorum signalling then, with the cost stated.
