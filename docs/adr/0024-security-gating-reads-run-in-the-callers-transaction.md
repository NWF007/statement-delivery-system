# ADR-0024: Security-gating reads run inside the caller's transaction

**Status:** Accepted · **Date:** 2026-08-30 · **Supersedes nothing. Constrains ADR-0008.**

## Context

`ConnectionIntent` (ADR-0008) splits connections three ways: `Write` and `ReadStrong` go to the primary, `ReadEventual` goes to a replica when one is configured. The enum's own documentation reserves `ReadStrong` for "a read whose result gates a security decision."

The download redemption path did not honour that. Inside the transaction that atomically consumes a token, it resolved the statement through `IStatementReadRepository.FindAsync` — a method that takes no transaction, opens its **own** connection, and does so at `ReadEventual`. One line, three separate defects:

**Isolation.** The read could not see the transaction that was deciding, and the transaction could not see the read. Two sessions running next to each other, one of them making an access-control decision on the other's behalf.

**Correctness.** A replica that has not caught up returns nothing for a row that exists. The endpoint treated that as a denial — and the consume, which had already succeeded, still committed. A customer's single-use link was spent on a 404 for a statement sitting intact on the primary. Worse, once envelope encryption put `wrapped_dek`, `kek_id` and `content_sha256` on that same row, a stale read meant decrypting against the wrong key material: `CiphertextIntegrityException`, `statement_decryption_failure_total`, and an operator paged to investigate tampering that was actually NTP.

**Liveness.** Acquiring a second connection while holding a write transaction is a classic pool deadlock. With no replica configured — the default — both data sources resolve to `pgbouncer/statements_download`, `pool_size=20`, `pool_mode=transaction`. Every in-flight redemption held one server slot and then asked for a second. At twenty concurrent redemptions, each holder waits for a slot only another holder can release; `query_wait_timeout = 15` converts the deadlock into a wave of timeouts rather than a permanent hang, which makes it harder to diagnose, not easier.

None of this was visible in the test suite, because every integration test ran against one database with no replica configured. Under those conditions the broken code and the correct code are indistinguishable.

## Decision

**A read whose result gates a security or access decision runs on the caller's connection, inside the caller's transaction.** `ReadEventual` is never correct for such a read.

`IStatementReadRepository.FindAsync` gained a second overload taking an `NpgsqlTransaction`. It executes the same SQL, with the same ownership predicate and the same partition key; only the connection changes. The transactionless overload remains, because the catalogue path genuinely wants eventual reads — browsing statements is not an access decision, it is a list the ownership predicate has already constrained.

Two tests hold the line:

- `GatingReadBoundaryTests.DownloadGateway_MustNotCall_TransactionlessStatementRead` reads the compiled IL and fails if the gateway references the three-argument overload. The two overloads are one keystroke apart and the wrong one compiles, which is precisely the shape a rule has to catch.
- `RedemptionInvariantTests.Redemption_SucceedsEvenWhenReplicaLags` points `ReadEventual` at a separately migrated database holding the schema and none of the rows, and asserts the redemption still returns 200 with the right bytes.

## Alternatives considered

| Option | Why not |
|---|---|
| Change `FindAsync` to `ReadStrong` | Fixes correctness, leaves isolation and the pool deadlock untouched. `ReadStrong` also goes to the primary, so the second connection is still acquired while holding the first. |
| Read the statement before opening the transaction | Reintroduces time-of-check-to-time-of-use: the row could change between the read and the consume, which is the exact class of bug the atomic consume exists to eliminate. |
| Raise `pool_size` | Moves the concurrency at which the deadlock occurs; does not remove it. A liveness bug that needs more load to reproduce is worse, not better. |
| Forbid `ReadEventual` entirely | Throws away the read/write split, which is load-bearing at 2.5 billion rows. The intent is right; one call site used it in the wrong place. |

## Consequences

The redemption path holds one connection instead of two, halving its PgBouncer footprint on the hot path. The statement lookup now sees the consume's uncommitted write, which is correct: it is reasoning about the token this transaction just spent.

`ReadEventual` keeps exactly one meaning — a read nobody is deciding anything on.

## Revisit when

- A second gating read appears anywhere. The IL rule covers `Download.Gateway` and `FindAsync` specifically; a new service or a new gating method needs its own arm, and the rule should be generalised before it is copied.
- A replica is deployed for real. The lag test simulates the worst case with an empty database; a real replica makes it possible to test bounded lag, which is the common case rather than the extreme one.
- The connection factory grows a fourth intent. Any new intent needs an explicit answer to "may a read at this intent gate access?", and the answer for anything not pinned to the primary is no.
