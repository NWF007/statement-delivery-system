# Scalar walkthrough, second pass (2026-09-07)

A fresh-session re-run of the reviewer exercise in
[2026-09-07-scalar-walkthrough.md](2026-09-07-scalar-walkthrough.md): behave as a stranger who
follows only the README and the Scalar page, log every stumble, then fix only what the log found.
The first pass had already put the Scalar section, the five-minute path, the on-page "Start
here" block, the token endpoint, the fixed demo customer and the token scripts in place. This
pass found no blockers and five pieces of friction.

## Phase 1: reviewer log

Times are CUT, 2026-09-07. The stack was torn down with `docker compose down -v` first, both to
behave like a cold start and to test the README's claim that the demo customer id survives it.

| Time | What happened |
| --- | --- |
| 15:50 | Read the README. The top banner points at *See it work in five minutes*; Quickstart is `cp .env.example .env && docker compose up --build -d`. |
| 15:50 | `down -v`, then `up --build -d`. |
| 15:54 | All fourteen containers in the documented state; `seed-demo` `Exited (0)`. About 3.5 minutes, inside the README's estimate. |
| 15:54 | README's *Explore the API* table gives both Scalar URLs. Opened `:8081/scalar/v1`. Loads; the six-step *Start here* block is the first thing on the page; groups are in usage order. |
| 15:54 | **Stumble 1.** The Bearer Token box already held a masked token. Scalar keeps it in the browser's local storage, so a browser that had seen the stack before shows a stale token, and the README did not say so. Cleared it. |
| 15:54 | **Stumble 2.** Both *Start here* sidebar entries read "Mints a development bearer token. Step 1 of the walkthrough." One endpoint answers GET and POST, so both operations inherit one summary; only the method badge differs. |
| 15:54 | Opened `GET /v1/dev/tokens`. Description says exactly what to do; `customerId` pre-filled with the demo id. **Stumble 3.** The 200 response on the page shows "No Body": the handler returns an anonymous object, so the document has no shape for it. |
| 15:55 | Test Request, Send: 200 in 30 ms. **Stumble 4.** The token is 317 characters and clipped in the response pane; a partial selection is easy. Pasted it into the Bearer Token box. |
| 15:55 | *Statements*, `GET /v1/customers/{customerId}/statements`. The curl preview already carries the Bearer header. Customer id, `from`, `to`, `limit` all pre-filled and ticked. 200 in 110 ms: one statement, exactly as README step 3 says. **Stumble 5.** The static 200 example on the page shows `"id": "string"`, `"status": "string"`. |
| 15:56 | *Download links*. The tag description already says to open the url twice. `period` pre-filled with last month; `statementId` empty, with a description saying where to copy it from and why there is no fixed example. Pasted the id. 201 in 142 ms. |
| 15:57 | New tab, pasted the url: `statement-2026-08.pdf`, 58 975 bytes, the same as `sizeBytes`. README step 5 had said "new tab" in advance. |
| 15:57 | Pasted it again: 404 `application/problem+json`, "Download unavailable". README step 6 is a bold blockquote, and the Scalar intro, the *Download links* tag and the gateway intro all say the same thing. **Led, not worked out.** |
| 15:57 | README step 7, the `psql` one-liner: `ACCESS_DENIED / CONSUMED`, `DOWNLOAD_COMPLETED`, `DOWNLOAD_STARTED`, `LINK_ISSUED`, `STATEMENT_LIST_VIEWED`. Matches the README to the row. |
| 15:58 | Gateway page at `:8082/scalar/v1`: "Start there, not here", "Paste it again: 404". One real endpoint plus a stray `Download.Gateway` group holding `/ping`. |
| 15:59 | Read the whole document: 22 operations. Every walkthrough operation has a summary and a description. The operator surfaces (legal holds, erasure, restore, reconciliation, statement runs: 13 operations) have summaries only. `/ping` sits in a default `Delivery.Api` group. |
| 16:00 | `scripts/demo-token.sh` and `.ps1`: token only on stdout, hint on stderr, `staff` / `dpo` arguments work, no arguments works. |

| Question | Answer |
| --- | --- |
| Could I reach Scalar from the README alone? | Yes |
| Could I authenticate from the README alone? | Yes, using the on-page token endpoint |
| Did I find a working test customer? | Yes. The README states it, Scalar pre-fills it, and it survived `down -v` |
| Could I complete the single-use flow? | Yes |
| Was I led to the replay, or did I work it out? | Led, by four separate places |
| Points where I had to guess | None |
| Points where I left the README | Once, for step 7 (psql), and the README sent me there |

## What was missing

Nothing that blocks a reviewer. Five pieces of friction, in the order a reader meets them:

1. No warning that Scalar persists the Bearer Token box, so a browser that has seen the stack before starts with a stale token.
2. Both token operations share one sidebar label.
3. The token endpoint's 200 shows "No Body" on the page.
4. No warning that the token is long and the response pane clips it.
5. Static response examples for the walkthrough's three responses are `"string"` placeholders.

Found while reading the page rather than while walking it:

6. Thirteen operator operations have no description.
7. `/ping` falls into a group named after the assembly (`Delivery.Api`, `Download.Gateway`) on both pages.

## What changed, per item

| Item | Change | Where |
| --- | --- | --- |
| 1, 4 | Two bullets after the "paste the token" paragraph: the token is about 320 characters and the pane clips it; the box lives in local storage, so clear it and paste a fresh token if a request answers 401. Step 1 of the five-minute path now says "copy the whole token". | `README.md` |
| 2 | The operation transformer gives the GET and POST operations distinct summaries: "Mint a development token (GET, also works pasted into a browser tab)" and "(POST, for scripts and curl)". | `DeliveryApiOpenApi.DescribeOperationAsync` |
| 3 | The same transformer adds a 200 example body (`accessToken`, `tokenType`, `expiresInSeconds`, `subject`, `scope`) to both token operations. | `DeliveryApiOpenApi.DescribeOperationAsync` |
| 5 | A schema transformer sets realistic examples on `StatementResponse`, `PeriodResponse`, `StatementPageResponse` and `IssueLinkResponse`, using the values the demo seed produces (last month's period, `AVAILABLE`, 58 975 bytes, a port-8082 url). | `DeliveryApiOpenApi.ExampleSchemaAsync` |
| 6 | `WithDescription` on all thirteen operator operations, each checked against the handler: staff/DPO scope, what the call actually does, and the status codes it answers (the 409 bodies that cite a case or statute, the 404 for a released hold, the 202-and-poll shape of restore and reconciliation, the per-period idempotency of runs). | `Retention/*.cs`, `Runs/StatementRunEndpoints.cs` |
| 7 | `/ping` is tagged `Operations` where it is mapped, with a description, and both documents list an `Operations` tag last. | `ServiceDefaultsExtensions.cs`, `DeliveryApiOpenApi.cs`, `DownloadGatewayOpenApi.cs` |

Nothing in the runtime path changed. `dotnet build` is clean for both services; the unit (419),
architecture (41) and security (54) suites pass.

## Phase 3: re-run with cleared browser storage

Scalar's local storage was wiped, the page reloaded, and the flow walked again from the README.

| | Before | After |
| --- | --- | --- |
| Time to Scalar | 4 min (all of it the build) | 0 min (stack already up) |
| Time to authenticated call | 5 min 30 s from `up`; ~1 min from page load | 45 s from page load |
| Time to first data | 6 min from `up`; ~1.5 min from page load | 1 min 15 s from page load |
| Completed single-use flow | Yes | Yes |
| Was led to the replay | Yes | Yes |
| Times I left the README | 1 (step 7, as instructed) | 1 (same) |
| Stumbles | 5 | 0 |

Page-load-to-404: 15:54 to 15:57 before, 16:07:07 to 16:09:20 after. The build dominates the
"before" wall clock; the honest comparison is the per-page numbers.

## Screenshots

Before: [top of the document](img/2026-09-07b-before-top.jpg) (note the pre-filled token box and
the duplicated sidebar labels), [the token endpoint](img/2026-09-07b-before-dev-tokens.jpg) (note
"No Body" under 200), [the replay 404](img/2026-09-07b-replay-404.jpg),
[the gateway page](img/2026-09-07b-gateway-top.jpg).

After: [top of the document](img/2026-09-07b-after-top.jpg),
[the token endpoint](img/2026-09-07b-after-dev-tokens.jpg) (200 example present),
[the statements list](img/2026-09-07b-after-statements.jpg) (realistic example),
[download links](img/2026-09-07b-after-download-links.jpg).

## Found beyond Phase 1

- `dotnet test` on the review machine reports "Zero tests ran, error: 1" for every project, with
  or without `--project`, and gives no further detail. The compiled test executables run and pass
  when launched directly (`tests/<Project>/bin/Debug/net10.0/<Project>.exe`). This looks like a
  local SDK/testing-platform handshake problem, not a repository one; the CI badge is green.
- The example statement id on the page is a real UUIDv7 from this stack. The id changes on every
  `down -v` because it is time-ordered, which is why the `statementId` parameter carries a
  description instead of an example; the response example is illustrative only.

## Still missing, and why

- **Step 7 stays in the terminal.** The audit chain is not exposed to customers by design, and a
  staff-scoped read endpoint would be a product decision, not a documentation fix. The README
  says up front that step 7 is the one terminal step, and a staff token can run
  `GET /v1/audit/verify` from the page.
- **The token stays clipped in Scalar's response pane.** That is Scalar's rendering; the README
  now says to select the whole value or use Raw. A copy button would need a Scalar change.
- **The stale-token behaviour is Scalar's.** It is documented rather than changed, because the
  persistence is the feature that makes the token survive a reload.
