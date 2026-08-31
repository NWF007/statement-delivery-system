# Threat model

> This is a heading skeleton. The system currently implements no business logic, so this
> file records the *shape* of the analysis rather than its conclusions, and each section is
> filled in as the corresponding control lands. No control may be marked implemented here
> without a test that proves it.

## Scope and trust boundaries

*A boundary diagram belongs here, showing each component, the data crossing between them,
and where trust changes hands.* The boundaries below already exist in the scaffold:

- Public internet to the unauthenticated `Download.Gateway`.
- Authenticated callers to `Delivery.Api`, behind JWT bearer authentication.
- Service to database, through PgBouncer, with a distinct least-privilege database role per
  service.
- Service to the object store holding statement content.
- `Retention.Worker` as the only component that holds DELETE rights.

## Assets

*What is worth protecting, in rough order of blast radius:*

- Statement PDFs and their contents.
- Download tokens.
- The integrity of the audit trail.
- Encryption keys.
- Customer identity data.

## Methodology

*STRIDE applied per trust boundary is the intended method.* Every identified threat gets a
linked mitigation and a test that demonstrates the mitigation holds; a threat with no test is
an open threat.

## Threats

*One row per identified threat. Status is `open`, `mitigated`, or `accepted`, and may only
read `mitigated` when the Test column names a real, passing test.*

| ID | Boundary | STRIDE | Threat | Mitigation | Status | Test |
| --- | --- | --- | --- | --- | --- | --- |
| T-01 | Public gateway | Spoofing | A stolen or guessed download link replayed by an attacker | Single-use tokens, 43-char CSPRNG, SHA-256 at rest, atomic consume - exactly one redemption ever wins (ADR-0013/0017) | mitigated | `ConcurrentRedemption_ExactlyOneSucceeds` |
| T-02 | Public gateway | Info disclosure | Denial responses used as an oracle (valid vs invalid vs expired vs consumed) | One uniform 404 for every validation failure, 50 ms timing floor; real reason lives only in the audit trail | mitigated | `DeniedAccess_IsAudited_WithAnInternalReasonThatIsNeverReturned` |
| T-03 | Customer API | Elevation | IDOR - one customer reading another's statements by id | Ownership is a WHERE-clause predicate on the JWT `sub`, never a post-load check; unowned == nonexistent (404, ADR-0012) | mitigated | `GetStatement_OwnedByAnother_Returns404` |
| T-04 | Customer API | Info disclosure | Storage keys / crypto envelope leaking through API responses | Response contracts enumerate exactly the agreed fields; leak-probe tests assert the forbidden names and values never serialise | mitigated | `StatementResponse_NeverContainsStorageKeyOrCryptoFields` |
| T-05 | Object storage | Tampering | Stored ciphertext modified, truncated or substituted | SDP1 framed AEAD: per-frame tags, AAD binds statement/customer/version, digest re-checked on read | mitigated | `Truncate_DropFinalFrame_IsDetected`, tamper suite |
| T-06 | Object storage | Repudiation/Tampering | Retention bypassed by deleting objects early | S3 Object Lock COMPLIANCE mode - no principal can delete inside the window; GOVERNANCE refused outside Development at startup | mitigated | `Startup_WithoutObjectLock_FailsReadiness` |
| T-07 | Database | Tampering | Audit history rewritten to hide access | Insert-only grants (no role holds UPDATE/DELETE), tamper trigger, 16 sharded hash chains re-verifiable end to end | mitigated | `ConcurrentWritersSameChain_ProduceValidChain`, `/v1/audit/verify` |
| T-08 | Database | Repudiation | A privileged DB actor forges a SELF-CONSISTENT chain (rewrite events AND heads) | NOT closed: chain heads live beside events. External anchoring is the fix (`IChainAnchor`, no-op today) | **accepted, open** | documented in LIMITATIONS.md |
| T-09 | Logs/telemetry | Info disclosure | Token plaintext, key material or statement content in logs, traces or metric labels | Central redactor covers every sensitive type; metric labels are closed sets; console output scanned | mitigated | `TokenPlaintext_NeverAppearsInAnyLogOrTraceOutput`, `TokenPlaintext_CanNeverBecomeAMetricLabel` |
| T-10 | Key hierarchy | Info disclosure | CEK theft from the database enabling offline decryption | CEKs stored only WRAPPED under KMS-held cohort KEKs; plaintext keys pinned+wiped in memory; DB access alone is insufficient | mitigated | `KeyHierarchyTests` wrap/unwrap suite |
| T-11 | Key hierarchy | Elevation | Erasure executed while litigation preservation applies | Decision engine precedence (hold outranks all), dual-layer holds, statement-scope visibility (V021), execution-time re-evaluation | mitigated | `Erasure_ReEvaluatesConflicts_AtExecutionTime` (both scopes) |
| T-12 | Public gateway | DoS | Redemption flood exhausting connections or the DB | Per-address + concurrency rate limits, PgBouncer transaction pooling, bounded streaming (O(1) memory) | mitigated | `EndToEndDownload_200MB_UsesConstantMemory`, limiter tests |
| T-13 | Any service | Elevation | A future endpoint shipped without authorisation | EndpointDataSource enumeration test: every route requires auth or sits on a justified allow-list | mitigated | `EndpointAuthorizationMatrixTests` |
| T-14 | Supply chain | Tampering | Malicious or vulnerable dependency / mutable base image | Central package pinning, SDK pinned via global.json, chiseled non-root images, CI vulnerability scan | mitigated | `SupplyChainAndConfigurationTests`, `ToolchainPinningTests` |

The one deliberately **open** row is T-08 — named, bounded, and priced in
`docs/LIMITATIONS.md` rather than hidden.

## Controls already in the scaffold

*Split by evidence. The distinction matters: a control nobody has attacked is a control nobody
knows works.*

### Implemented **and** covered by a test

| Control | Test |
| --- | --- |
| Ownership is a WHERE-clause predicate, never a post-load comparison. There is no code path that loads a row and then compares identifiers. | `Ownership_IsAPredicate_NotAPostCheck` |
| Unowned and non-existent resources are indistinguishable: both return 404, never 403, so enumeration gets no oracle. | `ListStatements_ForAnotherCustomer_Returns404NotForbidden`, `GetStatement_OwnedByAnother_Returns404` |
| The route `customerId` is untrusted; the JWT `sub` claim is authoritative, and the query runs against the subject regardless. | `ListStatements_ForAnotherCustomer_Returns404NotForbidden` |
| Storage keys and envelope-encryption material never reach the wire. | `StatementResponseTests` (no container needed), `StatementResponse_NeverContainsStorageKeyOrCryptoFields` |
| Every read **and every denial** is audited; the denial reason is recorded internally and never returned. | `DeniedAccess_IsAudited_WithAnInternalReasonThatIsNeverReturned`, `SuccessfulRead_IsAudited` |
| `audit_event` is append-only under **two independent mechanisms** — a trigger (incl. `TRUNCATE`) and revoked grants. | `Update_IsRejectedByTrigger`, `Delete_IsRejectedByTrigger`, `Truncate_IsRejectedByTrigger`, `AppDeliveryRole_CannotUpdateAuditEvent` |
| Altering an audit record breaks the chain and the break is reported at the altered record. | `TamperedRecord_FailsVerification` |
| Concurrent appends to one chain cannot duplicate a sequence number. | `ConcurrentWritersSameChain_ProduceValidChain` (50 threads) |
| Chain genesis differs per chain, so a record cannot be replayed into another chain. | `RecordFromAnotherChain_DoesNotVerify`, `Genesis_MatchesTheCSharpDefinitionByteForByte` |
| A field containing the canonical delimiter is rejected, not escaped. | `FieldContainingUnitSeparator_IsRejected` |
| An operation whose audit append fails rolls back entirely. | `UnitOfWork_AuditFailure_RollsBackBusinessOperation` |
| `app_delivery` cannot `INSERT`, `UPDATE` or `DELETE` statements. | `AppDeliveryRole_CannotDeleteFromStatement` |
| No service role can create tables or call `ensure_range_partitions`. | `NoServiceRole_CanCreateTables`, `AppDelivery_CannotCreatePartitions` |
| A development JWT signing key cannot start a service outside Development. | `JwtOptionsValidatorTests` |
| Chiselled non-root images with no shell, and no reintroduced `curl`. | `SupplyChainAndConfigurationTests` |
| A bounded date range is enforced, and the plan provably prunes partitions. | `WithDateRange_PrunesPartitions`, `InvalidDateRange_Returns400` |

### Implemented, **not yet** proven under attack

- Log redaction of `/v1/d/*` and of any field named `token`, `dek`, `kek` or `password`. The rules
  and their unit tests exist, but **no token-issuing code path has ever exercised them**. See the
  `TODO(security)` in `SensitiveDataRedactor`.
- RFC 9457 problem details that never return exception detail outside Development.
- Per-IP rate limiting on the public gateway — configured, never load-tested.
- Vulnerability scanning in CI.

### A control that is **not** what it appears

The audit hash chain does **not** defend against a privileged insider. Chain heads live in the same
database as the events, so an attacker able to rewrite both produces a self-consistent forgery, and
truncation from the tail is undetectable from inside. `IChainAnchor` is a **no-op**. Until it is
implemented, treat verification as evidence against application bugs and opportunistic tampering
only. See [ADR-0010](adr/0010-sharded-audit-hash-chains.md).

Every role that writes audit events necessarily holds `SELECT` and `UPDATE` on `audit_chain_head`,
because appending requires advancing the head. A `SECURITY DEFINER audit_append()` function would
remove that; it is not built.

## Explicitly deferred

*Knowingly absent. Listed so their absence is a decision rather than an oversight.*

- Download token issue and redemption.
- Encryption and key management.
- The audit trail implementation.
- Legal hold.
- Erasure.

## Review cadence

*Revisited whenever a trust boundary moves or a deferred item lands, and otherwise at each
release milestone.*
