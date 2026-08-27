# ADR-0021: Client-side envelope encryption, not SSE-KMS
**Status:** Accepted   **Date:** 2026-08-27

## Context
S3 will encrypt objects for us. `SSE-KMS` needs one header on a `PutObject` call, costs nothing to implement, is transparent on read, and is what most systems should use.

We are not using it. That deserves a better reason than "we wanted to write a cipher", and the reason is crypto-erasure.

## Options considered
| Option | Implementation cost | Erasure granularity | Who can read the plaintext |
|---|---|---|---|
| `SSE-S3` (AWS-managed keys) | One header | None | AWS, and anyone with `s3:GetObject` |
| `SSE-KMS`, one key for the bucket | One header | All-or-nothing | Anyone with `s3:GetObject` **and** `kms:Decrypt` on the bucket key |
| `SSE-KMS`, one key per customer | One header, 26M keys | Per customer | As above, per key |
| `SSE-C` (customer-provided key per request) | Key on every request | Per key | Us — but the key travels on every request, over the wire, to a service that logs request metadata |
| **Client-side envelope encryption (chosen)** | A framed AEAD and a key hierarchy | Per customer, per object | Only a process holding both the wrapped DEK and the cohort KEK |

## Decision
Encrypt client-side, before the bytes reach object storage. S3 stores ciphertext it cannot read.

**Crypto-erasure is the deciding factor, and it decides on a detail that is easy to miss.**

Under `SSE-KMS`, the mapping from object to key is *S3's*. To erase a customer you must either give every customer their own KMS key — eliminated at $26 million a month, see ADR-0020 — or accept that erasure means deleting objects. And deleting objects is exactly what this system cannot do: they sit under a seven-year Object Lock in COMPLIANCE mode, which no principal can bypass. Under `SSE-KMS` with a shared key, a right-to-erasure request has **no mechanism at all**: the bytes cannot be deleted and the key cannot be destroyed without erasing everyone.

Under client-side envelope encryption the mapping is *ours*. Each object's DEK is wrapped by a per-customer CEK, and the CEK is one row we control. Erasure is a single `UPDATE` that clears `wrapped_cek`. The objects remain, still locked, still auditable as having existed — and permanently unreadable. That is the only construction that satisfies both "retain for seven years, immutably" and "erase this person on request", which are the two requirements that would otherwise contradict each other.

Three secondary reasons, none of which would justify the decision alone:

**The ciphertext is bound to its identity.** The frame AAD authenticates `statementId`, `customerId` and `version`, supplied on read from the statement row. An attacker with database write access who repoints one customer's `storage_key` at another's object gets a successful fetch and a failed decryption. `SSE-KMS` decrypts whatever is at the key it is given — S3 has no notion of which statement an object is supposed to be, so it cannot detect the substitution.

**The trust boundary moves.** With `SSE-KMS` the plaintext exists inside S3's process, and anyone holding `s3:GetObject` plus `kms:Decrypt` sees it. With client-side encryption the plaintext exists only inside our processes; an S3 credential leak yields ciphertext.

**Key handling is uniform.** One envelope format, one wrapping algorithm, one place where key material is minted and wiped — rather than a KMS-shaped path for objects and a different path for anything else that ever needs encrypting.

## What this costs
- **We own a cryptographic construction.** ADR-0019 is the mitigation: no cipher is implemented, only a framing protocol around `AesGcm`, following the shape of Tink and the AWS Encryption SDK. It is nonetheless more of our code in the trusted path than a header would have been.
- **The read path needs a key before it needs bytes.** An unwrap precedes every `GetObject`, adding a KMS round trip that `SSE-KMS` would have absorbed transparently. Mitigated by the DEK cache, which exists for the write side's rate quota but also shortens this path.
- **A decryption failure is now ours to handle.** Under `SSE-KMS` a corrupt object is an S3 error. Here it is a `CiphertextIntegrityException` that must become a uniform 404, an audit record, and an alert — see the gateway's handling and `statement_decryption_failure_total`.
- **No server-side integrations.** S3 Select, Athena and anything else that reads object contents see noise. Acceptable: nothing in this system queries statement bytes, and a bank statement is not analytics data.

## The port survived; the value object grew
Prompt 3 defined `IStatementContentStore` with deliberately nothing crypto-shaped in its signature — no key identifier, no IV, no auth tag, no decryption callback. The gateway asks for bytes at a location and receives a stream. The stated test of that design was whether adding encryption would force a change to `Download.Gateway`.

It did not, and being precise about what that means matters more than the headline:

- **The port is unchanged.** `IStatementContentStore.OpenReadAsync(StorageLocation, CancellationToken)` is byte-for-byte the signature Prompt 3 shipped.
- **The value object grew.** `StorageLocation` gained `Envelope`, and `CryptoEnvelope` carries the wrapped DEK, the KEK id, the algorithm, the plaintext digest and the `ContentBinding` the AAD is checked against. The gateway reads four more columns from a row and hands them to the store. That is field plumbing.
- **Request-handling logic did not change.** No branch, no condition and no ordering in the redemption path moved. The diff over `src/Services/Download.Gateway/` is a DI registration, a `COPY` line in the Dockerfile, a metric, and error handling.
- **Error handling did change, and it had to.** A decryption failure has no equivalent in a filesystem adapter, so there was no clause to catch one; untranslated it became a 500 with a traceId, which is a visibly different response from the uniform 404 and therefore an oracle. There are now two catches — one at open time and one mid-stream — plus `PrimeAsync`, which authenticates the header before any response header is written so the failure lands where a denial is still expressible.

So **"zero changes" would be a false claim, and "no logic changes" is a true one.** The second is the property the port was defined to buy, and it is the one worth stating. A design note that overclaimed here would be worse than one that claimed nothing, because the next person would trust the port to absorb a change it cannot.

## Consequences
- `statement.wrapped_dek`, `dek_algorithm`, `kek_id` and `content_sha256` are populated on every write. The `iv` and `auth_tag` columns from V006 stay NULL permanently — the framed format has one nonce per frame, derived structurally, and one tag per frame. There is no single IV to record. They are left rather than dropped because dropping a column from a 2.5-billion-row table is a rewrite, and NULL costs nothing.
- Bucket-level `SSE-S3` is not disabled. It is free, it is orthogonal, and defence in depth means not objecting to a second layer that costs nothing.
- `content_sha256` is computed during encryption in the same pass and verified during decryption. It catches what per-frame authentication cannot: an object silently replaced by an *older, authentic* version of itself.
