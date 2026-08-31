# ADR-0032: Port contracts are tested with the awkward shapes production actually produces

**Status:** Accepted · **Date:** 2026-08-30

## Context

The batch pipeline's CRITICAL was not a subtle bug. `EncryptedObjectStore.WriteAsync` read
`CanSeek ? Length : 0` from its source stream, production handed it the one stream shape that
answers `CanSeek == false` (`PipeReader.AsStream()`), and every PUT failed client-side. The
storage test suite was thorough — empty inputs, frame-boundary sizes, 200 MB, tamper detection —
and every single test fed the port a seekable `MemoryStream`. The suite proved the cryptography
and never once exercised the stream *shape* the caller actually delivers.

That is a category of gap, not an instance: a port whose tests all use the convenient input
shape systematically walks the branch production does not take.

## Decision

Every port that accepts a stream, a sequence, or a transaction gets at least one test per
**production shape** its convenient-input tests do not cover. The sweep that established the
baseline:

| Port | Production shape | Was tested? | Gap closed by |
|---|---|---|---|
| `IStatementContentWriter` | non-seekable pipe stream | no — all seekable | `ContentWriter_AcceptsNonSeekableStream` (local, stub S3) + `Write_FromNonSeekableStream_RoundTrips` (gated, MinIO) |
| `IStatementContentWriter` | zero-length content | no | `Write_ZeroLengthContent_RoundTrips` (gated) |
| `IStreamingCipher` | non-seekable source and ciphertext | no — all seekable | `Encrypt_FromNonSeekableSource_RoundTrips` (local) |
| `IBulkWriter` | empty row sequence (re-plan pass) | no | `BulkWriter_EmptySequence_WritesNothingAndSucceeds` (gated) |
| `IBulkWriter` | source faults mid-COPY | no | `BulkWriter_SourceFaultsMidStream_PersistsNothing` (gated) |
| `IAuditWriter` | two appends in one transaction | no | `AuditWriter_TwoAppendsInOneTransaction_BothLandInOrder` (gated) |
| `IStatementRunRepository` | completion after reap+reclaim | no | `Completion_IsScopedToTheClaimant` (gated) |
| `IDataKeyBroker` | lease held by a dead process | yes | `ExpiredLease_IsTakenOver_WithAHigherFenceToken` (existing) |
| `IStreamingCipher` | very large objects | yes | existing 200 MB encrypt/decrypt tests |

The shared double for stream shape is `NonSeekableReadStream`
(`tests/UnitTests/Storage/ContentWriterSeamTests.cs`, public deliberately): it does not just
answer `CanSeek == false`, it **throws** from `Length`, `Position` and `Seek`, so code that
"checks" seekability but still reads `Length` on the sly fails loudly instead of reading a lie.

## The rule going forward

When adding a port or a port implementation, ask what shapes the *caller* produces — not what
shapes are easy to construct in a test — and write one test per shape the suite does not already
exercise. A `MemoryStream`, a non-empty list, and a one-append transaction are the convenient
defaults; a pipe stream, an empty sequence, a mid-stream fault, and a second append are what
production eventually delivers.

## Consequences

- The seam between "our fake accepts it" and "the AWS SDK accepts it" is pinned locally, without
  Docker: the stub enforces the SDK's refusal to send a body whose length it cannot resolve.
- Docker-gated entries above follow the standing limitation (docs/LIMITATIONS.md): written and
  compiling here, first executed on a Docker-capable host.

## Revisit when

- **A new port is added** — the table above is the sweep at 2026-08-30; a new port needs its
  own row(s) before it merges, not after its first production surprise.
- **A production incident traces to an input shape** — that shape joins the "convenient
  defaults vs production delivers" list in the rule above, and every existing port gets checked
  against it.
- **The Docker gate closes** (see docs/LIMITATIONS.md) — the gated rows in the table flip from
  "written" to "executed", and any that fail on first execution reopen this decision's
  premise that writing-now/running-later is sufficient.
