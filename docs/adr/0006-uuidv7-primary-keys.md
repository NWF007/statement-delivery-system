# ADR-0006: Use application-generated UUIDv7 primary keys
**Status:** Accepted   **Date:** 2026-08-26

## Context
Generation.Worker inserts ~30M rows/month at ~1,400/sec into a table reaching 2.52 billion rows. UUIDv4 is random: every insert lands at a random point in the B-tree — page splits, fragmentation, write amplification, cache misses. UUIDv7 puts a 48-bit millisecond timestamp in the leading bits, so inserts append near the index's right edge like a sequential key while staying globally unique and non-enumerable. PostgreSQL 18 benchmarks on a 50M-row table show smaller indexes, faster bulk loads, and ID-ordered range scans ~3x faster with ~100x fewer buffer hits: time-adjacent rows are disk-adjacent.

## Options considered
| Option | Pros | Cons |
| --- | --- | --- |
| bigint identity | Fastest, 8 bytes, perfect locality | Enumerable; needs a round trip before insert, awkward across services |
| UUIDv4 | Non-enumerable, client-generated | Random insert position; the fragmentation above |
| UUIDv7 | Non-enumerable, client-generated, time-ordered | 16 bytes; leaks approximate creation time |

## Decision
`Guid.CreateVersion7()` behind `IIdGenerator` in `BuildingBlocks/Persistence` so tests inject a deterministic sequence. Columns stay `uuid`; no `DEFAULT gen_random_uuid()` — the application always supplies the value.

1. **Two security caveats.** UUIDv7 leaks approximate creation time. Acceptable for `statement.id`, whose period is public to its owner anyway. Not for download tokens: 256-bit CSPRNG random, no time component, no structure — nobody converts them to UUIDv7 later.
2. **Monotonicity.** `CreateVersion7()` is monotonic across milliseconds but not within one: 2,000 IDs from one millisecond return roughly half out of order, since the sub-millisecond bits are random. `UuidV7Generator` adds a per-process monotonic guard so IDs strictly increase in big-endian byte order. Locality needs only approximate ordering; strict ordering makes it testable and keyset pagination safe.
3. **Endianness.** `Guid.CompareTo` and `ToByteArray()` use .NET's mixed-endian layout, not PostgreSQL's `uuid` sort order. Compare with `ToByteArray(bigEndian: true)` or the string form, or the ordering test passes for the wrong reason.

## Consequences
16 bytes not 8 across 2.52B rows, paid for locality and non-enumerability. The guard is per-process, so 400 replicas interleave within a millisecond; approximate order is all locality needs. PostgreSQL 18+ `uuidv7()` and `uuid_extract_timestamp()` serve ad-hoc operational queries; no code depends on them.

## Revisit when
- Sustained inserts exceed ~5,000 rows/sec, making per-worker bigint ranges worth re-measuring.
- `pgstattuple` reports statement index bloat above 25% after a full monthly cycle.
- .NET ships a `Guid.CreateVersion7()` monotonic within a millisecond, letting us delete the guard.
- A threat model or regulator classifies statement creation time as sensitive metadata, invalidating caveat 1.
