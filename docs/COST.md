# Cost model

> **Heading skeleton only — nothing here is priced yet.** Every figure added below must cite its source (published price list, measured usage, or stated assumption) and carry the date it was taken, because cloud prices move and an undated number is not evidence.

## Cost drivers

*What this system's spend scales with. Analysis, not pricing, so it is written now; the money attached to each row comes later.*

| Driver | Scales with |
| --- | --- |
| Object storage volume | Statements retained x average PDF size, growing monthly until the 7-year window fills |
| Storage class transitions | Lifecycle moves of cold statements: per-object transition requests plus the cheaper resting rate |
| PDF render compute | Statements rendered per cycle, concentrated in the month-end burst; peak concurrency, not the mean |
| PostgreSQL primary | Provisioned storage, IOPS ceiling and memory, sized for the burst |
| Download egress | Downloads served x average PDF size |
| KMS / key operations | Encrypt per statement written, decrypt per statement read |
| Telemetry ingestion | Log, trace and metric volume; request count x sampling rate |
| Standby / replica footprint | Replica count; each standby costs close to the primary |

## Unit economics

*The three per-unit numbers this model exists to produce: monthly cost divided by measured volume.*

| Unit | Cost | Source | Date |
| --- | --- | --- | --- |
| Per statement generated | TBD | | |
| Per statement stored per year | TBD | | |
| Per download served | TBD | | |

## Monthly estimate

*Bottom-up build at steady-state volume, one row per driver above. Unit prices come from the dated list cited below.*

| Component | Assumption | Quantity | Unit price | Monthly cost |
| --- | --- | --- | --- | --- |
| | | | | |
| **TOTAL** | | | | **TBD** |

## Retention tail

*Models statements aged 1-7 years accumulating in storage. A 7-year retention system's steady-state cost is dominated by data written years ago, not by this month's output, so this must show the year-by-year ramp to steady state rather than one figure.*

| Year | Statements held | Storage volume | Cost |
| --- | --- | --- | --- |
| 1-7 | TBD | TBD | TBD |

## Levers

*What can be traded against cost. Named here, priced once the estimate is populated.*

- Storage class lifecycle policy: how aggressively cold statements transition
- Render concurrency vs. burst duration: a shorter month-end window costs more per hour
- Compression and PDF size: the multiplier on both storage and egress
- Telemetry sampling rate: observability fidelity against ingestion volume
- Replica count: availability against duplicated provisioned capacity
- Reserved vs. on-demand capacity: commitment against flexibility

## Assumptions and sources

*Every number used above traces back to a row here. No row, no number.*

| Assumption | Value | Source | Date |
| --- | --- | --- | --- |
| | | | |
