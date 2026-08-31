# Architecture Decision Records

Forty-two decisions, grouped by theme. Every ADR carries a **Revisit when** section — the
conditions under which the decision should be reopened — so none of them is a monument.

## Platform and delivery

| ADR | Decision |
| --- | --- |
| [0000](0000-record-architecture-decisions.md) | Record architecture decisions |
| [0001](0001-microservices-over-modular-monolith.md) | Split generation and delivery — opposite scaling profiles |
| [0002](0002-dapper-and-dbup-over-ef-core.md) | Dapper + DbUp over EF Core |
| [0003](0003-built-in-logging-over-serilog.md) | Built-in logging with the OTel exporter |
| [0004](0004-aspire-dashboard-without-apphost.md) | Aspire Dashboard container without an AppHost |
| [0014](0014-no-idempotency-replay-on-link-issue.md) | No idempotency replay on link issue — a new link every time, on purpose |
| [0015](0015-unauthenticated-redemption-endpoint.md) | The redemption endpoint is unauthenticated — the token is the credential |
| [0016](0016-no-range-request-support.md) | No HTTP Range support — resumability loses to single-use |
| [0017](0017-consume-before-stream.md) | Consume before stream — attempted access is what the audit must capture |
| [0018](0018-per-ip-not-per-token-rate-limiting.md) | Rate limit per address, not per token |

## Data

| ADR | Decision |
| --- | --- |
| [0005](0005-partition-not-shard.md) | Partition by range, do not shard — with the arithmetic |
| [0006](0006-uuidv7-primary-keys.md) | Application-generated UUIDv7 primary keys (and why tokens are NOT UUIDs) |
| [0007](0007-partitioning-strategy.md) | Range-partition on time; bounded date ranges are mandatory |
| [0008](0008-pgbouncer-transaction-pooling.md) | Everything through PgBouncer, in transaction mode — and the trap list |
| [0009](0009-denormalised-customer-id-on-statement.md) | Denormalise the owner onto statement for hot-path authorisation |
| [0013](0013-mandatory-date-range-on-statement-queries.md) | The mandatory date range — unbounded reads are unrepresentable |
| [0023](0023-high-cardinality-storage-key-prefix.md) | A hashed shard leads the storage key |
| [0026](0026-postgres-queue-over-message-broker.md) | The generation queue is PostgreSQL (SKIP LOCKED), not a broker |
| [0027](0027-attempts-increment-on-claim.md) | Attempts increment on claim, not completion |

## Crypto and audit

| ADR | Decision |
| --- | --- |
| [0010](0010-sharded-audit-hash-chains.md) | Sixteen sharded audit hash chains — and the anchoring limit, stated |
| [0011](0011-canonical-serialisation-for-hashing.md) | Canonical serialisation for hashing |
| [0012](0012-404-not-403-for-unowned-resources.md) | 404, never 403, for unowned resources |
| [0019](0019-framed-aead-over-one-shot-gcm.md) | Framed AEAD over one-shot GCM — streaming in O(1) memory |
| [0020](0020-three-tier-key-hierarchy.md) | The three-tier key hierarchy — the $26M/month arithmetic |
| [0021](0021-envelope-encryption-over-sse-kms.md) | Envelope encryption over SSE-KMS — crypto-erasure decided it |
| [0022](0022-object-lock-compliance-mode.md) | Object Lock COMPLIANCE mode; GOVERNANCE refused outside Development |
| [0024](0024-security-gating-reads-run-in-the-callers-transaction.md) | Security-gating reads run in the caller's transaction |
| [0025](0025-audit-events-bind-to-the-transaction-they-describe.md) | Audit events bind to the transaction they describe |

## Generation

| ADR | Decision |
| --- | --- |
| [0028](0028-questpdf-licensing-position.md) | The QuestPDF licensing position |
| [0029](0029-deterministic-pdf-rendering.md) | Byte-deterministic PDF rendering, proven across processes |
| [0030](0030-pause-not-fail-on-circuit-open.md) | Pause the run when the ledger circuit opens; never fail it |
| [0031](0031-spool-ciphertext-for-content-length.md) | Spool ciphertext so every PUT declares a Content-Length |

## Compliance lifecycle

| ADR | Decision |
| --- | --- |
| [0033](0033-legal-conflict-surfaced-not-resolved.md) | Legal conflicts surfaced with their basis, never resolved in code |
| [0034](0034-delete-storage-before-marking-purged.md) | Purge deletes storage first, then marks the row |
| [0035](0035-cooling-off-period-on-erasure.md) | Crypto-erasure schedules seven days out, re-checked at execution |
| [0036](0036-metadata-survives-purge.md) | The statement row survives its own purge |
| [0037](0037-dual-layer-legal-hold.md) | Legal holds in the database AND the object store; storage first |
| [0038](0038-archive-tier-simulation-in-local-dev.md) | The archive tier is simulated locally — and labelled as such |
| [0039](0039-orphan-sweep-reports-does-not-delete.md) | The orphan sweep reports and never deletes |
| [0040](0040-legal-hold-carries-customer-id.md) | Every legal hold row carries its customer — the erasure gate's data model |

## Engineering discipline

| ADR | Decision |
| --- | --- |
| [0032](0032-port-contracts-tested-with-production-shapes.md) | Ports are tested with the awkward shapes production produces |
| [0041](0041-context-builders-are-enumerated.md) | Adapters feeding exhaustively-tested logic are exhaustively tested too |
