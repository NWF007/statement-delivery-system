# ADR-0029: Byte-deterministic PDF rendering

**Status:** Accepted · **Date:** 2026-08-30 · **Achieved — verified in-process and across process restarts**

## Context

`content_sha256` is stored on the statement row and verified on every download
(`FramedDecryptingStream` checks it as the final frame authenticates). Regeneration is a fact of
life — a crashed worker retries, an operator resets a quarantined item — and the unique
constraint `(account_id, period_start, version)` makes re-rendering the same version an
idempotent overwrite of the same object key. All of that only *verifies* if the same
`StatementDocument` produces the same bytes every time. A hash that drifts per render turns
"idempotent regeneration" into an unverifiable claim.

PDF generation is non-deterministic by default: creation timestamps, producer strings with
library versions, document IDs hashed over the current time, and host font resolution all leak
the render moment or the render machine into the bytes.

## Decision

Pin every source, then prove it with tests rather than assert it in a comment:

| Source of drift | Pin |
|---|---|
| Creation/modification timestamps | Set to **period end**, never a clock. No `DateTime.UtcNow` exists in any rendering path; `StatementDocument.GeneratedFor` is period-derived by construction |
| Producer/Creator strings | Fixed literals (`"SDP"`), no library version |
| Document ID | Skia derives it from metadata + content; with the dates pinned it is stable |
| Fonts | `Settings.UseEnvironmentFonts = false`; only QuestPDF's embedded Lato, named explicitly in every text style — the dev machine and the chiseled container resolve identically |
| Culture | Every format call is `CultureInfo.InvariantCulture`; money formatting is integer arithmetic in one function |
| Data order | The ledger sorts with a deterministic tie-break (date, amount, description) — an unstable sort under equal keys was the subtlest candidate leak |

## The proof

- `Render_SameDocument_ProducesIdenticalBytes` — two renders, one process, byte-compared.
- `Render_AcrossProcessRestart_ProducesIdenticalBytes` — the `renderhash` tool launched twice as
  real child processes; statics, font caches and JIT state do not survive, so what this compares
  is what production regeneration actually experiences. Verified: identical SHA-256
  (`25b6a03c…`) across separate invocations of QuestPDF 2026.8.0.

Both run on every build with no Docker dependency — a library bump that breaks the property
fails the suite on the machine that bumped it.

## The honest caveat

Determinism is proven **per library version and platform**. A QuestPDF or SkiaSharp upgrade may
legitimately change layout and therefore bytes — that is real regeneration, and version
arithmetic (`MAX(version)+1`, a new row, a new object) is the mechanism that absorbs it. The
property this ADR guarantees is *within* a deployment: the same document, the same binaries, the
same bytes. Cross-version hash stability was never the goal and is not claimed.

If a future library version breaks the property outright, the fallback the brief prescribes
stands: document the limitation, fall back to determinism of extracted text content, and lean on
the `(account_id, period_start, version)` unique constraint as the idempotency backstop. The two
tests are the tripwire that forces that conversation before the bytes ship.

## Revisit when

- Either determinism test fails after a dependency bump — decide: pin the old version, accept a
  version-wide regeneration, or invoke the text-content fallback.
- Statements gain user-supplied content (names in arbitrary scripts) — embedded-font coverage
  needs re-checking, and a fallback font reintroduces host resolution.
- PDF/A or signing requirements arrive: both interact with document IDs and timestamps.
