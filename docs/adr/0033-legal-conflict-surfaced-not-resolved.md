# ADR-0033: Legal conflicts are surfaced with their basis, never resolved in code

> Numbering note: the Prompt 6 brief calls this ADR-0029; 0029–0032 were already taken by
> earlier decisions, so the sequence continues here.

**Status:** Accepted · **Date:** 2026-08-31

## Context

The obligations over a statement point in opposite directions, and that is the normal condition
of regulated data, not a defect in the law:

```
POPIA s24          data subject MAY request erasure
POPIA s14          MUST NOT retain longer than necessary
FICA s23           MUST retain ≥ 5 years from end of relationship
Companies Act      MUST retain 7 years
Litigation hold    MUST retain indefinitely
Object Lock        CANNOT delete before retain-until, regardless of any decision
```

A system that quietly picks a side — purging despite a hold, refusing an erasure without saying
why — turns a legal judgement into a code path nobody reviews.

## Decision

One pure function, `RetentionDecisionEngine.Decide` (BuildingBlocks/Domain/Retention), encodes
the precedence order, and every consumer — the purge worker, the erasure API, the erasure
executor — calls it rather than re-deriving fragments of it:

```
1. CustomerKeyDestroyed          → AlreadyErased
2. HasActiveLegalHold            → BlockedByLegalHold(caseRef)
3. ObjectLockRetainUntil > Today → BlockedByObjectLock(until)
4. RetainUntil > Today           → RetainStatutory("Companies Act s24 / FICA s23", until)
5. otherwise                     → Purge
```

Legal hold outranks everything because litigation preservation is absolute. Object Lock comes
next because it is a physical impossibility rather than a policy. Statutory retention is a
policy a hold can extend and nothing can shorten.

**Every blocked outcome carries its basis and its date**, and they flow into the API response
and the audit record verbatim. Compare `409 Conflict` with
`409 Conflict — cannot erase: FICA s23 requires retention until 2031-03-14`: the second is a
defensible regulatory response; the first is a bug report. Refusals are audited
(`ERASURE_BLOCKED`, `RETENTION_SKIPPED_*`), because a decision not to act is still a decision.

The engine is verified by a 32-row table-driven test over every combination of its five inputs
(`RetentionDecisionEngineTests`) — a pure function encoding a legal precedence order,
exhaustively checked.

## Consequences

- Changing the precedence is a one-line diff with a red 32-row table, reviewed as a legal
  change rather than sneaking in as worker refactoring.
- The API, the purge worker and the erasure executor cannot disagree about the law: they share
  the function.
- A human decides contested cases. The system's job ends at making the conflict visible and
  citing the statute.

## Revisit when

- **A new obligation class arrives** (e.g. a tax-authority preservation order): it gets a
  precedence position, a context field, and 2× more table rows — never an ad-hoc check in one
  consumer.
- **A regulator or counsel disputes the order**: the table test is the artefact to review with
  them; the code follows it.
