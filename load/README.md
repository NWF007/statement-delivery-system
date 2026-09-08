# Load testing

k6 scenarios for the delivery and generation paths. **Numbers nobody can reproduce are worth
nothing** — these scripts, the seed command, and the export query below are the whole recipe.

## Prerequisites

- The stack up: `docker compose up --build -d` (wait for `/health/ready` on 8081 and 8082)
- [k6](https://k6.io) v0.50+
- A **seeded** database — testing against a hundred rows measures nothing:

```bash
dotnet run --project tools/seed -c Release -- --customers 500000 --months 24 --seed 101
# Repeat with --seed 102..106 for a 3M-customer volume. Chunk it: the tool materialises
# customers and accounts in memory before writing, so one large run OOMs where six small ones do not.
```

- The sample file the scripts read (one AVAILABLE statement per customer, spread across the base):

```bash
# ORDER BY random() over the statement tree is a full scan — 28 GB at 85.7M rows. Sample the
# UNPARTITIONED customer table instead and join to one month: 4.4 s rather than minutes.
#
# SIZE THIS DELIBERATELY. IssueLinkRules allows 10 issues/minute/customer, so a sample of N
# distinct customers caps the write path at N x 10/60 req/s no matter what the database can do.
# A 4,225-customer sample capped it at ~704/s and produced a 49.89% failure rate that reads like
# a system limit but is the rate limiter. 200k customers puts the ceiling at ~8,333/s, high
# enough that PostgreSQL binds first — which is the thing being measured.
docker compose exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" postgres psql -U postgres -d statements -c "\
  \copy (WITH c AS (SELECT id FROM customer TABLESAMPLE SYSTEM (12) LIMIT 300000) \
         SELECT DISTINCT ON (s.customer_id) s.customer_id, s.id AS statement_id, s.period_start \
           FROM c JOIN statement s ON s.customer_id = c.id \
          WHERE s.period_start >= '2026-06-01' AND s.period_start < '2026-07-01' \
            AND s.status='AVAILABLE' \
          ORDER BY s.customer_id LIMIT 200000) TO STDOUT WITH CSV HEADER" > load/data/statements.csv
```

## Scenarios

| Script | Path exercised | What it proves |
|---|---|---|
| `01-link-issue.js` | `POST /v1/statements/{id}/download-links` | Write-path RPS: token mint + hash insert + audit append (the chain-head serialisation candidate) |
| `02-redemption.js` | issue → `GET /v1/d/{token}` | The full redeem: atomic consume, decrypt, stream. Each iteration issues a FRESH link — tokens are single-use, so redemption load cannot be generated any other way |
| `03-catalogue-browse.js` | `GET /v1/customers/{id}/statements` | Read-path with partition pruning under the mandatory date range |
| `04-mixed-realistic.js` | 70% browse / 25% issue / 5% redeem | The composite that resembles production |
| `05-generation.js` | `POST /v1/statement-runs` + polling | Batch throughput: items/sec sustained, and the extrapolation to 30M-in-6h |

## Running a ramp

Every HTTP scenario takes the same stages — **ramp to the breaking point, not to a comfortable
number** — and stops on the failure thresholds:

```bash
k6 run load/01-link-issue.js \
  -e API_URL=http://localhost:8081 -e GATEWAY_URL=http://localhost:8082
```

Stages: 50 → 200 → 500 → 1000 → 2000 VUs, 2 minutes each.
Abort thresholds: error rate > 1%, or p99 > 2 s.
Record per stage: p50 / p95 / p99 / error rate / RPS → the tables in `docs/SCALE.md`.

While a run is hot, capture the bottleneck evidence:

```bash
# Chain-head lock waits (hypothesis 1 in SCALE.md)
docker compose exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" postgres psql -U postgres -d statements -c "
  SELECT wait_event_type, wait_event, count(*) FROM pg_stat_activity
   WHERE state='active' GROUP BY 1,2 ORDER BY 3 DESC;"

# PgBouncer pool pressure (hypothesis 2). PgBouncer listens on TCP only, so name the host.
docker compose exec -T -e PGPASSWORD="$PGBOUNCER_ADMIN_PASSWORD" pgbouncer \
  psql -h 127.0.0.1 -p 6432 -U pgbouncer pgbouncer -c "SHOW POOLS;"
```

## Honest notes

- Auth tokens come from the **development-only** mint (`POST /v1/dev/tokens`); these scripts are
  for the local/dev stack and will not run against a Production profile — by design.
- `02-redemption.js` measures issue+redeem PAIRS; its redeem RPS is bounded by issue RPS. The
  denial path (replayed links) is measured inside the same script as a separate metric, because
  the 50 ms uniform-timing floor makes denials behave differently — see SCALE.md hypothesis 3.
- **First executed 2026-09-01** against an 85.7M-statement seed; docs/SCALE.md carries the results
  and the host they were taken on. Three defects surfaced on that first run and are fixed here:
  every POST returned 415 (no `Content-Type` — hence `jsonAuthHeaders` in `lib.js`), the sample
  query above did not scale, and the sample was too small to escape the per-customer rate limit.
- `02-redemption.js` **cannot measure the redeem path against seeded data.** `tools/seed` computes
  storage keys without uploading objects — *"the objects these rows name have never existed"* — so
  every redemption 404s at the gateway before decrypt-and-stream. It needs a generation run to have
  written real ciphertext first. The denial floor is measurable without that; SCALE.md hypothesis 3
  shows how.
- The token mint is a **second HTTP request per iteration** and is not part of the production path,
  so `http_reqs` is roughly double the issue rate. Report k6 `iterations` as the issue RPS. The mint
  is also what fails first under high VU counts, exhausting host TCP connections before the API is
  troubled.
