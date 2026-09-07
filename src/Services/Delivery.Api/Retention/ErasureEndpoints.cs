using Npgsql;
using StatementDelivery.Domain.Abstractions;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Retention;
using StatementDelivery.Persistence.Retention;
using StatementDelivery.Persistence.Uow;
using StatementDelivery.ServiceDefaults.Auditing;
using StatementDelivery.ServiceDefaults.Retention;

namespace Delivery.Api.Retention;

/// <summary>Crypto-erasure requests: schedule with a cooling-off window, or cancel inside it.</summary>
/// <remarks>
/// <para>
/// Data-protection-officer scope, above staff, because this schedules the one operation no
/// backup can undo. The decision comes from the shared RetentionDecisionEngine, evaluated over
/// the AGGREGATE of the customer's statements: blocked if ANY is blocked — a hold on one
/// statement holds the customer's erasure, because destroying the CEK would destroy that
/// statement too.
/// </para>
/// <para>
/// The 409s cite their basis (the case reference, or the statute and its date). That is hard
/// constraint 2: a legal conflict is surfaced with the law attached, never silently resolved.
/// </para>
/// <para>
/// Evaluation HERE uses the database's mirror of the object-lock dates (fast, advisory);
/// the EXECUTOR re-evaluates against the object store at destruction time (authoritative,
/// hard constraint 4) — and re-checks the hold, because seven days is plenty of time for
/// litigation to start.
/// </para>
/// </remarks>
public static class ErasureEndpoints
{
    /// <summary>Maps the erasure endpoints, behind the DPO policy.</summary>
    /// <param name="app">The web application.</param>
    /// <returns>The application, for chaining.</returns>
    public static WebApplication MapErasureEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        _ = app.MapPost("/v1/customers/{customerId:guid}/erasure", RequestAsync)
            .WithTags(global::Delivery.Api.Configuration.DeliveryApiOpenApi.Tags.Erasure)
            .RequireAuthorization(global::Delivery.Api.Configuration.DeliveryApiExtensions.DpoPolicy)
            .WithName("RequestErasure")
            .WithSummary("Schedules crypto-erasure with a seven-day cooling-off window, or explains why it cannot happen.")
            .WithDescription(
                "DPO scope (`staff=true&dpo=true` on the token endpoint). Statements sit under a Compliance-mode Object Lock and cannot "
                + "be deleted, so erasure destroys the customer's content-encryption key instead: the ciphertext stays, permanently "
                + "unreadable. The request is evaluated over ALL of the customer's statements and refused with 409 if any one is under "
                + "a legal hold or inside a statutory retention period; the 409 body names the case reference or the statute and date. "
                + "Accepted requests are executed by the retention worker after a seven-day cooling-off window, during which "
                + "`DELETE` on this path cancels them.");

        _ = app.MapDelete("/v1/customers/{customerId:guid}/erasure", CancelAsync)
            .WithTags(global::Delivery.Api.Configuration.DeliveryApiOpenApi.Tags.Erasure)
            .RequireAuthorization(global::Delivery.Api.Configuration.DeliveryApiExtensions.DpoPolicy)
            .WithName("CancelErasure")
            .WithSummary("Cancels a scheduled erasure while the cooling-off window is open.")
            .WithDescription(
                "DPO scope. Withdraws a pending erasure before the worker executes it. 404 if nothing is scheduled for the customer; "
                + "once the key has been destroyed there is nothing to cancel and the customer's statements answer 410.");

        return app;
    }

    private static async Task<IResult> RequestAsync(
        Guid customerId,
        ErasureRequest? request,
        HttpContext http,
        HoldResolution holds,
        RetentionSweepRepository statements,
        ErasureRepository erasures,
        IUnitOfWork unitOfWork,
        RequestAudit audit,
        IIdGenerator ids,
        TimeProvider time,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Reason)
            || string.IsNullOrWhiteSpace(request.RequestReference))
        {
            return Results.Problem(
                title: "reason and requestReference are mandatory",
                detail: "An erasure without a documented basis cannot be defended later.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var customer = new CustomerId(customerId);

        // Idempotent re-request: the live schedule answers again with its original dates.
        ErasureRequestRow? existing = await erasures.FindScheduledAsync(customer, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return Results.Accepted(value: new
            {
                erasureId = existing.Id,
                scheduledFor = existing.DueAt,
                coolingOffEnds = existing.DueAt,
            });
        }

        // The aggregate context: worst hold (BOTH layers, through the shared resolver - a hold
        // on ANY statement, either scope, either layer, blocks), latest retention date, key
        // status. Evaluated through the SAME engine and the SAME context factory the purge
        // worker uses - one precedence order, one mapping, one place. The store-side walk costs
        // one S3 read per statement in the worst case; erasure requests are rare and the
        // executor re-resolves anyway before anything irreversible happens.
        CustomerKeyState? key = await erasures.FindKeyStateAsync(customer, cancellationToken).ConfigureAwait(false);
        HoldState holdState = await holds.ResolveForCustomerAsync(customer, cancellationToken)
            .ConfigureAwait(false);
        DateOnly? maxRetainUntil = await statements.MaxRetainUntilForCustomerAsync(customer, cancellationToken)
            .ConfigureAwait(false);
        DateOnly today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        RetentionDecision decision = RetentionDecisionEngine.Decide(RetentionContextFactory.Create(
            holdState,
            key?.DestroyedAt is not null,
            maxRetainUntil ?? today,

            // Null, honestly: this advisory evaluation does not HEAD every object for lock
            // dates; the statute date answers, and the executor consults the store again.
            objectInfo: null,
            today));

        switch (decision)
        {
            case RetentionDecision.BlockedByLegalHold blocked:
                _ = await audit.RecordAsync(
                    http, AuditAction.ErasureBlocked, AuditOutcome.Denied, customer, null,
                    denialReasonCode: "LEGAL_HOLD",
                    detail: Detail(request, ("case_reference", blocked.CaseReference)),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return Results.Json(new
                {
                    reason = "LEGAL_HOLD",
                    caseReference = blocked.CaseReference,
                    detail = "Cannot erase: statements are subject to an active legal hold.",
                }, statusCode: StatusCodes.Status409Conflict);

            case RetentionDecision.BlockedByObjectLock lockBlocked:
                _ = await audit.RecordAsync(
                    http, AuditAction.ErasureBlocked, AuditOutcome.Denied, customer, null,
                    denialReasonCode: "STATUTORY_RETENTION",
                    detail: Detail(request, ("retain_until", lockBlocked.Until.ToString("O"))),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return Results.Json(new
                {
                    reason = "STATUTORY_RETENTION",
                    retainUntil = lockBlocked.Until,
                    basis = RetentionDecisionEngine.StatutoryBasis,
                    detail = "Cannot erase: statutory retention period has not expired.",
                }, statusCode: StatusCodes.Status409Conflict);

            case RetentionDecision.RetainStatutory retain:
                _ = await audit.RecordAsync(
                    http, AuditAction.ErasureBlocked, AuditOutcome.Denied, customer, null,
                    denialReasonCode: "STATUTORY_RETENTION",
                    detail: Detail(request, ("retain_until", retain.Until.ToString("O"))),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return Results.Json(new
                {
                    reason = "STATUTORY_RETENTION",
                    retainUntil = retain.Until,
                    basis = retain.Basis,
                    detail = "Cannot erase: statutory retention period has not expired.",
                }, statusCode: StatusCodes.Status409Conflict);

            case RetentionDecision.AlreadyErased:
                return Results.Json(new
                {
                    reason = "ALREADY_ERASED",
                    detail = "This customer's key material was already destroyed.",
                }, statusCode: StatusCodes.Status409Conflict);

            case RetentionDecision.Purge:
                break;

            default:
                throw new InvalidOperationException($"Unhandled retention decision {decision}.");
        }

        if (key is null)
        {
            // No key row means no encrypted statements exist. Nothing to destroy; scheduling a
            // destruction for a key that does not exist would complete vacuously and record an
            // erasure that erased nothing.
            return Results.Problem(
                title: "Nothing to erase",
                detail: "This customer has no key material and no encrypted statements.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Irreversible operations get a reversal window (ADR-0035): schedule, never execute.
        int coolingOffDays = configuration.GetValue("Retention:ErasureCoolingOffDays", 7);
        DateTimeOffset dueAt = time.GetUtcNow().AddDays(coolingOffDays);
        var erasureId = ids.NewId();

        bool scheduled = false;
        await unitOfWork.ExecuteAsync(
            async (NpgsqlTransaction tx, CancellationToken token) =>
            {
                scheduled = await erasures.ScheduleAsync(
                    new ErasureRequestRow(
                        erasureId, customerId, request.Reason!.Trim(), request.RequestReference!.Trim(),
                        Actor(http), time.GetUtcNow(), dueAt, "SCHEDULED"),
                    tx, token).ConfigureAwait(false);

                if (scheduled)
                {
                    _ = await audit.RecordAsync(
                        http, AuditAction.ErasureScheduled, AuditOutcome.Success, customer, null, tx,
                        detail: Detail(
                            request,
                            ("erasure_id", erasureId.ToString("D")),
                            ("due_at", dueAt.ToString("O"))),
                        cancellationToken: token).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (!scheduled)
        {
            // The key row was not ACTIVE — a concurrent request won, or rotation is in flight.
            return Results.Conflict(new
            {
                reason = "NOT_SCHEDULABLE",
                detail = "The customer's key is not in a schedulable state. Retry after inspecting it.",
            });
        }

        return Results.Accepted(value: new
        {
            erasureId,
            scheduledFor = dueAt,
            coolingOffEnds = dueAt,
        });
    }

    private static async Task<IResult> CancelAsync(
        Guid customerId,
        HttpContext http,
        ErasureRepository erasures,
        IUnitOfWork unitOfWork,
        RequestAudit audit,
        CancellationToken cancellationToken)
    {
        Guid? cancelled = null;
        await unitOfWork.ExecuteAsync(
            async (NpgsqlTransaction tx, CancellationToken token) =>
            {
                cancelled = await erasures.CancelAsync(
                    new CustomerId(customerId), Actor(http), tx, token).ConfigureAwait(false);

                if (cancelled is { } id)
                {
                    _ = await audit.RecordAsync(
                        http, AuditAction.ErasureCancelled, AuditOutcome.Success,
                        new CustomerId(customerId), null, tx,
                        detail: new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["erasure_id"] = id.ToString("D"),
                        },
                        cancellationToken: token).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);

        return cancelled is null ? Results.NotFound() : Results.NoContent();
    }

    private static Dictionary<string, object?> Detail(
        ErasureRequest request, params (string Key, object? Value)[] extra)
    {
        var detail = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["reason"] = request.Reason,
            ["request_reference"] = request.RequestReference,
        };
        foreach ((string key, object? value) in extra)
        {
            detail[key] = value;
        }

        return detail;
    }

    private static string Actor(HttpContext http) =>
        http.User.FindFirst("sub")?.Value ?? "dpo-unknown";

    /// <summary>An erasure request body.</summary>
    /// <param name="Reason">The statutory basis, e.g. "POPIA s24 data subject request".</param>
    /// <param name="RequestReference">The DSR reference.</param>
    public sealed record ErasureRequest(string? Reason, string? RequestReference);
}
