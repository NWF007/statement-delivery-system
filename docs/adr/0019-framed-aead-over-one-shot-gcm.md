# ADR-0019: A framed AEAD over one-shot GCM, because .NET refuses to stream it
**Status:** Accepted   **Date:** 2026-08-27

## Context
Three requirements collide, and any two of them are easy.

1. **The download path must stream in O(1) memory.** ADR-0016 and the 200 MB test exist because a statement buffered in memory scales the working set with *statement size × concurrency*, and a 200 MB allocation lands on the large object heap, which is collected rarely and compacted almost never.
2. **Content at rest must be authenticated, not merely encrypted.** Encryption without authentication leaves the ciphertext malleable: an attacker with write access to the bucket can flip bits in a CTR-mode stream and change the plaintext in ways nothing detects.
3. **The only AEAD primitive .NET offers is one-shot.**

The third is a deliberate refusal by the platform, not a gap. `AesGcm` and `AesCcm` do not derive from `SymmetricAlgorithm`, expose no block functionality, and offer only `Encrypt`/`Decrypt` over spans. The .NET cryptography design rationale is explicit that this was chosen to prevent misuse — principally **the risk of releasing unauthenticated data**.

That risk is concrete. GCM is CTR plus GMAC, and the tag covers the entire message. A naive streaming decryptor emits plaintext as it decrypts and checks the tag at the end — by which time it has already handed the caller bytes it could not vouch for. Since streaming decryption is hard to do safely, .NET does not offer it at all.

So something has to give. What gives is the assumption that one message equals one AEAD operation.

## Options considered
| Option | Pros | Cons |
|---|---|---|
| Buffer the whole object, one `AesGcm` call | Trivially correct; the tag covers everything before anything is released | Forfeits O(1) memory outright. 200 MB on the LOH per concurrent download. This is the requirement the design exists to protect |
| `CryptoStream` over AES-CBC + separate HMAC | Streams natively; encrypt-then-MAC is a sound construction | The MAC still covers the whole message, so it is verified last — the same release-before-verify problem, now hand-rolled. Two keys, two primitives, and a construction we would be responsible for |
| Naive streaming GCM (release, then check the tag) | Simple, streams | Releases unauthenticated plaintext. This is precisely what the platform refuses to make easy, and refusing it is correct |
| Spill to a temp file, verify, then serve | O(1) memory in the process | Moves the problem to disk: every download needs scratch space, the plaintext exists unencrypted on a local volume, and cleanup becomes a correctness concern |
| **Framed AEAD: fixed-size frames, one `AesGcm` call each (chosen)** | Streams in O(1); each frame is authenticated **before** its bytes are released; per-frame random access comes free | We own a framing protocol. Truncation is only detected at end of stream. More moving parts than one call |

## Decision
Split the plaintext into fixed-size frames (64 KiB by default) and encrypt each frame independently with `AesGcm`. Each frame carries its own tag and is authenticated before a byte of it reaches the caller.

**This is not a novel construction.** It is the shape used by Google Tink's `AesGcmHkdfStreaming` and by the AWS Encryption SDK's framed message format, whose body AAD carries the message id, a content-type value that *differs for regular versus final frames*, the frame sequence number, and the content length. That design is copied deliberately — including the final-frame distinction, which is the part that matters most and the part easiest to leave out.

The wire format is specified in `FrameFormat`:

```
HEADER (plaintext on the wire, authenticated by its own tag)         48 bytes
  magic "SDP1" | version 0x01 | frameSize u32 BE | messageId(16) | noncePrefix(7) | tag(16)

BODY, per frame i:
  nonce = noncePrefix(7) || frameIndex(4 BE) || isFinal(1)
  aad   = messageId(16) || frameIndex(4 BE) || isFinal(1) || plaintextLength(4 BE)
        || statementId(16) || customerId(16) || statementVersion(4 BE)
  wire  = [plaintextLength: 4 BE][ciphertext][tag: 16]
```

Three properties follow, and each is why a piece of the layout is shaped the way it is.

**Nonce uniqueness is structural, not probabilistic.** IV reuse under one key breaks GCM catastrophically — not just confidentiality but authenticity, since the authentication subkey can be recovered from two messages sharing a nonce. The 7-byte prefix is fresh CSPRNG output per object and the 4-byte index is unique within an object, so two frames under one key cannot collide by construction. **The prefix is never derived from a counter.** A counter that resets — a restored snapshot, a redeployed pod, a rewound sequence — reissues a prefix that has already been used, and every guarantee here evaporates silently. This is also what makes the DEK cache safe: reusing one key across a thousand objects is fine precisely because the prefix is per-*object*, not per-key.

**The AAD binds the ciphertext to its identity.** `statementId`, `customerId` and `version` are authenticated, and on read they are supplied *again, independently, from the statement row*. An attacker with database write access can repoint customer A's `storage_key` at customer B's object; the fetch succeeds and the decryption fails. That is a cryptographic defence against a database-level attack, which is unusual and worth stating plainly. It only works because the reader takes the identity from the row rather than from the object — an identity read out of the ciphertext would be compared against itself and would catch nothing.

**The header is authenticated.** Encrypting an empty plaintext with `header[0..32]` as AAD produces a tag over the frame size, the message id and the nonce prefix. Tampering with the frame size to induce misparsing — the classic attack on a length-prefixed format — fails at the first operation, before a single body byte is parsed. The header nonce is `noncePrefix || 0xFFFFFFFF || 0xFF`, separated from every body nonce by its last byte, which body frames can only ever set to 0x00 or 0x01.

### Truncation resistance
Without a final-frame marker, an attacker can **drop trailing frames** and every remaining frame still authenticates perfectly. The recipient gets a valid-looking, silently truncated statement — for a financial document, an integrity failure that looks exactly like success.

`isFinal` therefore appears in **both the nonce and the AAD**, so a regular frame can never be reinterpreted as a final one. Finality is not a field on the wire: the decoder reads a frame, then attempts to read the next frame's length prefix, and end-of-stream is what makes the frame just read the final one. Two rules follow, and both are enforced in `FramedDecryptingStream`:

1. If the stream ends without a frame that authenticates as final — **throw**. Do not return what was decrypted so far.
2. If a frame that authenticates as final is followed by more bytes — **throw**.

Both attacks reduce to the same failure. Dropping the final frame promotes its predecessor to last-on-the-wire, so the decoder tries it as final and the tag rejects it. Appending bytes demotes the real final frame, so the decoder tries it as regular and the tag rejects it. Neither rule needs a length field to be trusted, which matters because a length field is exactly what an attacker would edit.

The encoder rule that makes this work is easy to miss: **if the plaintext length is an exact multiple of the frame size, emit a final EMPTY frame.** Otherwise a message whose length divides evenly ends on `isFinal = 0` and rule 1 rejects a perfectly legitimate object. Zero-length input is the same case, since 0 is a multiple of everything. `RoundTrip_LengthIsExactMultipleOfFrameSize` exists for this and nothing else.

## Consequences

### What this costs
20 bytes per frame (4 length prefix, 16 tag) plus a 48-byte header — 0.03% at the default frame size, or about 64 KB on a 200 MB statement. Negligible.

### The honest limitation
Per-frame authentication means plaintext is released one frame at a time, each verified before release. That is far stronger than naive streaming GCM, which releases everything before checking anything. **But truncation is still only detected when the stream ends** — a client reading progressively will have received authentic-but-incomplete data before the decoder throws.

Mitigations: `Content-Length` is set from the plaintext length recorded in the database, so an HTTP client sees a short read and treats the response as failed; and the decoder throws rather than returning partial output. Full protection would require buffering the entire object before releasing any of it, which forfeits the O(1) memory this format exists to preserve. That trade is accepted knowingly, not overlooked.

A second, smaller limitation: the header is read and range-checked *before* its tag is verified, because the frame size it declares is needed to size a buffer. The bound (`MinFrameSize`..`MaxFrameSize`) is what keeps a hostile header from dictating an arbitrary allocation in the window before the tag check.

### A property gained, not a feature enabled
The framed format permits **random access**: any frame can be decrypted without the preceding ones, since its nonce and AAD depend only on its index. That means HTTP `Range` support becomes technically possible if ADR-0016 is ever revisited.

Range remains unsupported. The reason it is unsupported is the single-use token — a resumed transfer is a second request against a credential that is already spent — and that reason is untouched by this change. What has changed is that the *crypto* is no longer also a blocker. Noted here so a future reader does not rediscover it as an obstacle that no longer exists.

### Enforcement
`tests/UnitTests/Crypto/FramedCipherTests.cs` covers the format as a format: round trips at every boundary including the exact-multiple case, and a negative test for every failure mode the format defines. `Truncate_DropFinalFrame_IsDetected` is the one that proves the construction works — if it passes the format is sound, and if it were missing the format could be broken in a way every other test still passes.
