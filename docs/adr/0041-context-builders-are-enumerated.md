# ADR-0041: Adapters that feed exhaustively-tested logic are themselves exhaustively tested

**Status:** Accepted · **Date:** 2026-08-31

## Context

Three rounds, three seam defects, and every time both sides were correct against their own
comments while the join was wrong — and the test encoded the same misunderstanding as the code,
so it passed:

| Round | Correct component | Defective seam |
|---|---|---|
| Prompts 1–4 | Atomic consume, hash chain primitives | The transaction they ran in |
| Prompt 5 | Render pipeline, storage adapter | The stream contract between them |
| Prompt 6 | Decision engine, 32-row table | The context construction feeding it |

The Prompt 6 instance is the sharpest: the engine had a 32-row exhaustive table, so a wrong
branch could not hide. The three call sites that *built its input* each mapped the world onto
the engine's parameters by hand, slightly differently, with one example test apiece — and the
unexercised scope was the production one.

## Decision

> An exhaustive table over a pure function proves the function. It proves nothing about the
> code that constructs the function's input. When a decision is important enough to warrant a
> decision table, the context builder feeding it warrants one too — otherwise the enumeration
> stops exactly where the real-world variation starts.

Concretely:

1. **One builder.** Facts-to-input mapping lives in a single pure component
   (`RetentionContextFactory`; hold aggregation in `HoldResolution`), never inline at call
   sites. Architecture tests pin the callers (`RetentionSeamTests`).
2. **An enumerated matrix over the builder** (`BuildRetentionContext_ProducesCorrectFields`,
   96 rows: hold scope × store hold × key state × retention × lock), asserting on the produced
   *fields*, not the downstream decision.
3. **Production constructors in tests** (the ADR-0032 corollary, now enforced): every key,
   path, stream or identifier a test constructs comes from the production constructor
   (`StorageKeyScheme.KeyFor`) — hand-built values only where the test's purpose is to reject
   them (path-traversal probes, leak needles).

## Consequences

- The CRITICAL is now one red cell in a matrix instead of an argument in an audit.
- Constants consumed by more than one component are derived, not duplicated
  (`StorageKeyScheme.ShardCount`/`ShardWidth` feeding the orphan sweep, pinned by
  `OrphanSweep_PrefixSet_MatchesStorageKeyScheme`).
- The standing sweep (this round's D3, repeated at each audit): for every exhaustively-tested
  pure function, name what builds its input and whether that builder is enumerated.

## Revisit when

- **A new decision table lands** — its builder matrix lands in the same change, or the gap is
  recorded here with a date.
- **The matrix's axes grow past usefulness** (thousands of rows): collapse axes that provably
  do not interact, and document the proof beside the table.
