# Database migrations

Plain, numbered `.sql` scripts run by [DbUp](https://dbup.readthedocs.io/), shipped as embedded
resources in `Db.Migrator`. No Entity Framework Core — see
[ADR-0002](../../../docs/adr/0002-dapper-and-dbup-over-ef-core.md).

Scripts live in `Scripts/` and are named `Vnnn__description.sql`. Resource names sort lexically,
and lexical order is execution order. Applied scripts are journalled in `schemaversions`.

## Running

The migrator is the only component that connects **directly** to PostgreSQL on port 5432. Every
running service goes through PgBouncer on 6432. DDL needs a session it owns for the duration, and
`CREATEROLE`, which no running service has.

```bash
# Via compose (the normal path). Runs once, then exits 0.
docker compose up db-migrator

# Directly, against a database you can reach.
Migration__ConnectionString='Host=localhost;Port=5432;Database=statements;Username=postgres;Password=...' \
Migration__AppDeliveryPassword=... \
Migration__AppDownloadPassword=... \
Migration__AppGenerationPassword=... \
Migration__AppRetentionPassword=... \
Migration__AppMigratorPassword=... \
dotnet run --project src/Migrations/Db.Migrator
```

Exit codes: `0` success or nothing to do, `1` a migration failed, `2` configuration is invalid.
Compose gates every service on `service_completed_successfully`, so a non-zero exit stops the whole
stack rather than letting services start against a half-migrated schema.

## Session guards

`SessionGuardPreprocessor` prepends this to every script:

```sql
SET lock_timeout = '3s';
SET statement_timeout = '30s';
```

**Fail fast rather than block.** A migration waiting indefinitely on a lock does not merely delay
itself: it sits at the head of the lock queue and every ordinary query wanting the same table
queues behind it. The migration that was "just waiting" has taken the application down.

## Rules

These are not style preferences. Each one describes a way a migration takes production down.

1. **Never `CREATE INDEX` without `CONCURRENTLY`** on a table with data. A plain `CREATE INDEX`
   holds a lock that blocks writes for the whole build. `CONCURRENTLY` **cannot run inside a
   transaction**, so such a script must be split out and the runner configured
   `WithoutTransaction()` for it. Note that a failed concurrent build leaves an `INVALID` index
   behind that must be dropped explicitly.

2. **Never `ALTER TABLE ... ADD COLUMN NOT NULL` with a volatile default** on a large table. A
   constant default is metadata-only and instant; a volatile one rewrites every row while holding
   `ACCESS EXCLUSIVE`.

3. **Never `ALTER TYPE`** on a large table. It rewrites the table. Add a new column, backfill it in
   batches, and switch over.

4. **Adding a constraint is two steps.** `ADD CONSTRAINT ... NOT VALID` takes a brief lock and
   applies to new rows immediately; `VALIDATE CONSTRAINT` scans existing rows under a weaker lock.
   Doing it in one statement holds `ACCESS EXCLUSIVE` for the length of a full scan.

5. **Never run `VALIDATE CONSTRAINT` during the month-end generation window.** It takes a lock and
   competes with 400 render workers for I/O. The one time the table is busiest is the one time this
   must not run.

6. **Forward-only, and backward-compatible with the currently deployed code.** Expand, migrate,
   contract — four deploys to remove one column: add the new one; write to both; stop reading the
   old one; drop it. A migration that assumes the new code is already everywhere breaks during the
   rollout window, when half the fleet is still old.

7. **Never edit a script that has run anywhere.** DbUp journals by name, not by content, so an
   edited script is silently skipped on every environment that already applied it — and applied in
   its new form on every environment that has not. Add `V00n+1` instead.

8. **Dollar-quote with a bare `$$`, never a named tag.** DbUp substitutes `$name$` variables in
   these scripts, so `$body$` or `$do$` looks exactly like a variable and gets eaten.

9. **Grants live with the DDL.** The migration that creates a table grants on it in the same file.
   A permission model kept somewhere else is a permission model that drifts.

## Current scripts

| Script | What it does |
| --- | --- |
| `V001__roles_and_grants.sql` | One least-privilege login role per service, plus the DDL owner. Revokes everything from `PUBLIC` and records the intended grant matrix for the business tables. |
| `V002__distributed_lease.sql` | Leader-election lease table with a monotonic fence token. Replaces `pg_advisory_lock`, which is unusable behind a transaction pooler. |
| `V003__partition_helper_functions.sql` | `ensure_range_partitions` and `range_partition_exists`. `SECURITY DEFINER`, owned by `app_migrator`, so services can create partitions without holding `CREATE`. |
| `V004__outbox.sql` | Transactional outbox, daily `RANGE` partitions, partial index on the pending backlog. Creates its first seven partitions. |

There are **no business tables**. `statement`, `account`, `download_token` and `audit_event` do not
exist yet; V001 records the grants they will get so that the migration which creates each one is
implementing a decision rather than inventing one.
