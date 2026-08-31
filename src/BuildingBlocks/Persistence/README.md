# Persistence — query discipline

Five rules. Every one of them describes a query that works on a laptop against a thousand rows and
falls over on a table holding two and a half billion. They are enforced by
`tests/ArchitectureTests/QueryDisciplineTests.cs`, not just written here.

See also the **transaction-pooling trap list** at the top of
[`Connections/NpgsqlConnectionFactory.cs`](Connections/NpgsqlConnectionFactory.cs) — read that
before writing any SQL at all.

---

## 1. Keyset pagination only. Never `OFFSET`.

`OFFSET` on a live table with concurrent inserts produces **duplicates and gaps**: the offset is
computed against a result set that has already changed by the time the next page is requested, so a
row can be shown twice or skipped entirely. It also degrades linearly, because the server generates
and discards every skipped row — page 10,000 costs ten thousand pages of work to return one.

Keyset is stable under concurrent writes and it **preserves partition pruning**, because the cursor
carries the partition key. See [`Paging/Cursor.cs`](Paging/Cursor.cs).

```sql
-- The intended shape. A row-value comparison PostgreSQL can satisfy from a composite
-- index in ONE range scan.
WHERE (period_start, id) < (@cursorPartitionKey, @cursorId)
ORDER BY period_start DESC, id DESC
LIMIT @take
```

Written as separate OR-ed comparisons instead, the planner usually cannot use the index.

## 2. Every query has an explicit timeout.

Short for the delivery path, long for batch. `IDbConnectionFactory.CommandTimeoutSeconds(intent)`
returns the budget for a `ConnectionIntent`, and the value is baked into each data source's
connection string so a command created from the connection inherits it.

A query with no timeout does not merely run slowly — it holds a pooled connection, and behind
PgBouncer that connection is one of a few dozen shared by the whole fleet. One unbounded query
becomes everybody's outage.

## 3. Never `SELECT *`. Column lists only.

Adding a column is a routine migration. With `SELECT *` it silently changes the shape of every
result set that touches the table: more bytes over the wire, a broken positional mapping, and — the
expensive one — a column that was never meant to leave the database appearing in a projection
nobody re-reviewed. In this system that could be an encrypted blob or a token hash.

An explicit column list makes a schema addition a no-op for existing queries, which is exactly what
expand-migrate-contract needs.

## 4. No `LIST`-style unbounded scans.

Every object in storage is reached by a **key computed from the database**, never by listing a
bucket and filtering. At 2.5 billion objects a listing is not slow, it is unusable — and it is
billed per request. The same applies to the database: no query without a bounding predicate reaches
production.

The readiness check in
[`ServiceDefaults/Storage/ObjectStorage.cs`](../ServiceDefaults/Storage/ObjectStorage.cs) is
deliberately a metadata call for this reason.

## 5a. Every mapped snake_case column carries a PascalCase alias.

Dapper maps columns to properties by name, and nothing in this codebase turns on underscore
matching. A `snake_case` column in a multi-column select list therefore maps to **no property at
all**, and an init-only record swallows the miss as a default value instead of an error. The first
real execution of the gated tests showed how quiet that is: audit appends read `last_seq` and
`last_hash` into nothing and wrote a genesis-less chain, run rows surfaced `0001-01-01` periods,
and the outbox relay published events with an empty type. Aliasing to snake_case
(`AS was_consumed`) is the same bug in disguise. Single-column lists are exempt - they feed scalar
reads, where the name is irrelevant. Enforced by
`QueryDisciplineTests.EveryMappedColumn_CarriesAPascalCaseAlias`.

## 5. Slow queries are visible in development.

The local PostgreSQL container runs with `log_min_duration_statement=200`, set in
[`docker-compose.yml`](../../../docker-compose.yml) and mirrored in the Testcontainers fixture. A
query that crosses 200 ms is logged while it is still cheap to fix. Discovering in production that
nothing was logging slow queries is the wrong time.

---

## Bounded date ranges are part of the API contract

Partition pruning only works when the query filters on the **partition key**. The hottest query is
"this customer's statements", keyed on `customer_id`, while the partition key is time — a real
tension, and it is resolved in the API rather than in the database: a date range is **required**,
defaulting to 24 months with a hard maximum of 84, and a request without one is rejected with
`400`. Bounding the query surface is a legitimate design decision and far better than an endpoint
that works in development and times out in production.

See [ADR-0007](../../../docs/adr/0007-partitioning-strategy.md). An integration test asserts that
`EXPLAIN` shows pruning for a bounded query, so the day somebody removes the constraint, the build
goes red.
