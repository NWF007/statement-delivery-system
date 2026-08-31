# Postman collection — Statement Delivery Platform

Exercises the two public services end to end:

| Service | Port | Role |
|---|---|---|
| **Delivery.Api** | `8081` | Authenticated JSON API — catalogue, link issue, compliance, operations |
| **Download.Gateway** | `8082` | Unauthenticated capability-URL redemption (`/v1/d/{token}`) |

The collection was **derived from the running APIs' OpenAPI documents**, fetched from
`http://localhost:8081/openapi/v1.json` and `http://localhost:8082/openapi/v1.json`
(the brief's `/v3/api-docs` path is Swashbuckle's; this stack uses .NET's built-in OpenAPI at
`/openapi/{document}.json`). Where the live contract and the brief disagreed, the contract won —
see **Findings** below.

Files:

```
postman/
├── StatementDelivery.postman_collection.json     51 requests, 10 folders
├── StatementDelivery.local.postman_environment.json
└── README.md
tools/postman/
├── mint-tokens.sh      prints the four dev JWTs
└── mint-tokens.ps1
```

---

## 1. Prerequisites

- The full stack running with **seeded data**. The data folders (01–08) read and write real
  statements, so they need PostgreSQL, the object store, and at least one generated statement for
  the customer the tokens are minted for. The fastest path is the demo seed:

  ```bash
  docker compose up --detach --wait
  ./scripts/seed-demo.sh            # prints a CUSTOMER_ID / STATEMENT_ID / PERIOD
  ```

- Both services in **Development** environment — the in-band token mint (`/v1/dev/tokens`) is
  Development-only by design.

## 2. Import

In Postman: **Import** → select both JSON files. Choose the *Statement Delivery — Local*
environment in the top-right selector. Newman:

```bash
newman run postman/StatementDelivery.postman_collection.json \
  -e postman/StatementDelivery.local.postman_environment.json
```

## 3. Minting the four JWTs

The stack signs dev JWTs with `Jwt:DevelopmentSigningKey` (HMAC-SHA256), issuer
`https://localhost/statement-delivery-dev`, audience `statement-delivery-api`. **You do not need
the key** — `/v1/dev/tokens` signs on your behalf.

**Automatic (default).** The collection's pre-request script mints all four the first time you run
it, into the four `jwt*` secret variables, and never re-mints a variable that is already set. A
fresh clone works from a single click.

**Manual.** If you'd rather paste them:

```bash
DELIVERY_URL=http://localhost:8081 ./tools/postman/mint-tokens.sh
#   or
pwsh ./tools/postman/mint-tokens.ps1
```

Required claims, and how each token gets them:

| Token | Query to `/v1/dev/tokens` | Claims |
|---|---|---|
| `jwtCustomerA` | `?customerId=A` | `sub=A`, `iss`, `aud`, `exp` |
| `jwtCustomerB` | `?customerId=B` | `sub=B` (for the IDOR tests) |
| `jwtStaff` | `?customerId=A&staff=true` | adds `scope: audit.verify` |
| `jwtDpo` | `?customerId=A&staff=true&dpo=true` | adds `scope: audit.verify erasure.execute` |

> **DPO scope stacks *on* staff.** `dpo=true` alone yields no scope claim — the endpoint only adds
> `erasure.execute` when **both** `staff=true` and `dpo=true` are set. The mint script and the
> collection both send both flags. (Reported under **Findings** — it surprised me too.)

`sub` **is** the customer id: every `/v1/customers/{customerId}/…` route checks the path against
the token's subject, so a token minted for customer A can only act as customer A. That is exactly
what makes the IDOR probes in folder 09 meaningful.

## 4. Run order

Run **top to bottom**. Two ordering facts matter:

- **Folder 02 must precede folder 03.** `03`'s replay case redeems the *same* token `02` already
  spent, to prove the second redemption fails. `02` deliberately copies its spent URL into a
  `spentUrl` variable for that one case.
- **Folder 07 (restore) self-polls.** Its status request re-queues itself (via
  `pm.execution.setNextRequest`) up to five times, then moves on, so an in-flight restore doesn't
  hang the run.

## 5. Why download URLs are cleared after use

**This is deliberate, not an oversight.** A download token is *single-use*: it is consumed
atomically on first redemption. Three consequences the collection handles on purpose:

1. **No accidental reuse.** After the happy-path download, `02` runs
   `pm.environment.unset("downloadUrl")`. If the live URL lingered in a variable, a later request —
   or a Postman client that speculatively prefetches URLs — could redeem it a second time and
   consume it for the wrong reason.
2. **Each denial case issues its own fresh link.** Folder `03`'s *revoked* case mints a link in its
   pre-request, deletes it, then redeems it. Sharing one link across denial cases would have the
   first case consume it and the rest assert the wrong failure.
3. **The one deliberate exception is the replay case**, which keeps a single copy of the spent URL
   in `spentUrl` — because proving replay *requires* a spent token.

## 6. What each folder demonstrates

| Folder | Demonstrates |
|---|---|
| **00 Setup** | Stack reachable; four JWTs minted in-band; a real statement id + partition key captured |
| **01 Catalogue** | List + single lookup; the **mandatory date range** (400 without it, 400 over 84 months); cursor round-trip; **no crypto material** ever in a catalogue response |
| **02 Download — happy path** | Issue single-use link → redeem → `%PDF` + every security header (`no-store`, `attachment`, `nosniff`, `no-referrer`) |
| **03 Download — denials** | Replay, revoked, malformed, wrong-length, unknown, cross-customer — and **★ all denials byte-identical** (the uniform-denial property, live) |
| **04 Audit** | All 16 chains verify; staff-only scope (customer → 403, no token → 401) |
| **05 Legal hold** | Both hold scopes placed and listed; missing `caseReference` → 400; released |
| **06 Erasure** | 409s that **cite their basis** (`LEGAL_HOLD`+`caseReference`, or `STATUTORY_RETENTION`+`basis`+`retainUntil`) asserted on the payload, not just the status; scope gates (staff→403, customer→403); cancel |
| **07 Archive & restore** | 409 with a working `restoreEndpoint`; restore → poll → download |
| **08 Operations** | Run **idempotency** (same period → same `runId`); failures + retry; clean reconciliation |
| **09 Security probes** | IDOR resources are **404, never 403**; scope violations; the gateway's deliberate unauthenticated 404 |

Two collection-level scripts run on **every** request: one asserts no internal detail leaks
(stack frames, SQL, Npgsql, connection strings, crypto column names), the other asserts every JSON
error carries a `traceId`.

## 7. CI

```bash
newman run postman/StatementDelivery.postman_collection.json \
  -e postman/StatementDelivery.local.postman_environment.json \
  --folder "09 Security probes" \
  --reporters cli,json
```

Drop `--folder` to run everything. `--reporters cli,json` writes a machine-readable report
alongside the console output.

## 8. Known limitations

- **A seeded, fully-running stack is required for folders 01–08.** They read and write real data;
  against a partial stack (no database or object store) the data-touching requests return 5xx.
  Folder 00's discovery request tolerates that with a warning so the setup folder stays honest
  about the stack it was pointed at; the auth-layer probes in folders 04/06/09 (401/403) pass even
  without a database, because authorization runs before any data access.
- **Archived-statement restore (folder 07)** needs a statement in `ARCHIVED` state. On an
  all-`AVAILABLE` seed the link-issue is a 201 rather than a 409; the folder asserts both so it is
  honest either way, but to see the restore path you must archive a statement first (the retention
  worker does this on its schedule, or archive one by hand for the demo).
- **The uniform-denial assertion (03)** compares whatever denial bodies were captured in that run;
  it needs at least the malformed and unknown cases to have executed against the gateway.

---

## Findings — where the API and the brief/spec disagree

Reported as the brief asked, contract-wins:

1. **No per-statement `access-log` endpoint exists.** The brief's folder 04 and §5 call for
   `GET /v1/statements/{id}/access-log`; there is no such route in the code or either OpenAPI
   document. Access events live in the audit chain, verified through `GET /v1/audit/verify`. The
   collection's folder 04 does chain verification and scope, and notes the absence in its
   description. **This is the one requested capability that could not be built — because the API
   does not offer it.**

2. **Endpoints present in code but absent from OpenAPI.** `/health/live`, `/health/ready` and
   `/v1/dev/tokens` are mapped but undocumented in the generated spec. Health probes being
   undocumented is conventional; `/v1/dev/tokens` is intentionally Development-only. All three are
   in the collection (folder 00 and the mint scripts).

3. **Several success responses are mislabelled `200` in OpenAPI.** These endpoints return other 2xx
   codes at runtime but lack `.Produces<T>(statusCode)` annotations, so the generator defaults to
   documenting `200`:
   - `POST /v1/statements/{id}/legal-holds` and `POST /v1/customers/{id}/legal-holds` → **201**
   - `POST /v1/admin/reconciliation/run` → **202**
   - `DELETE /v1/customers/{id}/erasure` (cancel) → **204** (matches the brief; contradicts the spec)
   - `POST /v1/customers/{id}/erasure` (permitted) → **202**

   The collection asserts the **runtime** codes. A documentation-only gap — the behaviour is
   correct — worth a `.Produces` pass on those endpoints.

4. **`DELETE /v1/legal-holds/{holdId}` takes a request body** (`{ releaseReason }`), which the
   brief showed without one. Minimal APIs require the body to be explicitly `[FromBody]` on DELETE;
   the collection sends it.

5. **DPO scope requires `staff=true&dpo=true`** on the dev-token mint (see §3). Documented, not a
   defect — but non-obvious, so worth stating.

None of these is a security or correctness defect; items 1–2 are coverage gaps in the OpenAPI
surface and item 3 is a documentation-annotation gap.
