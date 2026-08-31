using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Persistence.Retention;
using StatementDelivery.ServiceDefaults.Storage;

namespace StatementDelivery.ServiceDefaults.Retention;

/// <summary>Both layers' answer to "is this held?", resolved once and consumed everywhere.</summary>
/// <param name="HasDbHold">An unreleased <c>legal_hold</c> row covers the subject.</param>
/// <param name="HasStoreHold">The object store reports a legal hold ON for (at least one of) the subject's objects.</param>
/// <param name="DbCaseReference">The database hold's case reference, when one exists.</param>
public sealed record HoldState(bool HasDbHold, bool HasStoreHold, string? DbCaseReference)
{
    /// <summary>The synthetic case reference for a store-side hold with no database record.</summary>
    /// <remarks>
    /// Synthetic and unmistakable, so the drift is legible in the audit trail rather than
    /// hiding behind a real-looking case number.
    /// </remarks>
    public const string DriftCaseReference = "OBJECT-STORE-HOLD-NO-DB-RECORD";

    /// <summary>Gets a value indicating whether EITHER layer holds. This is the only bit destructive paths may consult.</summary>
    public bool IsHeld => HasDbHold || HasStoreHold;

    /// <summary>Gets a value indicating whether the layers disagree: physically held with no legal record.</summary>
    /// <remarks>
    /// Hold placement is storage-first (ADR-0037), so this is the DESIGNED crash residue - and
    /// the reason the store layer must gate destruction too. The reverse disagreement (DB hold,
    /// no store hold) is reconciliation check 3's finding, not this record's concern: the DB
    /// hold alone already blocks.
    /// </remarks>
    public bool IsDrift => HasStoreHold && !HasDbHold;

    /// <summary>Gets the case reference destructive paths report: the real one, or the drift marker.</summary>
    public string? EffectiveCaseReference =>
        DbCaseReference ?? (HasStoreHold ? DriftCaseReference : null);
}

/// <summary>
/// THE one implementation of "does anything hold this?", used by every destructive path.
/// </summary>
/// <remarks>
/// <para>
/// The Prompt 6 audit found the purge feeding the decision engine <c>dbHold || storeHold</c>
/// while erasure fed it the database alone — the dual-layer defence worked for the reversible
/// operation and failed for the irreversible one. Two hand-rolled aggregations is how that
/// happened; this class exists so it cannot happen again, and an architecture test
/// (RetentionSeamTests) pins that <c>PurgePass</c> and <c>ErasureExecutor</c> depend on this
/// type and not on <c>LegalHoldRepository</c>.
/// </para>
/// <para>
/// The placement/release/list surface stays on <c>LegalHoldRepository</c> — writing holds is
/// the API's job; ANSWERING whether one blocks destruction is this class's.
/// </para>
/// </remarks>
public sealed class HoldResolution
{
    private const int RefsPageSize = 200;

    private readonly LegalHoldRepository _holds;
    private readonly RetentionSweepRepository _statements;
    private readonly IStatementObjectAdmin _objects;

    /// <summary>Initialises a new instance of the <see cref="HoldResolution"/> class.</summary>
    /// <param name="holds">The database layer.</param>
    /// <param name="statements">Storage-ref paging for customer-wide store checks.</param>
    /// <param name="objects">The object store's hold reads.</param>
    public HoldResolution(
        LegalHoldRepository holds,
        RetentionSweepRepository statements,
        IStatementObjectAdmin objects)
    {
        _holds = holds;
        _statements = statements;
        _objects = objects;
    }

    /// <summary>Resolves both layers for ONE statement.</summary>
    /// <param name="statementId">The statement.</param>
    /// <param name="customerId">Its owner, so customer-scoped holds are honoured.</param>
    /// <param name="objectInfo">
    /// The store's answer for the statement's object, when the caller already fetched it (the
    /// purge pass reads retention and hold state in one call); null when the statement has no
    /// object, in which case only the database layer can hold.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The combined state.</returns>
    public async Task<HoldState> ResolveForStatementAsync(
        StatementId statementId,
        CustomerId customerId,
        ObjectRetentionInfo? objectInfo,
        CancellationToken cancellationToken)
    {
        string? dbCase = await _holds.ActiveCaseReferenceForStatementAsync(
            statementId, customerId, cancellationToken).ConfigureAwait(false);

        return new HoldState(
            HasDbHold: dbCase is not null,
            HasStoreHold: objectInfo?.LegalHold == true,
            DbCaseReference: dbCase);
    }

    /// <summary>
    /// Resolves both layers for a WHOLE customer — the erasure gate. Blocked if ANY statement
    /// is blocked, in either layer.
    /// </summary>
    /// <remarks>
    /// The store side pages the customer's storage refs and asks the store per object, stopping
    /// at the first hold. That is one S3 read per statement in the worst case — real money on a
    /// hot path, and fine here: erasure is rare, bounded per customer, and the alternative is
    /// destroying the readability of physically-held evidence because the crash residue of a
    /// storage-first placement (ADR-0037) was invisible to the database.
    /// </remarks>
    /// <param name="customerId">The customer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The combined state.</returns>
    public async Task<HoldState> ResolveForCustomerAsync(
        CustomerId customerId, CancellationToken cancellationToken)
    {
        // Since V021 every hold row carries customer_id, so this single indexed predicate sees
        // BOTH scopes - the query the audit's CRITICAL lived in, now fed by correct data.
        string? dbCase = await _holds.ActiveCaseReferenceForCustomerAsync(customerId, cancellationToken)
            .ConfigureAwait(false);

        bool storeHold = false;
        var afterId = Guid.Empty;
        DateOnly afterPeriod = DateOnly.MinValue;
        while (!storeHold)
        {
            IReadOnlyList<StatementStorageRef> page = await _statements.ListStorageRefsForCustomerAsync(
                customerId, afterId, afterPeriod, RefsPageSize, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            foreach (StatementStorageRef reference in page)
            {
                ObjectRetentionInfo info = await _objects.GetRetentionAsync(reference.StorageKey, cancellationToken)
                    .ConfigureAwait(false);
                if (info.LegalHold)
                {
                    storeHold = true;
                    break;
                }
            }

            afterId = page[^1].Id;
            afterPeriod = page[^1].PeriodStart;
        }

        return new HoldState(dbCase is not null, storeHold, dbCase);
    }
}

/// <summary>Registers the shared hold resolver.</summary>
public static class HoldResolutionExtensions
{
    /// <summary>Adds <see cref="HoldResolution"/>. Requires persistence and the object-admin store.</summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddHoldResolution(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<HoldResolution>();
        return builder;
    }
}
