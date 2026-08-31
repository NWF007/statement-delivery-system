# ADR-0022: Object Lock in COMPLIANCE mode, with GOVERNANCE in Development
**Status:** Accepted   **Date:** 2026-08-27

## Context
Statements are a regulatory record kept for seven years. "Kept" has to mean something stronger than "we intend not to delete them" — an auditor's question is whether a record *could* have been altered, not whether anyone did.

S3 Object Lock offers two retention modes:

- **GOVERNANCE** — a principal holding `s3:BypassGovernanceRetention` can shorten or remove the retention and delete the object.
- **COMPLIANCE** — nobody can. Not a user, not a role, not the account root, not AWS support. The retention runs to its date and that is the end of it.

## Options considered
| Option | Pros | Cons |
|---|---|---|
| No Object Lock, policy and IAM only | Nothing irreversible; simple | An auditor asks "could this have been deleted?" and the honest answer is yes, by anyone who could obtain the right role. A control that a sufficiently privileged insider can bypass is the control an insider bypasses |
| GOVERNANCE everywhere | Immutable in practice; recoverable from mistakes | Bypassable by design. For a regulatory record it is a convention with a strongly worded name |
| COMPLIANCE everywhere | Genuinely immutable | A bug in Development writes objects nobody can ever delete, and every developer accumulates permanent test data at permanent cost |
| **COMPLIANCE in Staging and Production, GOVERNANCE in Development (chosen)** | Real immutability where it is claimed; recoverable where mistakes are expected | Two behaviours to keep straight; Development does not exercise the exact production path |

## Decision
`ObjectLockOptions.Mode` is **required** and set explicitly per environment. Development uses `GOVERNANCE`; Staging and Production use `COMPLIANCE`.

The bucket is created with `mc mb --with-lock` and a default retention of `COMPLIANCE 2555d` (seven years, in days so the value is unambiguous and matches `RetentionPolicy.Default`). The application sets a mode and a retain-until date on every `PutObject` rather than relying on the bucket default, so the date is derived from the statement's **period end** — not from the write time, so a statement regenerated years late does not earn extra retention.

### ⚠ Compliance mode is genuinely irreversible
Not "hard to undo". Not undoable. A bug that writes objects with a seven-year compliance lock has created seven years of storage bills that nothing can cancel, and there is no support ticket that fixes it.

That is why the setting is **required with no default**. Defaulting to COMPLIANCE puts the irreversible setting one forgotten config file away from a developer's laptop; defaulting to GOVERNANCE puts the unenforceable one one forgotten config file away from production. Neither default is safe, so there is none, and startup fails without an explicit value.

The environment asymmetry is the mitigation, and it is the honest trade: Development does not exercise the exact production configuration, in exchange for developers being able to delete the ten thousand test objects they will inevitably create. The path is otherwise identical — same adapter, same call, same retention arithmetic — so what differs is one enum value.

### ⚠ Lock expiry is not deletion
This is the trap that costs money quietly. When the retain-until date passes, the object becomes **eligible** for deletion. Nothing removes it. The storage bill continues for as long as it exists, which without an explicit purge job is forever.

A system that set a seven-year retention and assumed expiry meant cleanup would believe it had a retention policy while paying to store 2.5 billion objects in perpetuity — and the discrepancy would surface as an unexplained storage bill years after anyone remembered writing this. The purge job is Prompt 6's work; this ADR is where the requirement is recorded so it cannot be forgotten in the meantime.

### Object Lock cannot be enabled on an existing bucket
It must be set at bucket creation (`mc mb --with-lock`, or `ObjectLockEnabledForBucket` on `CreateBucket`). Both MinIO's community build and S3 enforce this; newer MinIO AIStor releases relax it, but the pinned community image does not — and since S3 does not either, the local environment should mirror production rather than mask the constraint.

Prompt 1's `createbuckets` container created the bucket with versioning and **without** lock. That is not a config change to correct: **the bucket must be destroyed and recreated**, `docker compose down -v` included. Locally that is free. In production it would be a 470 TB migration — which is precisely why it is worth getting right in a scaffold, where the cost is one command.

Enabling Object Lock also enables versioning permanently, and versioning can then never be disabled. That is a feature: it is what makes the immutability claim survive an incident in which somebody would like to turn it off.

## Enforcement
Configuration is not evidence. Three things check it:

1. **`ObjectLockHealthCheck` fails readiness** on boot if the bucket lacks Object Lock or versioning. A silently unlocked bucket is worse than a crash, because the system appears compliant and is not — and the discovery happens at an audit rather than at a deploy.
2. **`Write_ThenDeleteBeforeRetention_IsRejected`** proves the storage system actually refuses to destroy a record. Note that it deletes a specific **version id**: on a versioned bucket a `DeleteObject` without one writes a delete marker and *succeeds* even under Object Lock, so a test that omitted it would pass, prove nothing, and read exactly like a real one.
3. **`Startup_WithoutObjectLock_FailsReadiness`** runs the health check against a bucket deliberately created without lock. A health check nobody has seen fail is a health check nobody knows works.

## Consequences
- Compose logs `mc version info` and `mc retention info` after provisioning, so the container output proves the settings applied rather than proving the commands were issued.
- A statement cannot be corrected in place. It never could — a regeneration is a new row and a new object at a new key (`-v2.enc`), by ADR-0009's versioning rule — and Object Lock now enforces what the schema already intended.
- Retention and legal hold interact: a legal hold must be able to *extend* retention past the object's date, never shorten it. Prompt 6.

## Revisit when

- **Retention periods change by statute**: new PUTs pick up the new period; existing locks
  cannot shorten, and any lengthening of EXISTING objects is a PutObjectRetention batch job to
  plan deliberately.
- **A legal instruction requires early destruction of locked objects**: it cannot be done, and
  that is the point - the response is crypto-erasure (ADR-0035), not a storage change.
