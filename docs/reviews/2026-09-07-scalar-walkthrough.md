# Scalar walkthrough review — 2026-09-07

Two-phase exercise: first use the stack exactly as the README describes, through Scalar in a
browser; then fix what a stranger could not do. Times are wall-clock (UTC) from a fresh session.

## Phase 1 — Reviewer log (before any changes)

| Time | What happened |
| --- | --- |
| 15:05:00 | Started in `C:\Projects\reviewed-repo`. It contains a single PDF and no README. Found the repo at `C:\Projects\statement-delivery-system`. **Stumble 0: the reviewed directory was not the repo.** |
| 15:05:16 | Read the README. It says Scalar exists and gives one URL, `http://localhost:8081/scalar/v1`, in the "What / Where" table. The Download Gateway has no Scalar URL listed. The walkthrough is curl/PowerShell only; Scalar is never named as a way to run it. |
| 15:05:36 | `docker compose up --build -d`. Images were cached; all services healthy by 15:06:07. `seed-demo` exited 0 at ~15:07:30. Matches the README exactly. |
| 15:07:40 | Opened Scalar. It loads. First impression: sidebar groups are C# class names (`AuditVerifyEndpoint`, `LegalHoldEndpoints`, `ErasureEndpoints`, ...). Statements are the sixth group down; Audit, Legal holds and Erasure come first. No introduction text. No Authorize button anywhere. |
| 15:08:30 | Fetched both OpenAPI documents. Neither declares a security scheme. Every operation has a summary; six have a description. No parameter has an example value. The `POST /v1/dev/tokens` endpoint the README relies on is **not in the document**, so it cannot be discovered or called from Scalar. |
| 15:08:40 | Guessed `http://localhost:8082/scalar/v1`. It exists and works. **Had to guess: the README does not list it.** |
| 15:09:02 | Token: the README says a plain `GET` of `/v1/dev/tokens?customerId=…` works in a browser. Opened it in a new tab; got JSON with `accessToken`. Copied it by hand. |
| 15:10:00 | Scalar deep-link `#tag/statementendpoints/GET/...` rendered a blank page. Reloaded the root and clicked through the sidebar instead. |
| 15:11:30 | Opened "Lists a customer's available statements" → Test Request. The client has no Auth section. Filled `customerId` with the README's demo id, typed `from`/`to`, added an `Authorization` header row by hand. |
| 15:12:30 | Send → **401**. The header row had not committed before Send; the request went out with an empty `authorization`. Sent again. |
| 15:13:30 | **400 Invalid date range**. Scalar had left `from`/`to` unticked because the spec marks them optional, so it dropped them. Ticked them; still 400 because the values typed while unticked were not applied. Retyped both. |
| 15:14:40 | **200**, one statement: `01a07c68-4763-769f-83b5-22150d2c216e`, period `2026-08-01` to `2026-08-31`, `AVAILABLE`, 58,975 bytes. First data. |
| 15:15:10 | Opened `POST /v1/statements/{statementId}/download-links` → Test Request. The Authorization header **did not carry over**; re-added it by pasting the token again. Body defaulted to `{"ttlSeconds": null}`. Set `statementId`, `period=2026-08-01`. |
| 15:16:20 | Send → **201**, `url` on `localhost:8082`. The URL was truncated in the response pane and had to be read out of the DOM. Nothing in the README or on the page says what to do with a URL on a different port from Scalar. |
| 15:16:45 | Pasted the URL into a new tab. Chrome downloaded `statement-2026-08.pdf` (58,975 bytes). |
| 15:16:49 | Pasted it again. **404 "Download unavailable"**. |
| 15:17:05 | Step 5. There is no access-log endpoint. The README's step 5 is a `docker compose exec … psql` command, so I left the browser for the terminal. It returned the five expected rows: `STATEMENT_GENERATED`, `LINK_ISSUED`, `DOWNLOAD_STARTED`, `DOWNLOAD_COMPLETED`, `ACCESS_DENIED / CONSUMED`. |

```
Could I reach Scalar from the README alone?       YES (Delivery API). NO for the Gateway — guessed.
Could I authenticate from the README alone?       YES, but not *in* Scalar: token from a browser tab,
                                                  pasted as a raw header on every request. No Authorize button.
Did I find a working test customer?               YES — README states 11111111-1111-1111-1111-111111111101.
                                                  Scalar itself shows nothing; every example is "string".
Could I complete the single-use flow?             YES — 12 minutes from Scalar open to audit rows.
Was I LED to step 4, or did I work it out?        LED, by the README's curl walkthrough. Nothing on the
                                                  Scalar page suggests redeeming twice.
```

Every point where I had to guess or leave the README:

1. The working directory was not the repo.
2. Gateway Scalar URL: guessed.
3. No Authorize button; added a header by hand, twice, because it does not persist across operations.
4. `from`/`to` are documented as required in prose but declared optional in the spec; Scalar dropped them.
5. No example values anywhere: `customerId`, `statementId`, `period` all had to be typed from the README.
6. The dev-token endpoint is invisible in Scalar; the README is the only place it exists.
7. The download URL is on another port; nothing says "paste it into a new tab".
8. Step 5 (audit) needs the terminal and Docker; there is no endpoint.
9. The Scalar page has no introduction, so a stranger who lands there without the README has no starting point.

Timings, before:

| | Before |
| --- | --- |
| Time to Scalar | 2 min 40 s |
| Time to authenticated call | 8 min (200 at 15:14:40; first 401 at 15:12:30) |
| Time to first data | 9 min 40 s |
| Completed single-use flow | Yes, 12 min |
| Was led to step 6 (redeem twice) | By README only; not by the page |
| Times I left the README | 4 (repo location, gateway URL, header persistence, audit via psql) |

Screenshots: `docs/reviews/img/phase1-scalar-top.jpg`, `docs/reviews/img/phase1-first-401.jpg`.

## What was missing

1. **No Scalar path in the README.** Scalar was one row in a table; the walkthrough was curl only,
   and the Gateway's Scalar URL was not listed at all.
2. **No security scheme in the OpenAPI document**, so Scalar had no authentication UI. The token had
   to be added as a raw header, on every operation, and did not persist.
3. **The token endpoint was excluded from the document**, so the only way to find it was the README.
4. **No document description**: the page opened on a bare title with no starting point.
5. **Tags were C# class names in registration order**: Audit, Legal holds and Erasure came before
   Statements.
6. **No example values**, and `from`/`to`/`period` were declared optional although the handlers
   reject requests without them. First click: 400.
7. **Nothing on the page said to redeem the link twice.** The README's curl steps did; a
   Scalar-only reviewer would not have seen it.
8. **No token script.** The README relied on the API endpoint, which is fine, but nothing piped
   cleanly into a variable and there was no documented way to mint a staff or DPO token from a shell.
9. **Nothing said what to do with a URL on port 8082** while sitting on a port 8081 page.
10. Not fixed, deliberately: the audit step needs a terminal. See "still missing" below.

## What was built or changed

| Gap | Change |
| --- | --- |
| 1, 9 | README: new **Explore the API** section listing both Scalar URLs, how to authenticate on the page and from a script, and the fixed test customer. **See it work in five minutes** is now Scalar-first, with the terminal version retained below it as "the same five steps from a terminal". Step 5 says the URL is on the gateway and to paste it into a new tab; step 6 (paste it again, 404) is a block quote in bold. |
| 2 | `DeliveryApiOpenApi` document transformer adds an HTTP bearer security scheme; an operation transformer attaches it to every operation that is not `AllowAnonymous`. Scalar now shows a Bearer Token box on the intro card and in every request modal. `MapScalarApiReference` prefers that scheme and enables persistent authentication, so the token survives a reload. |
| 3 | `DevTokenEndpoint` is included in the document under a **Start here** tag, with a description that says what to do with the result. Still mapped only inside the Development guard; the security test that asserts that still passes. |
| 4 | `info.description` carries the six-step walkthrough, ending with the second-redeem-404 step in bold. The gateway document has its own description that sends the reader to the Delivery API page first and says "paste it again: 404". |
| 5 | Every endpoint group has `WithTags` with a readable name, and the document transformer emits `tags` in reading order: Start here, Statements, Download links, Audit, Legal holds, Erasure, Restore, Reconciliation, Statement runs. Scalar renders that order. |
| 6 | Operation transformer sets examples: `customerId` = the demo customer, `from` = twelve months ago, `to` = today, `period` = the first of last month (the seeded statement), `ttlSeconds` = 600. `from`, `to` and `period` are marked required. `statementId` has no fixed example (UUIDv7, minted at render time) so its description says where to copy it from. |
| 7 | Covered by 4 and by the Download links tag description. |
| 8 | `scripts/demo-token.sh` and `scripts/demo-token.ps1`. Both read `JWT_DEV_SIGNING_KEY`, issuer and audience from `.env`, sign HS256 locally, accept `customer` (default), `staff` or `dpo` plus an optional customer id, print only the token on stdout and hints on stderr. Verified: the customer token gets 200 on the catalogue and 403 on `/v1/audit/verify`; staff and dpo tokens get 200 there. |

Files: `src/Services/Delivery.Api/Configuration/DeliveryApiOpenApi.cs` (new),
`src/Services/Download.Gateway/Configuration/DownloadGatewayOpenApi.cs` (new), `scripts/` (new),
`README.md`, the two `Program.cs` files, both `*Extensions.cs` files, and `WithTags` on nine
endpoint files. Builds warning-free; `dotnet format --verify-no-changes` clean; Unit (419),
Architecture (41) and Security (54) suites pass.

Demo customer verified the hard way: `docker compose down -v` then `up --build`; the seed took
about a minute after the services were healthy, and `11111111-1111-1111-1111-111111111101` came
back with last month's statement. The ids are constants in `tools/seed/DemoRun.cs`.

## Phase 3: fresh reviewer, updated README

Storage cleared, fresh Scalar session, README's Explore-the-API section and five-minute path only.

| Time | What happened |
| --- | --- |
| 15:31:35 | Rebuilt stack healthy and seeded from empty volumes. |
| 15:32:05 | Opened Scalar. The page opens on "Start here" with the six steps and a Bearer Token box. Sidebar order: Start here, Statements, Download links, ... |
| 15:33:50 | Step 1: `GET /v1/dev/tokens`, Test Request. `customerId` pre-filled. Send: 200 with `accessToken`. |
| 15:34:10 | Step 2: pasted into the Bearer Token box. |
| 15:34:58 | Step 3: Statements, list, Test Request. Bearer token already present ("Authentication: Required, Bearer"), `customerId`, `from`, `to` pre-filled and ticked. Send: **200, first data**. Copied `id` and `period.start`. |
| 15:36 | Step 4: Download links, POST, Test Request. `period` pre-filled, body `{"ttlSeconds": 600}`. Typed the `id`. **Stumble:** a stray Tab moved focus and the page re-rendered blank (a Scalar rendering quirk seen in Phase 1 too); reloaded. After the reload the Bearer Token box was empty: Scalar's auth persistence is off by default. Re-minted with `./scripts/demo-token.sh` as the README offers, pasted again. Fixed afterwards with `EnablePersistentAuthentication()`; verified the token now survives a reload. |
| 15:40:03 | Send: **201**, `url` on port 8082. |
| 15:40:15 | Step 5: pasted into a new tab; `statement-2026-08 (1).pdf` (58,975 bytes) downloaded. |
| 15:40:19 | Step 6: pasted again: **404 "Download unavailable"**. Led here by the README and by the page. |
| 15:40:38 | Step 7: the README's one terminal command. Newest five rows: `ACCESS_DENIED / CONSUMED`, `DOWNLOAD_COMPLETED`, `DOWNLOAD_STARTED`, `LINK_ISSUED`, `STATEMENT_LIST_VIEWED`. |

| | Before | After |
| --- | --- | --- |
| Time to Scalar | 2 min 40 s | 30 s (the URL is the first thing under Explore the API) |
| Time to authenticated call | 8 min, after a 401 and two 400s | 3 min, first attempt 200 |
| Time to first data | 9 min 40 s | 2 min 53 s |
| Completed single-use flow | Yes, 12 min | Yes, 8 min 30 s including a 3-minute reload detour |
| Was led to step 6 | By README only | By README **and** by the page's Start-here block and the Download links tag |
| Times I left the README | 4 | 1 (the audit query, which the README itself sends you to the terminal for) |

## Found in Phase 3 that Phase 1 did not reveal

- **Scalar does not persist auth across a reload by default.** In Phase 1 there was no auth UI at
  all, so this could not show. Fixed with `EnablePersistentAuthentication()`; re-verified.
- **There is no button labelled "Authorize".** Scalar 2.17 puts a Bearer Token box under an
  "Authentication" heading on the intro card and in every request modal. The README, the document
  description, the tag description and the script hints all now say "Bearer Token box".
- **Scalar sends an empty header if you click Send before the field commits.** Seen in both phases.
  Pressing Tab or clicking elsewhere before Send avoids it. Not fixable from this side.
- **Scalar's deep links and some sidebar clicks render a blank page** until reloaded. Also not
  fixable from this side; the README does not link into the page, so a reviewer starts at the top.
- **The GET and POST forms of `/v1/dev/tokens` both appear** because the endpoint is mapped for
  both methods. Harmless, and the GET one is the one a browser can open.

## Still missing, and why

- **An access-log endpoint.** Step 7 still needs the terminal. The audit trail is deliberately not
  exposed to customers: the `ACCESS_DENIED` row for a replay carries no statement or customer id so
  that a denial cannot confirm what a token pointed at, and an endpoint returning "your" audit rows
  would have to decide what to do with rows that are anonymous by design. `GET /v1/audit/verify`
  exists for staff and is callable from Scalar, but it verifies chains rather than listing events.
  Adding a customer-facing access log is a product decision with threat-model consequences, so it
  is left as a finding rather than built.
- **A fixed example for `statementId`.** Ids are UUIDv7 minted at render time. Pinning one would
  mean changing the seed to insert a known id, which touches the generation run. The description
  now says where to copy it from, and the list call that produces it is one click earlier.
- **The reviewed directory.** `C:\Projects\reviewed-repo` still contains only a PDF. The repo is
  `C:\Projects\statement-delivery-system`; whoever set up the review should point it there.

Screenshots: `img/phase3-scalar-top.jpg` (top of document), `img/phase3-download-links-expanded.jpg`
(an endpoint group expanded), `img/phase3-first-data.jpg`, `img/phase3-issue-link-201.jpg`,
`img/phase3-second-redeem-404.jpg`, `img/phase3-gateway-scalar.jpg`.
