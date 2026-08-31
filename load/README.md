# Load testing

k6 scenarios for the delivery and generation paths. **Numbers nobody can reproduce are worth
nothing** — these scripts, the seed command, and the export query below are the whole recipe.

## Prerequisites

- The stack up: `docker compose up --build -d` (wait for `/health/ready` on 8081 and 8082)
- [k6](https://k6.io) v0.50+
- A **seeded** database — testing against a hundred rows measures nothing:

```bash
dotnet run --project tools/seed -c Release -- --customers 100000 --months 24 --seed 42
```

- The sample file the scripts read (a bounded, random sample of AVAILABLE statements):

```bash
docker compose exec -T postgres psql -U postgres -d statements -c "\
  \copy (SELECT customer_id, id AS statement_id, period_start \
           FROM statement WHERE status='AVAILABLE' \
           ORDER BY random() LIMIT 5000) TO STDOUT WITH CSV HEADER" > load/data/statements.csv
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
docker compose exec postgres psql -U postgres -d statements -c "
  SELECT wait_event_type, wait_event, count(*) FROM pg_stat_activity
   WHERE state='active' GROUP BY 1,2 ORDER BY 3 DESC;"

# PgBouncer pool pressure (hypothesis 2)
docker compose exec pgbouncer psql -p 6432 -U pgbouncer pgbouncer -c "SHOW POOLS;"
```

## Honest notes

- Auth tokens come from the **development-only** mint (`POST /v1/dev/tokens`); these scripts are
  for the local/dev stack and will not run against a Production profile — by design.
- `02-redemption.js` measures issue+redeem PAIRS; its redeem RPS is bounded by issue RPS. The
  denial path (replayed links) is measured inside the same script as a separate metric, because
  the 50 ms uniform-timing floor makes denials behave differently — see SCALE.md hypothesis 3.
- These scripts have **not** been executed on the authoring machine (no Docker there —
  docs/LIMITATIONS.md). The tables in SCALE.md say which numbers are measured and which are
  still hypotheses.
