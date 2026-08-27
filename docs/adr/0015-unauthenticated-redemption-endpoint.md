# ADR-0015: The redemption endpoint is unauthenticated; the token is the credential
**Status:** Accepted   **Date:** 2026-08-27

## Context
`GET /v1/d/{token}` on Download.Gateway streams a statement PDF and requires no bearer token, no cookie and no session. Every other data-bearing endpoint on this platform is behind a JWT.

The link exists to be *followed*, and the things that follow it are not session holders: an email client, a corporate mail gateway pre-fetching URLs for scanning, an operating-system download manager, a phone that has never authenticated to this bank, a chat client rendering a preview. Requiring a JWT would be strictly more secure and would break the use case the link exists to serve — the customer would have to be re-authenticated inside whatever context they opened the link in, which for most of those contexts is not possible at all.

So the token in the URL is the credential. That is a bearer credential in a URL, which is a pattern with a bad reputation, and it deserves a written justification rather than a shrug.

## Options considered
| Option | Pros | Cons |
|---|---|---|
| Require the customer JWT | No bearer-in-URL; strongest control | Breaks every non-browser client; a link in an email becomes unusable; defeats the feature |
| Signed URL with an HMAC of the parameters | Stateless; no lookup | Cannot be revoked without rotating the key, which kills every outstanding link; cannot be made single-use without state anyway |
| S3/MinIO presigned URL, redirect the client | Zero bandwidth cost; trivial to build | No revocation, no single use, no audit of the actual transfer, and the object key leaks the storage layout |
| **Opaque single-use token, redeemed against our database (chosen)** | Revocable instantly; genuinely single-use; every access audited | Every download crosses our bandwidth; a public unauthenticated surface to defend |

## Decision
The endpoint is anonymous. Security rests entirely on the token's properties, and **each one is load-bearing** — this is the list to check before weakening anything:

| Property | Value | What it defends against |
|---|---|---|
| Entropy | 256 bits from a CSPRNG | Guessing. The search space is not walkable, and no timing or error signal narrows it |
| Lifetime | 10 minutes default, 1 hour hard cap (`ck_token_ttl`) | A URL that leaked into a browser history, a proxy log, a screenshot or a forwarded email is worthless by the time anyone finds it |
| Single use | One atomic `UPDATE … RETURNING` | A forwarded email delivering the statement twice; replay from a shoulder-surfed URL |
| Binding | To one customer **and** one statement | A leaked token being a capability against anything other than the one document it names |
| At rest | SHA-256 only, never the plaintext | A database read — a dump, a backup, a compromised replica — yielding usable credentials |
| In telemetry | Redacted at the export boundary and at the console formatter | The token landing in a log aggregator with 90-day retention and broad read access. This is the most commonly missed control in a system of this shape |
| Revocation | `DELETE /v1/download-links/{id}`, effective on the next attempt | A link known to have leaked staying live for the rest of its TTL |
| Rate limit | 30/minute per IP, counted fleet-wide (ADR-0018) | Enumeration and scripted abuse of a surface with no account to suspend |
| Isolation | A separate deployable from Delivery.Api | A flood against the public endpoint exhausting the authenticated API (ADR-0001) |

Two further consequences follow from the decision and are implemented rather than assumed:

- **All failures are indistinguishable.** Expired, consumed, revoked, malformed and never-existed return the same status, the same body, the same headers, and are padded to the same timing floor. The honest reason is not that this defeats a practical attack — 2²⁵⁶ is 2²⁵⁶ whichever error you get — but that a uniform failure costs almost nothing and removes an entire class of question from a security review.
- **The URL itself is a secret.** `SensitiveDataRedactor` treats anything after `/v1/d/` as a value to redact, in span attributes, log bodies, `ProblemDetails.instance`, exception messages and the console sink.

## Consequences
This service proxies bytes it could have redirected. That is a real cost in bandwidth and in connection-hold time, and it is the price of revocation, single use and an audit record of the actual transfer. It is also why `MaxConcurrentDownloads` exists: a request budget alone does not bound a streaming endpoint.

The gateway is internet-facing and unauthenticated, so it is the service that gets attacked. Its rate limits are tighter than the API's, its error surface is deliberately featureless, and it holds no credential that would be worth stealing beyond the database role that can only `SELECT` and `UPDATE` `download_token`.

A reviewer will, correctly, flag "bearer token in a URL" on sight. The answer is this table, not a defence of the pattern in the abstract.

## Revisit when
- A device-bound or DPoP-style proof becomes viable for the clients that actually follow these links — then the token could be bound to a key rather than to nothing.
- `download_incomplete_total` shows a material rate, indicating clients that need resumable transfers; that pressure interacts with ADR-0016 and may change the single-use model.
- The gateway's egress cost exceeds the value of per-transfer auditing — but note that switching to presigned URLs gives up revocation and single use, not just auditing.
