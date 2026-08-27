# ADR-0008: Route all service traffic through PgBouncer in transaction pooling mode
**Status:** Accepted   **Date:** 2026-08-26

## Context
PostgreSQL forks a several-MB backend process per connection; the practical ceiling is a few hundred. Demand:

```
Delivery.Api        6 replicas x pool 20  =   120
Download.Gateway    4 replicas x pool 20  =    80
Generation.Worker 400 replicas x pool  5  = 2,000   <-- the problem
Retention.Worker    2 replicas x pool  5  =    10
                                            -------
                                              2,210
```

The generation fleet alone takes the database down; pooling is mandatory.

## Options considered
| Option | Pros | Cons |
|---|---|---|
| Raise `max_connections` | No new component | ~20 GB of backends; contention, no headroom |
| App-side pooling only | Built into Npgsql | Replicas cannot coordinate a global ceiling |
| PgBouncer, session mode | Full session semantics | Backend held for the client's life; same ceiling |
| PgBouncer, transaction mode | 2,210 clients become ~100 backends | Session-scoped features break silently |

## Decision
Every service connects to PgBouncer on 6432; only `Db.Migrator` reaches PostgreSQL directly. Compose runs it from day one, so the constraint bites in development, not production.

Separate pools and database users per service: the 400-replica fleet cannot starve customer downloads. Render workers draining a shared pool while the API times out is the month-end incident this prevents.

PgBouncer 1.21+ tracks prepared statements per client and re-prepares on the routed backend, so `max_prepared_statements` is non-zero; without it, disable Npgsql auto-prepare instead. The connection string names which is in force.

Banned in transaction mode; each fails silently and only under load:

| Feature | Why it breaks | Use instead |
|---|---|---|
| `pg_advisory_lock` | Taken on one backend, released on another; leaks | Lease table, or `pg_advisory_xact_lock` |
| `SET` at connect time | Applies to a random backend | `SET LOCAL` in-transaction |
| `LISTEN`/`NOTIFY` | Subscription lives on a backend you lose | Polling or a real broker |
| `WITH HOLD` cursors | Die with the transaction | Keyset pagination |
| Temp tables across statements | Next statement may land elsewhere | CTEs or real tables |

## Consequences
Leader election for Retention.Worker and partition maintenance uses the `distributed_lease` table plus a monotonic fence token, not `pg_advisory_lock` — this supersedes the usual advisory-lock recipe.

## Revisit when
- `cl_waiting` on the delivery pool stays above zero for 60s at peak (~30 req/sec).
- Generation exceeds 400 replicas or pool 5, pushing past ~3,000 clients and PgBouncer's single-threaded CPU limit.
- PostgreSQL ships a GA built-in pooler or non-forking backend.
- A required capability needs session state, forcing a second session-mode pool.
