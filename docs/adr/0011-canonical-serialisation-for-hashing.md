# ADR-0011: Define one canonical serialisation for audit hashing
**Status:** Accepted   **Date:** 2026-08-27

## Context
A hash means nothing unless the bytes hashed are reproducible. Two serialisers — or one after a runtime upgrade — emit identical data as different bytes, and the record fails to verify though nothing changed. A false tamper alarm on the signal that must never cry wolf is worse than no signal.

## Options considered
| Option | Pros | Cons |
|---|---|---|
| Platform JSON of the record as-is | Nothing to own | Key order, escaping and number format are runtime policy, not contract; a patch release invalidates every prior record |
| Printable delimiter (pipe, comma) | Readable | actorId `a,b` and the pair (`a`,`b`) hash identically — substitute one for the other, chain intact |
| Length-prefixed binary framing | Rejects nothing | Opaque to grep; frame widths are a second format to freeze; no gain where control characters are already invalid |
| U+001F join, reject on delimiter | Unambiguous; wholly ours | Frozen forever; a canonicaliser to hand-write and own |

## Decision
`canonical = join(U+001F, [chainId, chainSeq, id, statementId?, customerId?, actorType, actorId?, action, outcome, denialReasonCode?, sourceIp?, userAgentHash?, occurredAt (round-trip "O", UTC), canonicalJson(context)])`; `hash = SHA256(previousHash || UTF8(canonical))`.

Unit separator, not a pipe: invalid in every field above, and a value carrying it is rejected, not escaped — escaping moves the ambiguity down a level instead of removing it. Nulls become the empty string, never omitted, so the delimiter count is constant.

canonicalJson: keys sorted ordinally at every nesting level; no whitespace; numbers normalised so `1`, `1.0` and `1e0` agree; escaping hand-written and limited to what JSON requires, so a future runtime's defaults cannot move our bytes. Array order is preserved — order is semantic; sorting would collide two different contexts.

previousHash is prepended as raw bytes, outside the canonical string: no crafted field value reaches the chain link.

## Consequences
- The format freezes at the first record; changing it needs a `format_version` column and a verifier computing both.
- Property tests assert every single-field mutation changes the hash, key order does not, and a delimiter-bearing field is rejected.

## Revisit when
- The frozen-vector suite (10k stored records, replayed) fails after a .NET or Npgsql upgrade; that blocks the release.
- Any production verification sweep reports a mismatch.
- Any write is rejected for carrying U+001F in a rolling 30 days.
