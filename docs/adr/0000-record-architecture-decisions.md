# ADR-0000: Record architecture decisions
**Status:** Accepted   **Date:** 2026-08-26

## Context
This scaffold will be handed to a feature team. Its expensive decisions — no ORM, plain-SQL migrations, a deliberately unauthenticated download gateway, one worker holding the only DELETE grant — are invisible in the code they produce. Code records what we did, never what we rejected. In six months someone will bolt a bearer token onto the gateway or reintroduce EF Core, and nobody left will know what that breaks.

## Options considered

| Option | Pros | Cons |
| --- | --- | --- |
| No formal record; commit messages and tribal knowledge | Zero overhead; history already exists | Rationale scattered over thousands of commits; rejected options never recorded; leaves with the author |
| Wiki or Confluence space | Rich editing; readable by non-engineers | Outside the repo: not code-reviewed, not versioned with the change; edits overwrite what we used to believe |
| Nygard-style ADRs committed with the code | Reviewed in the PR that makes the change; diffable; rejected options preserved | Discipline required; one short document per decision |

## Decision
Lightweight ADRs in `docs/adr`, numbered sequentially, one per decision. Immutable once **Accepted**: a changed mind produces a new ADR superseding the old, so the record shows what we believed and when.

Each carries a non-standard **Revisit when** section — two to four falsifiable triggers with concrete thresholds. That turns an ADR from a justification (unarguable) into a claim (checkable): if a trigger fires, the decision is due for review whether or not anyone feels uneasy.

Two decisions — no Entity Framework Core, no FluentAssertions — are additionally enforced as failing tests in `tests/ArchitectureTests`. An ADR nobody re-reads is weaker than a red build; where a decision is mechanically checkable, check it mechanically and let the ADR explain the failure.

## Consequences
Cost: one short document per significant decision, written while the context is fresh and reviewed in the PR that makes the change. Benefit: the feature team can tell deliberate constraints from accidents, and reverse either on evidence rather than taste.

## Revisit when
- `docs/adr` exceeds 30 files, or a reviewer cannot find the relevant ADR in under a minute — add an index or split by subsystem.
- Any Accepted ADR is edited in place rather than superseded — immutability then needs a CI diff check, not exhortation.
- A third mechanically enforceable decision is accepted without a matching test in `tests/ArchitectureTests`.
- The organisation mandates a central decision registry outside the repository.
