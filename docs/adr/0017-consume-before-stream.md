# ADR-0017: Consume the token before streaming, and do not release it on abort
**Status:** Accepted   **Date:** 2026-08-27

## Context
Redemption has two parts: mark the token used, and send the bytes. They can be ordered either way, and the transfer can fail at any point in between.

The tempting order is bytes-first: stream the statement, and mark the token consumed only once the client has actually received all of it. It reads as fairer — a customer whose connection dropped at 90% did not really *get* their statement, so why should their link be spent?

The problem is that the client controls when the connection drops.

## Options considered
| Option | Pros | Cons |
|---|---|---|
| Consume only on successful completion | A failed transfer costs the customer nothing | Infinitely replayable by aborting: 49 aborts and one success is 50 grants of access from a "single-use" token. Each abort leaves **no record that access was granted at all** |
| Consume on completion, with a short reservation during the transfer | Bounds the replay window | A reservation is a lock, and a lock needs a lease, a renewal and an expiry — a distributed-systems problem introduced to make an edge case slightly kinder |
| Consume first, restore on abort | Same intent, transactional | The restore is a second write that can itself fail, leaving the token in whichever state the crash chose. It also *is* the replay hole, just with extra steps |
| **Consume first, never restore (chosen)** | Single use is unconditional; every grant is recorded before any byte leaves | A customer whose connection genuinely drops must request a new link |

## Decision
The order is fixed:

1. `UPDATE download_token SET consumed_at = now(), … WHERE token_sha256 = … AND expires_at > now() AND consumed_at IS NULL AND revoked_at IS NULL RETURNING …` — one statement, no read-then-write.
2. Write the `DOWNLOAD_STARTED` audit record **in the same transaction** as the consume.
3. Commit.
4. Only then open the content stream and write the response body.

**The token stays consumed if the transfer fails.** No compensating update, no restore, no retry window.

Two independent reasons, either of which is sufficient:

- **Replay.** If the token were released on abort, an attacker would replay it indefinitely by killing the connection every time. "Single use" would mean "single *successful* use, as judged by the attacker".
- **The audit trail.** Access was granted at step 2, before any byte was sent, and that is the fact an audit trail exists to capture. A download that aborts at 99% still means the bytes left this system and reached something. Recording that only on success would mean the most suspicious transfers — the ones that never complete — are the ones that leave no evidence.

The outcome of the transfer is recorded separately and **outside** the transaction, as `DOWNLOAD_COMPLETED` or `DOWNLOAD_INCOMPLETE`. Those are observations about what happened after the grant; they are not the grant, and they must not be able to roll it back.

## Consequences
A customer whose connection drops mid-download has spent their link and must request another. That is the cost, it is visible, and it is the right way round: the system fails towards *not* delivering a statement twice rather than towards delivering it to whoever aborts most persistently.

Because the audit write shares the consume's transaction, a failure to record the grant fails the redemption. That is intended — an ungrantable-but-unrecorded access is worse than a failed download.

`download_incomplete_total` makes the customer-facing cost of this decision measurable, and feeds the range-request question in ADR-0016.

## Revisit when
- `download_incomplete_total` shows a material rate **and** its causes are demonstrably network-side rather than client-abort — the same trigger as ADR-0016, and the same first remedy (a scoped, range-only secondary token), not a change to this ordering.
- A future content store can guarantee a byte range is delivered atomically end-to-end, which would change what "the transfer failed" even means.

Note what would *not* justify revisiting: complaints that a spent link feels unfair. The fairness argument is real and it loses to replay.
