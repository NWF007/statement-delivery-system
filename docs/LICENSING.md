# Licensing

Every dependency this system ships with, its licence, and the two commercial judgements that
required actual analysis. Package versions are pinned in `Directory.Packages.props`; this file is
reviewed whenever that one changes.

## The one that needs a decision: QuestPDF

**Licence:** dual — [QuestPDF Community](https://www.questpdf.com/license/) or commercial.
**Used by:** `BuildingBlocks/Rendering` only, behind the `IStatementRenderer` port.

The Community licence is free for individuals, open-source projects, and companies with **annual
gross revenue under $1 million USD**. Above that threshold a Professional licence (~$999
perpetual, up to 10 developers) or Enterprise (~$2,999) is required. Two properties of the
threshold catch people:

- It is measured on the **licensee company's total annual gross revenue**, not on revenue from
  the product using the library. A company at $1,000,001 owes the fee in full even if the PDF
  feature earns nothing.
- The declaration is asserted in code at startup (`QuestPDF.Settings.License`). This repository
  reads it from configuration (`Rendering:License`), so **each deployment declares its own
  tier** — the code never hard-codes a legal claim its operator might not hold.

**Position for this repository:** Community. It is an individual portfolio project with no
revenue. That declaration is honest today and is the default in every config file.

**Position for enterprise adoption:** a bank fails the revenue threshold by four or five orders
of magnitude and must purchase Professional or Enterprise before the first production deploy.
At ~$3k perpetual against the alternatives below, that is likely the correct purchase — but if
procurement rejects it:

**Migration path:** [PDFsharp](http://www.pdfsharp.net/) — MIT, no thresholds, no revenue gates.
The `IStatementRenderer` port was shaped for exactly this exit: one implementation class
(`QuestPdfStatementRenderer`, ~250 lines) plus its DI registration, in a project
(`BuildingBlocks/Rendering`) that only Generation.Worker references. Estimated effort: 2–4 days —
one class rewritten against PDFsharp's lower-level drawing API (it has no fluent layout engine,
so pagination and the running-balance table become manual), plus re-running the determinism and
pagination test suite, which is renderer-agnostic and already exists.

## The two traps we did not walk into

**iText (7/8/9)** — **AGPL 3.0**. Deploying it in any network-accessible service obliges you to
release the **entire application's source** under AGPL; the SaaS loophole that shelters GPL does
not exist in AGPL — network use *is* distribution. The commercial exemption starts around
$3–5k/year per developer. Not used, and must never be introduced as a transitive dependency of a
"free PDF helper" package — several NuGet wrappers quietly carry it.

**wkhtmltopdf** (and its .NET wrappers: DinkToPdf, Rotativa, WkHtmlToPdfDotNet) — **archived in
2023 with unpatched critical CVEs** in its embedded Qt WebKit (SSRF via crafted HTML, arbitrary
file read). Rendering statements means rendering data derived from external systems; an archived
HTML engine in that path is a finding in any compliance audit, before the first CVE scanner runs.
Not used.

## Everything else

| Package | Licence | Notes |
|---|---|---|
| QuestPDF 2026.8.0 | Community/commercial | See above. Embedded Lato font: OFL 1.1 |
| SkiaSharp (transitive, via QuestPDF) | MIT | BSD-3 for the native Skia binaries |
| Npgsql, Npgsql.OpenTelemetry | PostgreSQL licence | |
| Dapper | Apache 2.0 | |
| dbup-postgresql | MIT | |
| AWSSDK.S3, AWSSDK.KeyManagementService | Apache 2.0 | |
| StackExchange.Redis | MIT | |
| Microsoft.* (Extensions, AspNetCore, OpenApi, Testing) | MIT | |
| OpenTelemetry.* | Apache 2.0 | |
| Asp.Versioning.Http | MIT | |
| Scalar.AspNetCore | MIT | |
| FluentValidation | Apache 2.0 | |
| AspNetCore.HealthChecks.* | MIT | |
| Polly (transitive, via Microsoft.Extensions.Http.Resilience) | BSD-3 | |
| xunit.v3 | Apache 2.0 | |
| Shouldly | BSD-3 | |
| NSubstitute | BSD-3 | |
| NetArchTest.Rules | MIT | |
| Mono.Cecil (transitive, via NetArchTest) | MIT | |
| Testcontainers.* | MIT | |
| **FluentAssertions** | — | **Deliberately absent.** v8+ moved to a paid licence (~$130/dev/yr); the suite uses Shouldly, and an architecture test (`NoProject_ShouldReference_FluentAssertions`) keeps it out. |

## Fonts

QuestPDF's embedded **Lato** is licensed under the SIL Open Font License 1.1 — free for embedding
in documents, no attribution required in the PDFs. Environment font discovery is disabled
(`Settings.UseEnvironmentFonts = false`), so no host-installed font — with whatever licence it
carries — can leak into a rendered statement.

## Review triggers

- Any change to `Directory.Packages.props`
- QuestPDF version bumps: the licence terms are re-read, not assumed stable
- Enterprise adoption: the QuestPDF tier decision above must be made before first deploy
