# ADR-0028: QuestPDF, with the licensing position stated rather than assumed

**Status:** Accepted · **Date:** 2026-08-30

## Context

The renderer needs a maintained, managed-code PDF library that can paginate a table of hundreds
of rows and embed fonts deterministically. The .NET field is small and every serious option has a
licensing dimension that matters more than the API:

| Library | Licence | The catch |
|---|---|---|
| QuestPDF | Dual: Community / commercial | Community is gated on the licensee's **total annual gross revenue < $1M** — measured on the company, not the product |
| iText 7+ | AGPL 3.0 | Network use counts as distribution: deploying it in any service obliges releasing the entire application under AGPL. SaaS gets no exemption |
| wkhtmltopdf wrappers | LGPL, but— | Archived 2023 with unpatched critical CVEs in embedded WebKit. A liability in any compliance audit |
| PDFsharp | MIT | Low-level drawing API: no layout engine, pagination by hand |

## Decision

**QuestPDF**, with the position documented in `docs/LICENSING.md` and three structural
safeguards:

1. **The tier is a deployment declaration, not code.** `QuestPDF.Settings.License` is set from
   `Rendering:License` configuration. This repository ships `Community` — honest for an
   individual portfolio project — and an enterprise deployment must change the value it deploys
   with, because the legal claim belongs to the licensee, not to the source.
2. **The dependency is quarantined** in `BuildingBlocks/Rendering`, referenced only by
   Generation.Worker, behind the `IStatementRenderer` port in Domain.
3. **The exit is priced:** PDFsharp behind the same port is one implementation class plus DI
   registration, estimated 2–4 days including re-running the renderer-agnostic determinism and
   pagination suites. If procurement rejects the ~$999–2,999 QuestPDF fee, that is the move.

An organisation above the threshold adopting this system buys Professional or Enterprise before
first production deploy. That sentence exists so nobody has to discover it in an audit.

## Consequences

Byte-deterministic rendering was achieved with QuestPDF 2026.8.0 (ADR-0029) — a property the
PDFsharp exit would need to re-prove. The embedded Lato font (OFL 1.1) removes host-font drift
and carries no attribution burden in the documents.

## Revisit when

- Any QuestPDF version bump: the licence terms are re-read, not assumed stable.
- The renderer needs HTML input, accessibility tagging (PDF/UA) or PDF/A archival profiles —
  re-run the library comparison; the field moves.
- An acquisition or revenue change moves the operator across the $1M threshold mid-life: the
  Community declaration becomes false the day the threshold is crossed, not at renewal.
