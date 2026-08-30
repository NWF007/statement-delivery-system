# ADR-0023: A hashed shard leads the storage key, not the date
**Status:** Accepted   **Date:** 2026-08-27

## Context
Every statement object needs a key. The obvious one reads beautifully:

```
statements/2026-08/{accountId}/statement.pdf
```

It sorts by month, it is browsable, and it makes an operator's life easy. It is also the shape that throttles.

S3 partitions its index by key prefix and scales request rate **per partition**. A date-first key puts every write for a month under one prefix — and this system writes 30 million objects into a few hours at month-end. That is one partition absorbing the entire fleet's `PUT` traffic while the rest of the keyspace sits idle. S3 does split hot partitions eventually, but "eventually" is measured in tens of minutes of elevated `503 SlowDown` responses, which is the whole of a month-end window.

## Options considered
| Option | Write distribution at month-end | Browsable | Notes |
|---|---|---|---|
| `statements/{period}/{accountId}/…` | One prefix for the entire month | Yes | The hot-partition case. Reads fine, throttles hard |
| `statements/{accountId}/{period}…` | Spread by account, but accounts cluster by creation time | Partly | Account ids are UUIDv7: they lead with a timestamp, so a sign-up cohort shares a prefix |
| Random prefix per object | Perfectly even | No | The key stops being derivable from the row, which means it must be *stored* — and a key that is stored but not computable cannot be recomputed if the column is ever lost |
| **`statements/{shard}/{accountId}/{period}-v{version}.enc`, shard = first 3 hex of SHA-256(statementId) (chosen)** | 4,096 prefixes from the first object | By shard | Deterministic, derivable, and evenly spread |

## Decision
```
statements/{shard}/{accountId}/{period}-v{version}.enc
  shard = first 3 hex characters of SHA-256(statementId)   → 4,096 prefixes
```

Three hex characters is 4,096 leading prefixes, reached uniformly from the very first object rather than after S3 has finished splitting anything. At 30 million writes in a month-end window that is roughly 7,000 objects per prefix — well inside per-partition limits, with no warm-up.

**The statement id is hashed rather than used directly.** UUIDv7 leads with a 48-bit millisecond timestamp, so statements minted in the same window share their leading bytes almost exactly. Taking them directly would reproduce the hot-prefix problem while *looking* like it had been solved — the key would contain a random-looking hex prefix that happened to be identical across a whole render batch. Hashing removes the structure, which is what makes the distribution provable (`StorageKey_ShardPrefix_IsHighCardinality` asserts that 20,000 consecutively-minted ids reach more than 3,900 of the 4,096 prefixes).

The date is still in the key, just not in front. Nothing is lost for a human reading a key; what is lost is the ability to list a month with a single prefix scan, which was never a viable operation anyway — see below.

## The key is always computed, never discovered
`StorageKeyScheme.KeyFor(statementId, accountId, period, version)` derives the key from database metadata. Two rules follow, and both matter more than the sharding.

**Never list.** At 2.5 billion objects, `ListObjectsV2` returning a thousand keys per call means two and a half million round trips for a full enumeration. That is not slow, it is not an operation that finishes. Every access path in this system reaches an object by a key computed from a row it has already authorised.

**Never accept a client-influenced key.** A key derived from anything a caller supplies is a path-traversal and SSRF primitive pointed at the bucket. The filesystem adapter this replaced carried an explicit traversal guard for exactly this reason; the encrypting adapter inherits the same rule and enforces it earlier, by deriving rather than accepting.

The `.enc` extension is deliberate. An operator looking at the bucket should be able to tell at a glance that these are not PDFs, so that nobody wastes an afternoon wondering why the file will not open.

## Consequences
- **Browsing by month requires a query, not a prefix scan.** The database is authoritative for "which statements exist for August 2026", and it answers that from an index in milliseconds. Object storage was never a good answer to that question at this scale.
- **Reads are unaffected.** A read already knows its statement id, so it computes the same shard the write did. The scheme costs one SHA-256 per key, which is nanoseconds.
- **The scheme must not change once objects exist.** A different shard function makes existing keys uncomputable. `storage_key` is stored on the row, so existing objects remain reachable — but the invariant that a key is *derivable* would be lost, and with it the ability to recompute a key that was never written down. If the scheme ever changes, it changes for new versions only, and old rows keep their stored keys.
- **Shard count is fixed at 4,096 by the three-character prefix.** Unlike the cohort count (ADR-0020), changing it is recoverable: existing keys stay valid because they are stored, and only new objects land differently. It is a performance parameter, not a one-way door — the two are worth distinguishing, since they look similar in code.
