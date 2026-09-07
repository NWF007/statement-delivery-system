# Stepping through the code

How to run one service under a debugger on the host while everything it depends on keeps running
in compose, then drive it from Postman and follow a request through the code.

## 1. Start the stack, then seed it

```bash
docker compose up -d --wait
./scripts/seed-demo.sh          # 25 customers + one real generation run for last month
```

The script needs `jq` or `python` on the host. Without either, do its two data steps by hand:

```bash
set -a; . ./.env; set +a
Postgres__PrimaryConnectionString="Host=localhost;Port=6432;Database=statements_generation;Username=app_generation;Password=$APP_GENERATION_PASSWORD" \
  dotnet run --project tools/seed -c Release -- --customers 25 --months 3 --seed 7
# then, in Postman, folder "08 Operations" > "Create statement run" for last month, and wait for
# GET /v1/statement-runs/{runId} to report COMPLETED (about a minute).
```

## 2. Run the service on the host

`.vscode/launch.json` (local only, git-ignored) has two configurations and one compound:

| Configuration | Listens on | Notes |
| --- | --- | --- |
| Delivery.Api (host, :5081) | `http://localhost:5081` | Talks to PgBouncer 6432, Redis 6379, MinIO 9000 in compose |
| Download.Gateway (host, :5082) | `http://localhost:5082` | Same, plus the local crypto key |
| Api + Gateway (host) | both | Set `DownloadLinks__GatewayBaseUrl` to `http://localhost:5082` in the API config first, so issued links point at the gateway you are debugging |

The containerised copies on 8081/8082 keep running; the host copies use the same database roles
and the same bucket, so both see the same data. Pick a configuration in VS Code's Run and Debug
panel (C# Dev Kit is installed) and press F5. The pre-launch task builds the project in Debug.

Sanity check once it is up:

```bash
curl.exe -f http://localhost:5081/health/ready
```

## 3. Point Postman at it

Import ONE file: `postman/StatementDelivery.hostdebug.postman_collection.json`. It is the standard
collection with the environment baked in as collection variables (`deliveryUrl` on `:5081`, two
seeded customer ids) and its scripts rewritten to use collection variables, so no environment file
is needed and nothing is typed as a Postman secret. That sidesteps the "Unlock vault" and "Secrets
detected" dialogs that the separate environment file can trigger. Select "No Environment". Folder
`00 Setup` mints the four JWTs on first run and discovers a real statement id.

If you prefer the two-file layout, import the original collection plus
`postman/StatementDelivery.hostdebug.postman_environment.json` and select that environment.

If you re-seed, the customer ids change: pick two from

```bash
docker compose exec -T -e PGPASSWORD=local-dev-postgres-password postgres psql -U postgres -d statements -tA -c \
  "SELECT DISTINCT s.customer_id FROM statement s JOIN statement_run_item i ON i.statement_id = s.id LIMIT 2;"
```

## 4. Where to put breakpoints

The download path is the one everything else exists to protect. Follow it in this order.

**Delivery.Api**

| Step | File | Method |
| --- | --- | --- |
| Token minted (Development only) | `src/Services/Delivery.Api/Statements/DevTokenEndpoint.cs` | the lambda mapped to `/v1/dev/tokens` |
| Catalogue list; mandatory date range; ownership is a WHERE predicate on the JWT `sub` | `src/Services/Delivery.Api/Statements/StatementEndpoints.cs` | `ListStatementsAsync` |
| Single lookup; 404-not-403 for another customer's statement | same file | `GetStatementAsync` |
| Issue a single-use link: CSPRNG token, SHA-256 stored, plaintext returned once, audit `LINK_ISSUED` in the same transaction | `src/Services/Delivery.Api/Downloads/DownloadLinkEndpoints.cs` | `IssueAsync` |
| The audit append every step above ends in | `src/BuildingBlocks/Persistence/Auditing/AuditWriter.cs` | `AppendAsync` |

**Download.Gateway** (run the compound configuration to step here)

| Step | File | Method |
| --- | --- | --- |
| Redemption entry; steps 4 to 7 consume, resolve, audit and commit before a single byte streams | `src/Services/Download.Gateway/Downloads/DownloadEndpoints.cs` | `RedeemAsync` |
| The atomic `UPDATE ... RETURNING` that makes exactly one concurrent redeemer win | `src/BuildingBlocks/Persistence/Tokens/DownloadTokenRepository.cs` | `ConsumeAsync` |
| Every denial, and the timing floor that keeps them uniform | `DownloadEndpoints.cs` | `DenyAsync`, `PadAndDenyAsync` |
| Ciphertext opened from the object store and decrypted frame by frame | `DownloadEndpoints.cs` then `src/BuildingBlocks/Crypto/Framing/FramedDecryptingStream.cs` | `StreamedStatementResult.ExecuteAsync` |
| Where the per-customer key is unwrapped (and where erasure makes this fail forever) | `src/BuildingBlocks/Crypto/Keys/KeyProvider.cs` | |

A good first session: breakpoint on `IssueAsync`, run `02 Download - happy path` in Postman,
watch the token get hashed and the audit row written, continue, then breakpoint on `RedeemAsync`
and `ConsumeAsync` and send the redemption. Send the same request again and stop in `DenyAsync`
to see the replay recorded as `CONSUMED` in the audit trail while the response stays a plain 404.

## 4b. Folder by folder, with the breakpoint for each

Run the compound "Api + Gateway (host)" configuration for folders 02, 03 and 07; the API alone
is enough for the rest. All files below are under `src/Services/Delivery.Api/` unless stated.

| Postman folder | Breakpoint | What you will see |
| --- | --- | --- |
| 00 Setup | `Statements/DevTokenEndpoint.cs`, the `/v1/dev/tokens` lambda | Four tokens minted; `staff` adds `audit.verify`, `staff+dpo` adds `erasure.execute` |
| 01 Catalogue | `Statements/StatementEndpoints.cs` `ListStatementsAsync` | The 84-month range check, the keyset cursor, the JWT `sub` becoming the WHERE predicate |
| 02 Download, happy path | `Downloads/DownloadLinkEndpoints.cs` `IssueAsync`, then `Download.Gateway/Downloads/DownloadEndpoints.cs` `RedeemAsync` and `Persistence/Tokens/DownloadTokenRepository.cs` `ConsumeAsync` | Token hashed at issue; the atomic consume and audit commit before any byte streams |
| 03 Download, denials | `DownloadEndpoints.cs` `DenyAsync` and `PadAndDenyAsync` | Six different reasons, one identical 404, the timing floor; the real reason only in the audit row |
| 04 Audit | `Auditing/AuditVerifyEndpoint.cs` `VerifyAsync`, then `Persistence/Auditing/PostgresAuditVerifier.cs` | All 16 chains walked and re-hashed; scope check rejects the customer token before any query |
| 05 Legal hold | `Retention/LegalHoldEndpoints.cs` `PlaceOnStatementAsync`, `PlaceOnCustomerAsync`, `ReleaseAsync` | The DB row and the object-store hold written as a pair (dual-layer, ADR-0037) |
| 06 Erasure | `Retention/ErasureEndpoints.cs` `RequestAsync`, then `Domain/Retention/RetentionDecisionEngine.cs` | The pure decision: hold outranks Object Lock outranks statute; the 409 carries the basis and date |
| 07 Archive and restore | `Retention/RestoreEndpoints.cs` `RequestAsync`, `StatusAsync` | 409 with a restore endpoint on an archived statement; the folder self-polls |
| 08 Operations | `Runs/StatementRunEndpoints.cs` `CreateAsync`, `RetryFailuresAsync`; `Retention/ReconciliationEndpoints.cs` `RunAsync`, `LatestAsync` | Same period returns the same runId (idempotency); reconciliation is picked up by the worker on its own cadence |
| 09 Security probes | `StatementEndpoints.cs` `GetStatementAsync` | Another customer's statement id resolves to 404, never 403 |

## 5. Things that will look wrong but are not

- Every service logs one warning at startup that no read replica is configured. Intentional.
- `01 Catalogue` rejects `to=2099-01-01`: the range may not exceed 84 months. The collection uses
  `2020-01-01` to `2026-12-31`.
- With `justMyCode` on, the debugger skips framework and Npgsql frames. Turn it off in
  `launch.json` if you want to see the transaction pooler round-trips.
- Stopping at a breakpoint for longer than the gateway's request timeout will make Postman report
  a failed request; the code you are inspecting is still fine.
