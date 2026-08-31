# Setup notes for Prompt 6

Scoped-ahead work identified before Prompt 6 begins, so it lands as the first task rather than
a mid-prompt surprise. Added during the Prompt 5 RED remediation (2026-08-30) per the audit's
Part E: the methods below are deliberately NOT implemented yet — code with no caller is how
dead methods come to exist.

## Part 0.4 — The storage adapter needs a delete and lock-read surface

The Prompt 5 audit found the writer-side port exposes neither. Purge (Part C)
and the orphan sweep (Part F) both start here. Add to the port and adapter,
before anything else:

    Task DeleteObjectVersionsAsync(string key, NpgsqlTransaction? tx, CancellationToken ct);
    Task<ObjectRetentionInfo> GetRetentionAsync(string key, CancellationToken ct);

`GetRetentionAsync` returns mode, retain-until and legal-hold status. The purge
worker treats it as authoritative over the database — see Prompt 6 constraint 4.
Delete must be idempotent: a missing object is success, not an error.
