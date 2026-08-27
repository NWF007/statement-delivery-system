# ADR-0016: No HTTP range request support on the download endpoint
**Status:** Accepted   **Date:** 2026-08-27

## Context
`GET /v1/d/{token}` responds with `Accept-Ranges: none` and ignores any `Range` header. Statements are typically 40 KB to 2 MB, but the platform must tolerate outliers — a full-year business statement can reach hundreds of megabytes.

Range requests are what make a download resumable. A client that loses its connection at 80% sends `Range: bytes=<n>-` and continues. That requires **a second request against the same URL** — and the URL's token was consumed by the first one, atomically, before a single byte was written (ADR-0017).

So the tension is direct: resumability requires the credential to be reusable, and single use is the property the whole design rests on. There is no way to have both without giving something up.

## Options considered
| Option | Pros | Cons |
|---|---|---|
| Support ranges, keep the token valid until fully sent | Resumable downloads work | Reintroduces the replay this design exists to eliminate: abort at 99%, replay forever, and every replay is a fresh grant of access. Also makes "was this delivered?" unanswerable |
| Support ranges, allow N redemptions instead of one | Resumable within a bounded budget | "Single use" becomes "a few uses", which is a much weaker sentence to write in a security review; the audit trail gains ambiguity about what N events mean |
| Support ranges via a second, range-only token issued on the first redemption | Resumable, still bounded | Two token types, two lifecycles, two revocation paths — a large increase in the complexity of the one part of the system that must be obviously correct |
| **Refuse ranges (chosen)** | Single use stays a simple, true statement | A client that loses its connection must ask for a new link |

## Decision
Refuse ranges. `Accept-Ranges: none` is set explicitly rather than merely omitted, so the answer is stated rather than inferred.

The failure mode is mild and recoverable: a customer whose download dies asks for another link. Issuing one is cheap, the API is already there, and the customer is already authenticated at that point. Compare that to the alternative — an attacker who aborts the connection every time and replays a "single-use" token indefinitely, leaving an audit trail that records one grant per abort and no completion.

The cost of this decision is **measured, not assumed**. `download_incomplete_total` counts every transfer that ended before all bytes were sent, and it is emitted specifically so that this trade-off can be re-examined with evidence instead of intuition.

## Consequences
On a poor mobile connection, a large statement may fail repeatedly, and each retry costs a round trip through the authenticated API to mint a new link. For the typical 40 KB–2 MB statement this is close to theoretical; for a multi-hundred-megabyte annual statement it is not.

`Content-Length` is always set and the body is always the whole object, so clients see accurate progress. Download managers that expect ranges will fall back to a single stream rather than failing.

The concurrency limit (`MaxConcurrentDownloads`) matters more because of this decision: every transfer holds its connection for the whole object, with no opportunity to release and resume.

## Revisit when
- `download_incomplete_total / download_completed_total` exceeds **2% over a rolling month**. That is the number that turns "clients cope fine" into a claim that is no longer true.
- The p99 statement size exceeds **50 MB**, at which point a failed transfer costs enough bandwidth on both sides that resumability is worth new machinery.
- Customer support attributes more than **10 tickets a quarter** to failed large downloads.
- If any of those trip, the option to reach for first is the range-only secondary token: it keeps the primary token single-use, and it can be scoped to one statement, one client address and a few minutes.
