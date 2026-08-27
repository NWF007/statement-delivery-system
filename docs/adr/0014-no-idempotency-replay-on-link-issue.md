# ADR-0014: No idempotency replay on download-link issue
**Status:** Accepted   **Date:** 2026-08-27

## Context
`POST /v1/statements/{statementId}/download-links` returns the plaintext download token in its response body. That body is the only place the plaintext ever exists: the database holds a SHA-256, the logs and spans hold neither, and the value is generated, encoded once and dropped.

The platform's convention for unsafe HTTP methods is idempotency-key replay — a client sends `Idempotency-Key`, the server stores the request hash **and the serialised response**, and a retry with the same key returns the stored response without re-executing. That is the right shape for a payment, a transfer, or anything where executing twice is worse than answering twice.

Applied here it would write the plaintext token into an `idempotency_key` row. The token would then be at rest, in a table with its own retention and its own backups, readable by every role that can read that table, and recoverable from every dump taken while the row lived. One design property — *the plaintext exists in exactly one place, exactly once, and never at rest* — would be destroyed by a middleware that has never heard of tokens.

A second, smaller conflict: this endpoint is not idempotent in the domain sense. Two calls **should** produce two distinct tokens; that is what "single use" means.

## Options considered
| Option | Pros | Cons |
|---|---|---|
| Store the full response, as elsewhere | Uniform middleware; retries are free | Persists a live credential at rest; defeats the system's central invariant |
| Store the response with the token field stripped | Keeps replay; removes the secret | Returns a body with no token — a "successful" retry the client cannot use, which is worse than an error |
| Store only the request hash, re-execute on replay | No secret at rest | Not idempotency at all; the name would lie about the behaviour |
| **Exempt the endpoint (chosen)** | The invariant holds without exception | One endpoint behaves differently from the rest; the exemption must be honoured by future middleware |

## Decision
The endpoint is marked `[SkipIdempotency("Response contains a secret")]` and is exempt from replay. The trade is not close:

- A **duplicate link is harmless.** Each is independently single-use, short-lived, bound to the same customer and the same statement, separately audited, and independently revocable. Issuing two costs one row and one audit record.
- A **stored plaintext token is a breach.** It is a bearer credential at rest, in a table nobody thinks of as holding credentials.

The per-customer issue rate limit (10/minute, 100/hour — ADR-0018) is what bounds the cost of a retrying client, and it does so without persisting anything sensitive.

**Implementation note.** No idempotency middleware or `idempotency_key` table exists in this repository yet. `SkipIdempotencyAttribute` is a marker placed *now*, before the middleware is written, precisely because the alternative is a future change that applies replay uniformly to every endpoint and quietly starts writing tokens to disk. `IdempotencyTable_RemainsEmpty_AfterLinkIssue` asserts both halves: that no idempotency store exists, and that this endpoint carries the exemption.

## Consequences
A client that retries a link-issue request after a network timeout gets a second link rather than the first one back. Both work; both expire; the first one going unused costs a row.

The exemption is a standing obligation. When idempotency middleware lands it must read this attribute, and a reviewer must ask "does this response contain a secret?" for every endpoint that opts out. That obligation is written down here rather than living in whoever remembers it.

Uniformity is lost. This is a real conflict between two good practices — idempotent writes, and secrets never at rest — resolved by deciding which property matters more, rather than mechanically applying both and discovering the interaction in an incident review.

## Revisit when
- An idempotency store lands that can hold a **reference** to a response rather than its body, with per-field classification — then replay could return a 303 to a re-issue rather than the token itself.
- Link issue starts costing something material (a KMS operation per link, a per-call charge), so that duplicate issues are no longer free and the retry cost outweighs the exemption.
- Telemetry shows client retries producing more than 5% duplicate links in a month — that is a client-behaviour problem worth solving, though probably with a client fix rather than by storing tokens.
