# Cost model

Three of these numbers changed the architecture; that is the reason this document exists. Unit
prices are AWS public list prices (us-east-1, checked 2026-08) — swap in negotiated rates and
the *conclusions* survive, because each finding is an order-of-magnitude argument, not a
penny-accurate one.

## Total cost of ownership

Bottom-up at steady state (year 7+, retention tail full):

| Component | Assumption | Quantity | Unit price | Monthly |
| --- | --- | --- | ---: | ---: |
| Hot storage (S3 Standard, 90-day window) | 90M objects × 200 KB | 18 TB | $0.023/GB | $414 |
| Cold storage (Glacier Instant Retrieval) | 2.43B objects × 200 KB | 486 TB | $0.004/GB | $1,944 |
| PUTs (generation) | 30M/month | 30M | $0.005/1k | $150 |
| Lifecycle transitions to GIR | 30M/month | 30M | $0.02/1k | $600 |
| GETs + retrievals | ≤1.2 downloads/statement, mostly hot | ~36M | $0.0004–$0.01/1k | ~$60 |
| KMS cohort keys | 1,024 keys (ADR-0020) | 1,024 | $1/key | $1,024 |
| KMS API calls | CEK mint/unwrap only (DEKs wrap locally) | ~5M | $0.03/10k | $15 |
| PostgreSQL (primary + replica, RDS-class) | catalogue + tokens + audit | 2 × large | — | ~$2,600 |
| Compute — steady services | 2× api, 2× gateway, workers idle-ish | ~8 vCPU | — | ~$700 |
| Compute — generation burst | scale-to-~400 for one 6 h window | ~2,400 vCPU-h | $0.04/vCPU-h | ~$96 |
| Redis, PgBouncer hosts, dashboard | small | — | — | ~$250 |
| Egress (Finding 2) | ~253 GB/month | 253 GB | $0.09/GB | **$22** |
| Contingency ≈ 6% | | | | ~$425 |
| **TOTAL** | | | | **≈ $8,300 (~R150,000)** |

## Cost per client per year

$8,300 × 12 ÷ 26,000,000 customers ≈ **$0.0038 (~R0.07) per client per year.**

The conclusion the numbers force: **at seven cents per client per year, every design decision
should optimise for correctness and compliance, not cost.** A 50% infrastructure saving is
3.5 cents per customer per year; one regulatory finding is not. That conclusion is only
available to someone who did the arithmetic — which is why the three findings below each begin
with a multiplication.

## Finding 1 — per-customer KMS keys cost $26M/month

26,000,000 customers × $1/key/month = **$26,000,000/month** — roughly 3,000× the entire
platform. This killed the *clean* crypto-erasure design (one KMS key per customer, destroy the
key to erase) outright, and forced the three-tier hierarchy of ADR-0020: 1,024 cohort KEKs in
KMS ($1,024/month) wrapping per-customer CEKs stored, wrapped, in our own database at zero
marginal cost. Erasure stays per-customer — it deletes one row — and the HSM protects the tier
that is affordable to put in one. The accepted residue (CEKs guarded by database controls
rather than an HSM) is named in LIMITATIONS.md.

## Finding 2 — egress is $22/month

Peak delivery bandwidth is ~6 MB/s (30 req/s × 200 KB); monthly volume ~253 GB ≈ **$22**. The
standard argument for presigned URLs is bandwidth offload — at this scale that argument is
worth twenty-two dollars. Which freed the proxy architecture: the gateway decrypts and
authenticates frame by frame, enforces consume-before-stream and uniform denials — none of
which a presigned URL can do — and the thing sacrificed to get all that was a rounding error
(ADR-0015/0017/0019).

## Finding 3 — Intelligent-Tiering would cost $6,300/month for nothing

2.52B objects × $0.0025 per 1,000 objects monitoring = **$6,300/month** — more than the entire
tiered storage bill it would be optimising. And the access pattern is *knowable in advance from
the domain*: statements are hot for ~90 days and cold forever after. Intelligent-Tiering exists
to discover unpredictable access patterns; paying it to discover what the domain already tells
you is paying for nothing. A dumb lifecycle rule (Standard → Glacier IR at 90 days) captures
the whole saving at $600/month of transition fees.

## Storage tiering

| Tier | Window | Volume at steady state | $/GB | Monthly | Transition in |
| --- | --- | ---: | ---: | ---: | ---: |
| S3 Standard | 0–90 days | 18 TB | 0.023 | $414 | — |
| Glacier Instant Retrieval | 90 days–7 years | 486 TB | 0.004 | $1,944 | $600 |
| (deleted by the purge worker) | > retain_until | — | — | — | — |

The purge worker is a cost control as much as a compliance one: **lock expiry is not deletion**
(ADR-0022) — without the purge job, the 486 TB never stops growing and the bill never stops.

## Retention tail

| Year | Objects held | Cold volume | Storage cost/month |
| ---: | ---: | ---: | ---: |
| 1 | 360M | ~70 TB | ~$290 |
| 3 | 1.08B | ~212 TB | ~$860 |
| 5 | 1.80B | ~353 TB | ~$1,430 |
| 7+ (steady) | 2.52B | ~486 TB | ~$1,944 |

Steady-state cost is dominated by data written years ago, not this month's output — the reason
tiering and the purge worker matter more than any compute optimisation.

## What I would optimise first

Nothing, until measurement says otherwise — the arithmetic above says the platform's cost story
is already decided by three structural choices (cohort keys, dumb lifecycle, proxy delivery).
The first *measured* candidate: the PostgreSQL instances are half the bill; the audit trail's
seven-year growth is what sizes them, and partition-detach-to-cheap-storage for audit months
older than N is the lever — designed for (monthly partitions exist), unbuilt, listed in
LIMITATIONS.md.
