# ADR-0025: Audit events bind to the transaction they describe

**Status:** Accepted · **Date:** 2026-08-30 · **Relates to ADR-0010.**

## Context

The system has claimed this rule since Prompt 2. `NpgsqlUnitOfWork` opens with it:

> THE RULE: AN OPERATION WITH NO AUDIT RECORD MUST BE IMPOSSIBLE. The audit append happens inside the same transaction as the operation it records.

The download gateway repeated it: *"Consume atomically, resolve the statement, audit, and COMMIT."* The link-issue endpoint repeated it: *"The insert and the audit share one transaction: a link that exists without a record of who asked for it must be impossible."*

**None of it was true.** `IAuditWriter.AppendAsync` had exactly one caller in the entire codebase — `RequestAudit.RecordAsync` — and that method opened its own transaction through `_unitOfWork.ExecuteAsync`. Every state change therefore committed first, and its audit record was written afterwards in a second, unrelated transaction:

| Operation | Committed at | Audited at |
|---|---|---|
| Token consume | `DownloadEndpoints.cs:132` | `:224` |
| Link issue | `DownloadLinkEndpoints.cs:275` | `:285` |
| Link revoke | `DownloadLinkEndpoints.cs:327` | `:335` |

A crash between the two left a spent token, an issued link or a revocation with no record of it. An audit write that merely *failed* was worse: the caller got a 500 for a token that was already burned, with nothing in the trail to say access had been granted. That is exactly the leverage an attacker who can induce audit failures wants — operate while the recording mechanism is the only thing that breaks.

The test that was supposed to prevent this — `UnitOfWork_AuditFailure_RollsBackBusinessOperation` — opened its own transaction and called `AppendAsync` directly, a shape no production code used. It proved that `NpgsqlUnitOfWork` rolls back when a delegate throws, which is trivially true. It passed for three prompts while the rule in its own first line was false of every endpoint in the system.

## Decision

**An audit event describing the outcome of a state-changing operation is appended inside that operation's transaction.**

**An audit event describing what happened after the commit** — a completed download, an aborted stream, an object that turned out to be missing — **is appended in its own transaction**, because there is no business write left to bind to.

**A pure-read denial keeps the transactionless form.** Nothing was written, so there is nothing to bind to.

`RequestAudit.RecordAsync` gained an overload taking an `NpgsqlTransaction`. The transactionless overload stays, and is correct for the second and third cases above; deleting it would push callers into opening transactions purely to satisfy a signature.

**The append must be the last statement in its transaction.** It takes `FOR UPDATE` on the chain head, and that row serialises every other writer on the same chain. Work done after it runs while a sixteenth of the system's write capacity waits behind it. This is a throughput requirement, not a style preference, and each converted call site says so at the call site.

## What this changes, deliberately

An audit failure now **rolls back the business operation**. On the redemption path the token is not consumed, so the customer's link still works once auditing recovers.

This is strictly better than the behaviour it replaces, which burned the token, recorded nothing, and returned 500.

**A correction worth recording.** The remediation brief for this change described the fix as "the code catching up with ADR-0007 (fail closed when audit is unavailable)." ADR-0007 is the partitioning strategy and says nothing about auditing — the reference was wrong. The rule *was* written down once, for one path: ADR-0017 (consume-before-stream) states that the `DOWNLOAD_STARTED` record is written "in the same transaction as the consume" and that "a failure to record the grant fails the redemption." The shipped code contradicted that accepted ADR for a full prompt, on that path and every other, while comments in `NpgsqlUnitOfWork` and both endpoints repeated the claim.

So this ADR generalises ADR-0017's per-path decision into the system-wide rule, and is the first place the *general* rule is written down. That is worth being exact about, because the failure here was both kinds at once: on the redemption path the code was wrong against a documented decision and nothing checked them against each other; everywhere else the rule had never been written anywhere a reviewer would look.

## Alternatives considered

| Option | Why not |
|---|---|
| Leave it; audit asynchronously via the outbox | The outbox is itself a table written in a transaction, so this is the same decision one level down — and it adds a window where the record exists but is unrelayed. For an evidentiary trail the window is the problem. |
| Best-effort audit, log on failure | Makes "operated with no record" a normal, silent outcome. The one thing the trail may never permit. |
| Keep the separate transaction, add a reconciliation sweep | Detects the gap after the fact instead of preventing it, and cannot distinguish a crash from a deletion — which is the distinction the hash chain exists to make. |
| Bind every event, including post-commit ones | Would hold the consume transaction open across a multi-megabyte transfer, pinning a pooled connection behind PgBouncer for the duration. Trades an evidentiary gap for an availability one. |

## Consequences

The chain-head lock is now held for slightly longer on three paths, because the append shares a transaction with a write rather than running alone. Both writes are short and the append is last, so the additional hold is one INSERT plus one UPDATE.

`Redemption_WhenAuditWriteFails_DoesNotConsumeToken` replaces the vacuous test. It drives the real HTTP path with a deliberately broken `IAuditWriter` and asserts the token survives, no audit row exists, and — the assertion that actually proves the rollback was clean rather than merely absent — the same link still redeems successfully once auditing recovers.

## Revisit when

- A fourth write path appears. The rule is enforced by review and by one behavioural test on the redemption path; a general architecture rule ("no state-changing endpoint calls the transactionless overload") would be better, and needs a way to tell state-changing endpoints apart from read ones.
- Chain-head contention shows up in latency percentiles. The fix is more chains, not a looser binding — but the measurement should come first.
- Prompt 5 adds generation. `MarkAvailableAsync` and `MarkFailedAsync` both take a transaction precisely so their audit records can join it; that is the pattern to follow, not an exception to it.
