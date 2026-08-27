# ADR-0002: Use Dapper and DbUp instead of Entity Framework Core
**Status:** Accepted   **Date:** 2026-08-26

## Context
This platform's security boundary is one statement: the atomic `UPDATE ... RETURNING` in Download.Gateway that redeems a single-use token. Race-freedom there depends on the exact SQL emitted — the predicate, the RETURNING clause, the absence of a read-then-write gap — so it is handwritten and reviewed, never generated. Generation.Worker separately lands ~30M rows/month via Npgsql `BeginBinaryImportAsync` binary COPY, which EF Core cannot express. The schema is PostgreSQL-specific: PARTITION BY RANGE on issue month, partial indexes over ~2M live tokens, append-only triggers on audit, REVOKE plus per-service roles so only Retention.Worker holds DELETE.

## Options considered
| Option | Pros | Cons |
| --- | --- | --- |
| EF Core (migrations + LINQ) | Familiar; change tracking; model-to-schema drift detection | Generated SQL on the token hot path; no binary COPY; migrations model neither partitions, partial indexes, triggers, nor grants |
| EF Core migrations + Dapper queries | Reviewable hot-path SQL; keeps migration tooling | Two models over one schema; migrations degenerate into `migrationBuilder.Sql("…")` blobs — DBA review gets worse, not better |
| Dapper + DbUp (chosen) | Explicit SQL, cheap materialisation, COPY available; numbered `.sql` scripts a DBA reviews and production runs verbatim | No LINQ, no change tracking, no drift detection; mapping by hand |

## Decision
Dapper for all queries; DbUp runs numbered plain-SQL scripts embedded as resources. No ORM anywhere; an architecture test asserts no project references `EntityFrameworkCore`.

## Consequences
Every query is hand-mapped — more code, and a column added without a mapping fails at integration-test time, not model validation. In exchange, the SQL in the repository is the SQL that runs, migration diffs are readable by someone who never opened C#, and redemption fits on one screen. The one-line code-review answer: the schema uses PostgreSQL features EF Core cannot express, and the hot path is a hand-optimised statement.

## Revisit when
- EF Core ships binary COPY bulk insert *and* declarative partition, trigger and grant support in migrations — both, not either.
- Mapping defects cause more than two production incidents per year, or hand-written mapping exceeds 5% of solution LOC.
- More than three schema-drift escapes reach staging in a year, making absent drift detection costlier than generated SQL.
- Token redemption moves off PostgreSQL (e.g. Redis-only), removing the atomic `UPDATE ... RETURNING` entirely.
