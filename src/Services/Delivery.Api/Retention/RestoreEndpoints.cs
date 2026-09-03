using Npgsql;
using StatementDelivery.Domain.Abstractions;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Statements;
using StatementDelivery.Persistence.Repositories;
using StatementDelivery.Persistence.Retention;
using StatementDelivery.Persistence.Uow;
using StatementDelivery.ServiceDefaults.Auditing;

namespace Delivery.Api.Retention;

/// <summary>Archive restore: request asynchronously, poll for completion.</summary>
/// <remarks>
/// The asynchrony is IN the API on purpose. A restore from cold storage takes hours in
/// production; a synchronous endpoint that blocks for four hours is not an alternative design,
/// it is a broken one. So: 202 with an estimate, a poll endpoint, and a
/// <c>statement.restored</c> event through the outbox when it completes — which is how banks
/// genuinely answer old-statement requests: "we'll notify you when it's ready."
/// </remarks>
public static class RestoreEndpoints
{
    /// <summary>Maps the restore endpoints. Customer-authenticated, owner-scoped.</summary>
    /// <param name="app">The web application.</param>
    /// <returns>The application, for chaining.</returns>
    public static WebApplication MapRestoreEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        _ = app.MapPost("/v1/statements/{statementId:guid}/restore", RequestAsync)
            .RequireAuthorization()

            // Customer-facing and it queues WORK (a simulated or real cold-tier retrieval), so
            // it shares the per-caller budget the other customer surfaces carry - a gap found
            // while reviewing the authorization/rate-limit matrix across every route. The
            // pending-restore reuse already blunts repeats; the limiter is the backstop.
            .RequireRateLimiting(global::Delivery.Api.Configuration.DeliveryApiExtensions.PerCallerPolicy)
            .WithName("RequestRestore")
            .WithSummary("Requests a restore from cold storage. 202: this takes a while.");

        _ = app.MapGet("/v1/statements/{statementId:guid}/restore/{restoreId:guid}", StatusAsync)
            .RequireAuthorization()
            .RequireRateLimiting(global::Delivery.Api.Configuration.DeliveryApiExtensions.PerCallerPolicy)
            .WithName("GetRestoreStatus")
            .WithSummary("Polls one restore request.");

        return app;
    }

    private static async Task<IResult> RequestAsync(
        Guid statementId,
        DateOnly? period,
        HttpContext http,
        IStatementReadRepository statements,
        RestoreRequestRepository restores,
        IUnitOfWork unitOfWork,
        RequestAudit audit,
        IIdGenerator ids,
        TimeProvider time,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (period is null)
        {
            return Results.Problem(
                title: "period is required",
                detail: "Pass the statement period, e.g. ?period=2026-08-01. It is the partition key.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (Subject(http) is not { } owner)
        {
            return Results.NotFound();
        }

        // Owner-scoped read: an unowned or unknown statement is a uniform 404 (ADR-0012).
        Statement? statement = await statements.FindAsync(
            new StatementId(statementId), period.Value, owner, cancellationToken).ConfigureAwait(false);
        if (statement is null)
        {
            return Results.NotFound();
        }

        if (statement.Status != StatementStatus.Archived)
        {
            // The message must not promise a download the statement cannot deliver: a PURGED row
            // reaches this branch too, and "can be downloaded directly" would be false there.
            return Results.Problem(
                title: "Statement is not archived",
                detail: "Only archived statements need a restore. Check the statement's status.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // A pending restore answers again with its original id and estimate — a customer
        // clicking twice must not queue two retrievals.
        RestoreRequestRow? pending = await restores.FindPendingAsync(
            new StatementId(statementId), cancellationToken).ConfigureAwait(false);
        if (pending is not null)
        {
            return Accepted(pending.Id, pending.DueAt);
        }

        // The estimate. Locally this is the SIMULATED latency (ADR-0038: MinIO has no Glacier);
        // in production it comes from the storage class's published retrieval window.
        int delayMinutes = configuration.GetValue("Retention:Restore:SimulatedDelayMinutes", 5);
        DateTimeOffset dueAt = time.GetUtcNow().AddMinutes(delayMinutes);
        var restoreId = ids.NewId();

        await unitOfWork.ExecuteAsync(
            async (NpgsqlTransaction tx, CancellationToken token) =>
            {
                await restores.CreateAsync(
                    new RestoreRequestRow(
                        restoreId, statementId, period.Value, owner.Value, Actor(http),
                        time.GetUtcNow(), dueAt, "PENDING", null, null),
                    tx, token).ConfigureAwait(false);

                _ = await audit.RecordAsync(
                    http, AuditAction.StatementRestoreRequested, AuditOutcome.Success,
                    owner, new StatementId(statementId), tx,
                    detail: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["restore_id"] = restoreId.ToString("D"),
                        ["due_at"] = dueAt.ToString("O"),
                    },
                    cancellationToken: token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return Accepted(restoreId, dueAt);
    }

    private static async Task<IResult> StatusAsync(
        Guid statementId,
        Guid restoreId,
        HttpContext http,
        RestoreRequestRepository restores,
        CancellationToken cancellationToken)
    {
        if (Subject(http) is not { } owner)
        {
            return Results.NotFound();
        }

        RestoreRequestRow? restore = await restores.FindAsync(
            restoreId, new StatementId(statementId), cancellationToken).ConfigureAwait(false);

        // Uniform 404 for missing AND unowned, same rule as statements themselves.
        if (restore is null || restore.CustomerId != owner.Value)
        {
            return Results.NotFound();
        }

        return Results.Ok(new
        {
            status = restore.Status,
            availableAt = restore.AvailableAt,
            estimatedAvailableAt = restore.DueAt,
            expiresAt = restore.ExpiresAt,
        });
    }

    private static IResult Accepted(Guid restoreId, DateTimeOffset dueAt) =>
        Results.Accepted(value: new
        {
            restoreId,
            estimatedAvailableAt = dueAt,
            notifyOn = "statement.restored",
        });

    private static CustomerId? Subject(HttpContext http)
    {
        string? claim = http.User.FindFirst("sub")?.Value;
        return Guid.TryParse(claim, out Guid subject) ? new CustomerId(subject) : null;
    }

    private static string Actor(HttpContext http) =>
        http.User.FindFirst("sub")?.Value ?? "customer-unknown";
}
