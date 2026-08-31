using System.Globalization;
using Npgsql;
using StatementDelivery.Domain.Abstractions;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Persistence.Retention;
using StatementDelivery.Persistence.Uow;
using StatementDelivery.ServiceDefaults.Auditing;
using StatementDelivery.ServiceDefaults.Storage;

namespace Delivery.Api.Retention;

/// <summary>Places, releases and lists legal holds. Staff scope.</summary>
/// <remarks>
/// <para>
/// DUAL-LAYER, AND THE ORDER IS THE POINT (ADR-0037). A hold lives in two places: the database
/// row the policy engine reads and the audit describes, and the object-store legal hold that
/// physically stops deletion even if the purge worker has a bug. Placement sets the STORAGE hold
/// FIRST, then the database row. Work the crash through both orders:
/// </para>
/// <para>
/// Storage hold set, DB insert fails → an over-protected object. Reconciliation (Part G, check 3)
/// reports it; a human releases it. Harmless. DB inserted, storage hold fails → the system
/// BELIEVES an object is held that is physically deletable — the dangerous direction, because
/// every read of the policy layer now reports protection that does not exist. So storage first.
/// </para>
/// <para>
/// Release inverts the order for the same reason: database first, storage second. A crash
/// between them leaves the object over-protected (released in policy, still held physically),
/// which reconciliation reports and a human clears. The failure mode in both flows is
/// "over-protected and visible", never "unprotected and invisible".
/// </para>
/// </remarks>
public static class LegalHoldEndpoints
{
    private const int MaxObjectsPerCustomerHold = 10_000;

    /// <summary>Maps the legal-hold endpoints, all behind the staff policy.</summary>
    /// <param name="app">The web application.</param>
    /// <returns>The application, for chaining.</returns>
    public static WebApplication MapLegalHoldEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        _ = app.MapPost("/v1/statements/{statementId:guid}/legal-holds", PlaceOnStatementAsync)
            .RequireAuthorization(global::Delivery.Api.Configuration.DeliveryApiExtensions.StaffPolicy)
            .WithName("PlaceStatementLegalHold")
            .WithSummary("Places a legal hold on one statement, in the object store and the database.");

        _ = app.MapPost("/v1/customers/{customerId:guid}/legal-holds", PlaceOnCustomerAsync)
            .RequireAuthorization(global::Delivery.Api.Configuration.DeliveryApiExtensions.StaffPolicy)
            .WithName("PlaceCustomerLegalHold")
            .WithSummary("Places a legal hold on all of a customer's statements, present and future.");

        _ = app.MapDelete("/v1/legal-holds/{holdId:guid}", ReleaseAsync)
            .RequireAuthorization(global::Delivery.Api.Configuration.DeliveryApiExtensions.StaffPolicy)
            .WithName("ReleaseLegalHold")
            .WithSummary("Releases a hold. The row survives as the record that data was preserved.");

        _ = app.MapGet("/v1/legal-holds", ListAsync)
            .RequireAuthorization(global::Delivery.Api.Configuration.DeliveryApiExtensions.StaffPolicy)
            .WithName("ListLegalHolds")
            .WithSummary("Lists holds, keyset-paginated.");

        return app;
    }

    private static async Task<IResult> PlaceOnStatementAsync(
        Guid statementId,
        PlaceHoldRequest request,
        HttpContext http,
        RetentionSweepRepository statements,
        LegalHoldRepository holds,
        IStatementObjectAdmin objectAdmin,
        IUnitOfWork unitOfWork,
        RequestAudit audit,
        IIdGenerator ids,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        PurgeCandidate? statement = await statements.FindStatementRefAsync(statementId, cancellationToken)
            .ConfigureAwait(false);
        if (statement is null)
        {
            return Results.NotFound();
        }

        // Storage first (see the class remarks). A statement with no bytes yet (PENDING) has
        // nothing to hold physically; the DB row still gates the purge that could one day run.
        if (statement.StorageKey is not null)
        {
            await objectAdmin.SetLegalHoldAsync(statement.StorageKey, place: true, cancellationToken)
                .ConfigureAwait(false);
        }

        var holdId = ids.NewId();
        await unitOfWork.ExecuteAsync(
            async (NpgsqlTransaction tx, CancellationToken token) =>
            {
                // customer_id ALWAYS populated (V021, ADR-0040): the erasure gate asks "any
                // hold affecting this customer?" with one indexed predicate, and a statement-
                // scoped hold that left it null was invisible to that gate - the audit's
                // CRITICAL. Scope is expressed by statement_id alone now.
                await holds.PlaceAsync(
                    new LegalHoldRow(
                        holdId, statementId, statement.CustomerId, request!.CaseReference!.Trim(),
                        request.Reason, Actor(http), DateTimeOffset.UtcNow, null),
                    tx, token).ConfigureAwait(false);

                _ = await audit.RecordAsync(
                    http, AuditAction.LegalHoldPlaced, AuditOutcome.Success,
                    new CustomerId(statement.CustomerId), new StatementId(statementId), tx,
                    detail: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["hold_id"] = holdId.ToString("D"),
                        ["case_reference"] = request.CaseReference,
                        ["scope"] = "statement",
                        ["object_hold_set"] = statement.StorageKey is not null,
                    },
                    cancellationToken: token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return Results.Created(
            string.Create(CultureInfo.InvariantCulture, $"/v1/legal-holds/{holdId:D}"),
            new { holdId });
    }

    private static async Task<IResult> PlaceOnCustomerAsync(
        Guid customerId,
        PlaceHoldRequest request,
        HttpContext http,
        RetentionSweepRepository statements,
        LegalHoldRepository holds,
        IStatementObjectAdmin objectAdmin,
        IUnitOfWork unitOfWork,
        RequestAudit audit,
        IIdGenerator ids,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        // Storage holds on every EXISTING object, paged. Statements generated AFTER this hold
        // are covered anyway: the purge worker and the erasure evaluator check hold status at
        // decision time against the customer-scoped DB row, never at generation time. A partial
        // failure mid-loop leaves some objects over-protected and none under-protected -
        // reconciliation's drift check reports the difference.
        int held = 0;
        var afterId = Guid.Empty;
        DateOnly afterPeriod = DateOnly.MinValue;
        while (held < MaxObjectsPerCustomerHold)
        {
            IReadOnlyList<StatementStorageRef> page = await statements.ListStorageRefsForCustomerAsync(
                new CustomerId(customerId), afterId, afterPeriod, 200, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            foreach (StatementStorageRef reference in page)
            {
                await objectAdmin.SetLegalHoldAsync(reference.StorageKey, place: true, cancellationToken)
                    .ConfigureAwait(false);
                held++;
            }

            afterId = page[^1].Id;
            afterPeriod = page[^1].PeriodStart;
        }

        var holdId = ids.NewId();
        await unitOfWork.ExecuteAsync(
            async (NpgsqlTransaction tx, CancellationToken token) =>
            {
                await holds.PlaceAsync(
                    new LegalHoldRow(
                        holdId, null, customerId, request!.CaseReference!.Trim(), request.Reason,
                        Actor(http), DateTimeOffset.UtcNow, null),
                    tx, token).ConfigureAwait(false);

                _ = await audit.RecordAsync(
                    http, AuditAction.LegalHoldPlaced, AuditOutcome.Success,
                    new CustomerId(customerId), null, tx,
                    detail: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["hold_id"] = holdId.ToString("D"),
                        ["case_reference"] = request.CaseReference,
                        ["scope"] = "customer",
                        ["objects_held"] = held,
                    },
                    cancellationToken: token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return Results.Created(
            string.Create(CultureInfo.InvariantCulture, $"/v1/legal-holds/{holdId:D}"),
            new { holdId });
    }

    private static async Task<IResult> ReleaseAsync(
        Guid holdId,

        // Explicit [FromBody]: minimal APIs refuse an INFERRED body on DELETE at route-building
        // time, which broke the whole host's endpoint resolution - found by the Prompt 7
        // endpoint-enumeration test. An explicit attribute is allowed, and the release reason
        // genuinely belongs in the body.
        [Microsoft.AspNetCore.Mvc.FromBody] ReleaseHoldRequest? request,
        HttpContext http,
        RetentionSweepRepository statements,
        LegalHoldRepository holds,
        IStatementObjectAdmin objectAdmin,
        IUnitOfWork unitOfWork,
        RequestAudit audit,
        CancellationToken cancellationToken)
    {
        // Database first on release (see the class remarks): a crash after this transaction
        // leaves objects over-protected, which reconciliation reports; the reverse order could
        // leave objects deletable while the database still says held.
        (Guid? StatementId, Guid? CustomerId, string CaseReference)? released = null;
        await unitOfWork.ExecuteAsync(
            async (NpgsqlTransaction tx, CancellationToken token) =>
            {
                released = await holds.ReleaseAsync(
                    holdId, Actor(http), request?.ReleaseReason, tx, token).ConfigureAwait(false);
                if (released is null)
                {
                    return;
                }

                _ = await audit.RecordAsync(
                    http, AuditAction.LegalHoldReleased, AuditOutcome.Success,
                    released.Value.CustomerId is { } c ? new CustomerId(c) : null,
                    released.Value.StatementId is { } s ? new StatementId(s) : null,
                    tx,
                    detail: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["hold_id"] = holdId.ToString("D"),
                        ["case_reference"] = released.Value.CaseReference,
                        ["release_reason"] = request?.ReleaseReason,
                    },
                    cancellationToken: token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        if (released is null)
        {
            return Results.NotFound();
        }

        // Now the physical layer. An object may be covered by ANOTHER still-active hold (a
        // statement hold and a customer hold can overlap), so each object's store hold is
        // released only when no active DB hold still covers it.
        if (released.Value.StatementId is { } statementId)
        {
            PurgeCandidate? statement = await statements.FindStatementRefAsync(statementId, cancellationToken)
                .ConfigureAwait(false);
            if (statement?.StorageKey is not null)
            {
                string? still = await holds.ActiveCaseReferenceForStatementAsync(
                    new StatementId(statementId), new CustomerId(statement.CustomerId), cancellationToken)
                    .ConfigureAwait(false);
                if (still is null)
                {
                    await objectAdmin.SetLegalHoldAsync(statement.StorageKey, place: false, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        else if (released.Value.CustomerId is { } customerId)
        {
            var afterId = Guid.Empty;
            DateOnly afterPeriod = DateOnly.MinValue;
            while (true)
            {
                IReadOnlyList<StatementStorageRef> page = await statements.ListStorageRefsForCustomerAsync(
                    new CustomerId(customerId), afterId, afterPeriod, 200, cancellationToken).ConfigureAwait(false);
                if (page.Count == 0)
                {
                    break;
                }

                foreach (StatementStorageRef reference in page)
                {
                    string? still = await holds.ActiveCaseReferenceForStatementAsync(
                        new StatementId(reference.Id), new CustomerId(customerId), cancellationToken)
                        .ConfigureAwait(false);
                    if (still is null)
                    {
                        await objectAdmin.SetLegalHoldAsync(reference.StorageKey, place: false, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                afterId = page[^1].Id;
                afterPeriod = page[^1].PeriodStart;
            }
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ListAsync(
        bool? active,
        string? cursor,
        int? limit,
        LegalHoldRepository holds,
        CancellationToken cancellationToken)
    {
        int pageSize = Math.Clamp(limit ?? 50, 1, 200);
        (DateTimeOffset After, Guid AfterId)? position = ParseCursor(cursor);

        IReadOnlyList<LegalHoldRow> rows = await holds.ListAsync(
            active ?? false,
            position?.After,
            position?.AfterId ?? Guid.Empty,
            pageSize,
            cancellationToken).ConfigureAwait(false);

        string? next = rows.Count == pageSize
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{rows[^1].PlacedAt.UtcTicks:D}.{rows[^1].Id:N}")
            : null;

        return Results.Ok(new
        {
            holds = rows.Select(r => new
            {
                r.Id,
                r.StatementId,
                r.CustomerId,
                r.CaseReference,
                r.Reason,
                r.PlacedBy,
                r.PlacedAt,
                r.ReleasedAt,
            }),
            cursor = next,
        });
    }

    private static IResult? Validate(PlaceHoldRequest? request)
    {
        // Mandatory, not merely validated: a hold with no case reference is a hold nobody can
        // justify or ever release with confidence — the case is the only key that unlocks it.
        if (request is null || string.IsNullOrWhiteSpace(request.CaseReference))
        {
            return Results.Problem(
                title: "caseReference is mandatory",
                detail: "A legal hold must cite the matter it preserves evidence for.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return null;
    }

    private static (DateTimeOffset, Guid)? ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        string[] parts = cursor.Split('.', 2);
        return parts.Length == 2
               && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
               && Guid.TryParseExact(parts[1], "N", out Guid id)
            ? (new DateTimeOffset(ticks, TimeSpan.Zero), id)
            : null;
    }

    private static string Actor(HttpContext http) =>
        http.User.FindFirst("sub")?.Value ?? "staff-unknown";

    /// <summary>A hold placement request.</summary>
    /// <param name="Reason">Why the hold is needed.</param>
    /// <param name="CaseReference">The matter. Mandatory.</param>
    public sealed record PlaceHoldRequest(string? Reason, string? CaseReference);

    /// <summary>A hold release request.</summary>
    /// <param name="ReleaseReason">Why the hold can end.</param>
    public sealed record ReleaseHoldRequest(string? ReleaseReason);
}
