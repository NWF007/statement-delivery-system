# ADR-0018: Rate limit per IP and per customer, never per token
**Status:** Accepted   **Date:** 2026-08-27

## Context
The redemption endpoint is unauthenticated and internet-facing (ADR-0015). There is no account to suspend and no bill to attach abuse to, so rate limiting is a primary control here rather than a secondary one.

"Rate limit the download token" is the phrase that comes to mind first, and it is wrong in a way that is worth writing down, because the mistake is invisible in code review: the limiter is present, the tests pass, the dashboards show counters, and the control does nothing.

## Options considered
| Option | Pros | Cons |
|---|---|---|
| Per token | Reads as directly protecting the thing under attack | **Does not work.** See below |
| Per IP, counted in-process | Trivial; no infrastructure | With N replicas the effective limit is N× the configured one — an attacker round-robining across the fleet walks straight past it |
| Per IP, counted in Redis | Holds across the fleet; the limit means what it says | Needs a shared store, and a decision about what to do when it is unreachable |
| Per customer on issue **and** per IP on redeem, both in Redis (chosen) | Each limit addresses a threat that actually exists | Two configuration surfaces; a Redis dependency on the request path |

## Decision

| Surface | Partitioned by | Budget | What it actually defends against |
|---|---|---|---|
| Issue link | **customer** (JWT `sub`) | 10/minute and 100/hour | A compromised session mass-generating links to exfiltrate an entire statement history |
| Redeem | **IP address** | 30/minute | Token guessing and enumeration — **this is the real control** |
| Redeem, global | endpoint / replica | configurable (`RateLimiting:PermitLimit`, default 120/min) | Service-level denial of service |

Two windows on issue rather than one: 10/minute alone permits 14,400 links a day at a rate that never trips it; 100/hour alone permits the whole hour's budget in two seconds. Together they bound both the burst and the sustained total.

The identity-scoped limits are counted in Redis via an atomic `INCR` + `PEXPIRE` Lua script — one script rather than two commands, because a process that dies between `INCR` and `EXPIRE` leaves a counter with no TTL, which locks that partition out permanently.

**They fail closed.** If Redis is unreachable the limiter does not open up; it falls back to per-process counters with the same limits, which is stricter than intended during an outage. Removing a control because its backing store is down is how an outage becomes an incident.

### Why per-token limiting is not a control

An attacker guessing tokens presents a **different value on every attempt**. Each one hashes to a different key, so a per-token counter is created, incremented to one, and never touched again. A million guesses leaves a million counters sitting at one — every single one of them comfortably under any limit you choose. The limiter fires zero times and reports perfect health.

Per-token limiting constrains exactly one adversary: someone who already **holds a valid token** and is replaying it. Single use already reduces that adversary to one redemption, so the limiter adds nothing there either.

The one thing an enumeration script cannot avoid is making its requests **from somewhere**. That is why the redeem budget is partitioned by address, and it is why this decision is recorded as an ADR rather than left as a comment: per-token limiting *looks* protective, and looking protective is how it survives review.

## Consequences
Redis is on the request path for issue and redeem. It is already a dependency for caching, and the failure behaviour is defined and tested rather than emergent.

Callers behind a large NAT share a partition, so one abusive client can throttle its neighbours. That is why the **authenticated** API partitions by `sub` first and falls back to address only when there is no subject — an authenticated surface has a better identity available and should use it. The gateway has no such option.

`X-Forwarded-For` is honoured only when `RateLimiting:TrustForwardedHeaders` is set **and** the proxy is listed in `KnownProxies`. Trusting it unconditionally would let a caller put a fresh value in the header on every request and land in a fresh partition each time — a limiter that limits nothing, which is the same failure as per-token limiting wearing a different hat.

A per-IP limit is not a defence against a distributed attacker with a large address pool. Nothing at this layer is. What defends against that attacker is 256 bits of entropy; the rate limit is what keeps a single scripted client from generating noise, cost and log volume.

## Revisit when
- `download_denied_total{reason="UNKNOWN_TOKEN"}` rises materially — that shape *is* enumeration, and the response is to tighten the per-IP budget and to look at the address distribution, not to add a per-token counter.
- Legitimate 429s appear on the issue path, indicating a client integration that batches link generation; raise the hourly budget rather than the per-minute one.
- The gateway moves behind a CDN or WAF that can enforce per-address budgets closer to the edge, at which point this limiter becomes the second layer rather than the first.
