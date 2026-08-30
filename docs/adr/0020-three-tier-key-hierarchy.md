# ADR-0020: A three-tier key hierarchy, because per-customer KMS keys cost $26 million a month
**Status:** Accepted   **Date:** 2026-08-27

## Context
Crypto-erasure is the mechanism that makes deletion feasible at this scale. Objects sit under a seven-year Object Lock and *cannot* be deleted; a right-to-erasure request cannot be served by removing bytes. It is served by destroying the key, after which the bytes are noise.

For that to work, key destruction has to be **per customer**. Destroying a key shared by thousands of people erases thousands of people.

The clean design is therefore one key management service key per customer: destroy the key, the customer's statements are gone, done. It is simple, it is obviously correct, and it is the design anyone would reach for first.

## The arithmetic that eliminates it
AWS KMS charges roughly **$1 per customer-managed key per month**. At 26 million customers:

> 26,000,000 × $1 = **$26,000,000 per month**, or $312 million a year.

That is not a design to be weighed against alternatives on grounds of elegance. It is eliminated outright, by a factor of about three thousand against any plausible budget for the whole platform.

The obvious retreat is cohort keys — a fixed number of KMS keys, customers assigned to one each. 1,024 keys × $1 = **~$1,024 per month**, entirely affordable. And useless for the purpose: with 26 million customers over 1,024 cohorts, destroying one key erases **roughly 25,000 customers at once**. Erasure has to be per-customer or it is not erasure.

So the affordable option cannot erase and the option that can erase is unaffordable.

## Options considered
| Option | Monthly key cost | Erasure granularity | Verdict |
|---|---|---|---|
| One KMS key per customer | ~$26,000,000 | Per customer | Eliminated by cost |
| One KMS key per cohort, used directly | ~$1,024 | ~25,000 customers | Eliminated by granularity |
| One KMS key for everything | ~$1 | None | Eliminated by both |
| Client-side keys held by the customer | $0 | Per customer | The bank cannot produce a statement on demand for a regulator, and a customer who loses their key destroys their own records. Not viable for a regulated archive |
| **Cohort KEK → per-customer CEK → per-object DEK (chosen)** | ~$1,024 | Per customer | Affordable and erasable |

## Decision
Three tiers:

```
KEK   1,024 cohort keys, held in KMS, HSM-backed
        │ wraps
CEK   one per customer (26M), stored WRAPPED in customer_key
        │ wraps
DEK   one per object (or per cached batch), stored WRAPPED on statement
        │ encrypts
      statement bytes
```

Cohort KEKs live in KMS where an HSM protects them and the bill is four figures. Per-customer CEKs live **wrapped, in our own database**, at zero marginal cost — one row each. Crypto-erasure deletes the `wrapped_cek` from one row.

Cohort assignment is `SHA-256(customerId)[0..4] mod 1024`, stable for all time. It is hashed rather than taken from the UUID directly because UUIDv7 leads with a millisecond timestamp: using those bytes would distribute customers by sign-up time, which is not a distribution at all.

## The trade-off, stated plainly
**A CEK is protected by this database's access controls rather than by an HSM.** That is a weaker guarantee than KMS-resident key material, and it is accepted because the alternative is economically impossible — not because the two are equivalent. Anyone reading this should be able to see exactly what was given up.

Compensating controls, in descending order of importance:

1. **The wrapped CEK is useless without the cohort KEK in KMS.** An attacker who exfiltrates the entire database holds ciphertext and nothing else. They need *both* a database compromise and a KMS compromise, behind different credentials in different systems, and KMS never releases key material at all — it only performs operations.
2. **`wrapped_cek` is readable only by the roles that decrypt.** V013 revokes `app_delivery`'s table-level `SELECT` on `customer_key` and grants column-level access to everything *except* that column. The catalogue and link-issue service — the largest internet-facing attack surface — cannot read key material at all. Note the reason the revoke was needed: V009's table-level grant was correct when written and silently began covering a key column the moment one was added.
3. **Every access is audited**, on the same append-only hash-chained trail as everything else.
4. **KMS operations are logged independently**, in CloudTrail, outside this system's control — so a mass unwrap is visible even to an attacker who owns the application.

Residual risk that is *not* mitigated: an attacker holding both a database dump and live KMS credentials for the relevant cohorts can decrypt those customers' statements. That is the accepted exposure.

## ⚠ The cohort count is a one-way door
`CohortAssignment.CohortCount` is a `const`, not configuration, and `CohortAssignment_CountIsPinned` asserts its exact value.

Changing it after any CEK exists remaps most customers to a different cohort, whose KEK cannot unwrap the CEK written under the old one. Those CEKs are gone. Every statement those customers have is permanently unreadable. **No backup helps**, because the wrapped bytes in the backup are wrapped under a key the new mapping will never select again. It is indistinguishable from crypto-erasing millions of customers by accident, and it would be discovered one support ticket at a time over weeks.

A config value could be changed by anyone with access to a config map. A `const` with a failing test attached requires a developer to open the file, change the line, change the test, and explain both in review. If the count ever genuinely must change, the migration is: introduce a second mapping, re-wrap every CEK under the new cohort while both KEK sets exist, then retire the old one. That is a project, not a constant.

## DEK caching is a throughput control, not a cost control
A data key is cached per process and reused for at most **1,000 objects, 256 MB, or 5 minutes**, whichever comes first.

It would be easy to justify this as a saving: `GenerateDataKey` costs about $0.03 per 10,000 requests, so 30 million statements a month is roughly **$90**. That is immaterial and nobody would build a cache for it.

**The real reason is KMS request-rate quota.** Rendering 30 million statements inside a month-end window means about **1,400 `GenerateDataKey` calls per second** sustained across 400 workers. KMS enforces account-level request-rate limits, and crossing one does not degrade gracefully — it returns throttling errors, which become retries, which raise the offered rate further. Getting the rationale the wrong way round would mean tuning the cache against a number that does not matter.

Blast radius, so the bounds can be argued with rather than assumed: a data key recovered from a compromised worker process decrypts **at most 1,000 statements written in a 5-minute window on that one worker**. Not the corpus, not the customer's history, not the fleet.

Reusing one key across objects is safe here for a specific reason worth stating, because "one key, many messages" is the shape of a GCM nonce-reuse disaster: every object gets a fresh random nonce prefix at encryption time, so the prefix is per-*object*, not per-key. Had it been derived from the key, this cache would be a critical vulnerability rather than an optimisation. See ADR-0019.

## Consequences
- `customer_key` gains `cohort_id`, `wrapped_cek` and `cek_algorithm` (V013). A `CHECK` enforces that an `ACTIVE` row has both material and a cohort, and that wrapped material is at least 40 bytes — a raw 32-byte key cannot satisfy it.
- `app_generation` is the only role that may `INSERT` a customer key. Download reads them; retention destroys them; nobody updates them outside rotation.
- The CEK insert is `ON CONFLICT DO NOTHING` with a `RETURNING` clause, because 400 replicas can reach a keyless customer simultaneously. Losers discard their candidate and re-read. A customer with two CEKs is a customer half of whose statements survive erasure.
- `DestroyCekAsync` throws `NotImplementedException` with a `TODO(prompt6)`. Erasure is irreversible and the code that decides *whether it is lawful yet* — retention expired, no legal hold, audited — does not exist. Building the destructive half first is how a system ends up able to erase data it was required to keep.
- KMS key rotation is out of scope. `kek_id` is stored per object precisely so rotation does not have to rewrite history, and the cohort index (V014) exists so a cohort can be walked when it does.
