# ADR-0031: Spool ciphertext to local disk so every PUT declares a Content-Length

**Status:** Accepted · **Date:** 2026-08-30

## Context

The render pipeline streams PDF bytes through a `Pipe` into `EncryptedObjectStore.WriteAsync`,
which encrypts and uploads in one pass. `PipeReader.AsStream()` is non-seekable, and the AWS SDK
refuses a `PutObjectRequest` whose body length it cannot determine — thrown client-side, before
any network I/O:

```
Amazon.S3.AmazonS3Exception: Could not determine content length
```

Every statement produced by the batch pipeline hit this. The ciphertext length is unknowable up
front (framed AEAD adds per-frame overhead to an unknown plaintext length), so the length must
come from somewhere: buffer in memory, spool to disk, upload in parts, or render twice.

One constraint dominates the design: **plaintext statement PDFs must never touch disk.** The
system has no plaintext at rest anywhere — introducing it in a temp directory, readable by
anything that compromises the container filesystem or snapshots the node, would be a new PII
exposure class created by a plumbing fix.

## Decision

`EncryptedObjectStore.WriteAsync` always spools the **ciphertext** to a local temp file, then
PUTs from the file with an explicit, exact `Content-Length`:

1. Encrypt the (non-seekable) source through `FramedEncryptingStream` into a spool
   `FileStream` — plaintext bytes exist only in flight, never on disk.
2. PUT from the spool with `Headers.ContentLength = ciphertextLength` and
   `AutoCloseStream = false`; disposal is ours.

The spool file is created `FileMode.CreateNew` with `FileOptions.DeleteOnClose`, and on Linux it
is additionally **unlinked immediately after open** — a SIGKILL mid-upload leaves no file for
`DeleteOnClose` to miss; the data dies with the file descriptor. The spool directory
(`ObjectStorage:Spool:Directory`, default = the OS temp path) gets a readiness probe
(`spool-directory`: create, write, delete) that fails readiness — a writer that cannot spool
cannot store, and should say so before traffic arrives, not one request at a time.

The spool must live on **real disk, not tmpfs**: tmpfs is RAM, and eight parallel render slots
spooling large statements would compete with the heap the streaming design exists to protect.
Peak disk is bounded and small: `RenderParallelism (8) × largest ciphertext` — roughly 8 MB per
replica for typical statements (~1 MB), roughly 80 MB for pathological 10 MB ones.

## Options rejected

- **Spool the plaintext PDF, measure, then encrypt while uploading** — mechanically simpler (no
  re-read of ciphertext), but it puts customer PII on the container filesystem. Rejected
  outright; the threat model, not convenience, owns this call.
- **Multipart upload (no spool)** — the S3 API's native answer to unknown lengths, and the right
  one for large objects. Rejected *for now*: the minimum part size is 5 MB, so typical ~1 MB
  statements degenerate into single-part multiparts with three round trips (initiate, upload,
  complete) plus abort-cleanup lifecycle rules for crashed uploads. Revisit when any statement
  class approaches **50 MB**, where spool disk and single-PUT limits both start to matter.
- **Render twice — once to count bytes, once to upload** — no disk and an exact length, but
  rendering costs roughly 100× what encrypting does in this pipeline; doubling the expensive
  stage to avoid a temp file inverts the economics of the whole streaming design.
- **Buffer ciphertext in memory** — reintroduces the per-item heap the pipeline was built to
  avoid; 8 slots × large statements is exactly the OOM the scale review warned about.

## Consequences

- Every PUT now declares an exact length. The storage fake that accepted length-less bodies was
  the seam the CRITICAL hid behind; the seam test now pins `Content-Length > 0` at PUT time
  against a stub that enforces the SDK's real refusal.
- Statement bytes at rest on the node are ciphertext under the customer DEK — losing the node
  loses nothing the object store would not also have lost.
- One extra sequential disk write+read per statement (single-digit milliseconds for 1 MB on
  instance SSD) against a multi-second render: noise.

## Revisit when

- **Any statement class approaches 50 MB.** Above that, multipart upload wins: parts stream as
  they are produced, spool disk stops scaling with statement size, and the 5 MB minimum part
  size stops being overhead. The spool path stays for small objects; large ones switch.
- **The spool readiness probe fires in production.** That means the node's disk story changed
  (read-only root, exhausted volume) and the "real local disk" assumption needs re-verifying.
- **`RenderParallelism` grows past ~32 or statements past ~10 MB routinely** — recompute peak
  spool disk (`parallelism × largest ciphertext`) against the node's ephemeral storage before
  raising either.
