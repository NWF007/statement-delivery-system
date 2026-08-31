# ADR-0026: A PostgreSQL queue over a message broker

**Status:** Accepted · **Date:** 2026-08-30

> Numbering note: the Prompt 5 brief called this ADR-0024. That number (and 0025) was taken by
> the audit-remediation ADRs; the Prompt 5 set ships as 0026–0030.

## Context

The batch subsystem fans 30 million render items out to up to 400 workers. Something must hand
each item to exactly one worker, survive worker crashes, and resume mid-run. The reflex answer is
a broker — Kafka, RabbitMQ, SQS. The compose stack has none, and this prompt does not add one.

## The arithmetic

30 million items over a 6-hour window is **~1,400 claims/second**. Batch-claiming 50 items per
round trip reduces that to **~28 claim queries/second** — a rate PostgreSQL with
`FOR UPDATE SKIP LOCKED` handles without noticing, on a table whose competing writers are the
claims themselves. The queue's entire hot set (the unclaimed tail plus in-flight claims) lives in
one partial index.

Adding a broker to serve 28 queries/second would introduce: a second stateful system to run,
back up, and monitor; a second delivery semantic to reason about (the DB transaction can no
longer atomically cover "claimed" and "rendered"); and a class of failure — broker/DB divergence
— that the single-store design cannot express. The exactly-once-ish behaviour this workload needs
falls out of row locks for free.

## Decision

`statement_run_item` **is** the queue. Claims are `FOR UPDATE SKIP LOCKED` batches of 50
(ADR-0027 covers the attempt semantics); completion is an UPDATE in the same transaction as the
statement row it produced. The outbox (V004) remains the integration-event channel, with the
relay publishing to a logging sink behind `TODO(transport)` — the pattern is proven, the
transport is swappable, and standing up infrastructure nothing consumes would be theatre.

## Options considered

| Option | Why not |
|---|---|
| Kafka | A distributed log is the right tool at 100× this claim rate or with cross-service consumers. Today it is a second stateful system solving a solved problem. |
| RabbitMQ/SQS | Same operational tax, plus the claim-and-complete atomicity with the statement row is lost — a broker ack and a DB commit are a dual write. |
| LISTEN/NOTIFY + table | NOTIFY does not survive PgBouncer transaction pooling (the subscription lives on a backend the client no longer owns). Polling the claim query is simpler and pool-safe. |

## Revisit when

- Claim latency p99 exceeds **50 ms**, or `pg_stat_activity` shows lock contention on
  `statement_run_item` under load.
- Work must fan out to consumers in **other services** — the moment a second service consumes
  render work, the queue stops being an implementation detail of one worker and a broker's
  decoupling starts paying rent.
- The outbox relay's 500 ms polling is outgrown: above roughly **10,000 events/second**, Debezium
  CDC becomes justified, at the cost of managing connector state and schema evolution.
