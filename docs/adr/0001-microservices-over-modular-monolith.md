# ADR-0001: Split generation and delivery into separate deployables
**Status:** Accepted   **Date:** 2026-08-26

## Context
Generation is a monthly burst: ~30 million PDFs at ~1,400 renders/sec, async, resumable, silently retried, throughput-optimised. Delivery is steady at ~0.5 req/sec (~30 peak), synchronous, sub-second, user-visible on failure, correctness- and audit-optimised.

Microservices are not the default; for most systems this size a modular monolith is correct, and I would have chosen one had the load been uniform. The divergent scaling profile alone earns this split.

## Options considered

| Option | Pros | Cons |
| --- | --- | --- |
| Modular monolith | One pipeline; in-process transactions; easy local testing | API fleet sized for month-end batch; a render leak kills the download path; delete rights beside serving code |
| API plus one worker | Separates burst from steady load | Public gateway shares capacity with the authenticated API; purge shares identity with generation |
| Four services (chosen) | Independent scaling, failure and authorisation per deployable | Four pipelines; tracing mandatory; shared schema |

## Decision
- **Delivery.Api** — authenticated, interactive, customer-facing; scales with app traffic, not batch.
- **Download.Gateway** — unauthenticated by design; the token is the credential. Different threat exposure, different auth model; a DDoS here cannot exhaust the authenticated API.
- **Generation.Worker** — 0 to 400 replicas for hours a month; coupled to the API, that burst sets the API fleet's floor all month.
- **Retention.Worker** — sole holder of DELETE rights: separate deployment, identity and approval path. Code that deletes statements must not share a process with code that serves them.

## Consequences
- Distributed tracing becomes mandatory across four services.
- Integration testing spans several processes plus PostgreSQL, MinIO and Redis.
- Four deployment pipelines, dashboard sets and runbooks.
- Eventual consistency: a PDF exists in storage before it is catalogued.
- Shared-database coupling is the residual risk: one migration can break three services. Mitigated by additive migrations and per-service roles.

## Revisit when
- Peak delivery sustains above ~500 req/sec — the two public services then need separate data stores, not just processes.
- Statement production moves from month-end burst to continuous intraday; reconsider merging the workers.
- A schema change forces a coordinated release of three or more services twice in a quarter — split the database, not the services.
- A purge run exceeds 24 hours and must shard concurrently with generation.
