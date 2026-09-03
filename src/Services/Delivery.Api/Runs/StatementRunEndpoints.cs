using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using StatementDelivery.Domain.Abstractions;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Persistence.Runs;
using StatementDelivery.Persistence.Uow;
using StatementDelivery.ServiceDefaults.Auditing;

namespace Delivery.Api.Runs;

/// <summary>Request body for creating a run.</summary>
/// <param name="PeriodStart">Statement period start. Must be the first of a month.</param>
/// <param name="PeriodEnd">Statement period end.</param>
public sealed record CreateRunRequest(DateOnly PeriodStart, DateOnly PeriodEnd);

/// <summary>A run, as returned to staff.</summary>
/// <param name="RunId">The run.</param>
/// <param name="Status">PLANNING, RUNNING, PAUSED or COMPLETED.</param>
/// <param name="PeriodStart">Period start.</param>
/// <param name="PeriodEnd">Period end.</param>
/// <param name="TotalItems">Planned item count.</param>
/// <param name="Queued">Claimable items.</param>
/// <param name="Rendering">Currently claimed.</param>
/// <param name="Done">Completed.</param>
/// <param name="FailedRetryable">Failed with attempts remaining.</param>
/// <param name="FailedFinal">Quarantined.</param>
public sealed record RunResponse(
    Guid RunId,
    string Status,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    long TotalItems,
    long Queued,
    long Rendering,
    long Done,
    long FailedRetryable,
    long FailedFinal);

/// <summary>Request body for retrying quarantined items.</summary>
/// <param name="ItemIds">Specific items - or null/empty with <paramref name="All"/> true for everything.</param>
/// <param name="All">Retry every quarantined item in the run.</param>
public sealed record RetryFailuresRequest(IReadOnlyList<long>? ItemIds, bool All);

/// <summary>
/// Staff-only run management: trigger, inspect, and retry batch generation runs.
/// </summary>
/// <remarks>
/// <para>
/// LIVES IN DELIVERY.API DELIBERATELY, AND THE HARD CONSTRAINT SURVIVES IT. "No changes to the
/// delivery path" means the customer-facing read and redemption flows - none of which these
/// endpoints touch. This is a new, additive, staff-scoped surface beside the existing staff
/// audit-verify endpoint, placed here because the worker exposes no HTTP beyond health probes
/// (a deliberate design decision) and these routes are specified on the API's :8081 surface.
/// The grants deviation this forces is documented in V017.
/// </para>
/// <para>
/// The generation worker's ORCHESTRATOR does the planning; these endpoints only write the run
/// row (create) and reset item rows (retry). Both are audited IN THE SAME TRANSACTION as the
/// write, per ADR-0025.
/// </para>
/// </remarks>
public static class StatementRunEndpoints
{
    /// <summary>The attempts ceiling used to classify FAILED items. Must match the worker's.</summary>
    /// <remarks>
    /// Read from configuration in both places (<c>Generation:MaxAttempts</c>); the default here
    /// mirrors <c>GenerationWorkerOptions.MaxAttempts</c>.
    /// </remarks>
    private const int DefaultMaxAttempts = 3;

    /// <summary>Maps the run endpoints, all behind the staff policy.</summary>
    /// <param name="app">The web application.</param>
    /// <returns>The application, for chaining.</returns>
    public static WebApplication MapStatementRunEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/v1/statement-runs")
            .RequireAuthorization(Configuration.DeliveryApiExtensions.StaffPolicy);

        _ = group.MapPost("/", CreateAsync)
            .WithName("CreateStatementRun")
            .WithSummary("Requests a batch generation run for a period. Idempotent per period.");

        _ = group.MapGet("/{runId:guid}", GetAsync)
            .WithName("GetStatementRun")
            .WithSummary("Run status and progress counters.");

        _ = group.MapGet("/{runId:guid}/failures", ListFailuresAsync)
            .WithName("ListRunFailures")
            .WithSummary("Quarantined items, keyset-paginated.");

        _ = group.MapPost("/{runId:guid}/failures/retry", RetryFailuresAsync)
            .WithName("RetryRunFailures")
            .WithSummary("Resets quarantined items for a fresh round of attempts. A deliberate operator action.");

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateRunRequest request,
        HttpContext http,
        IStatementRunRepository runs,
        IUnitOfWork unitOfWork,
        RequestAudit audit,
        IIdGenerator ids,
        TimeProvider time,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        StatementPeriod period;
        try
        {
            period = StatementPeriod.Create(request.PeriodStart, request.PeriodEnd);
        }
        catch (StatementDelivery.Domain.Exceptions.DomainException ex)
        {
            return Results.Problem(title: "Invalid period", detail: ex.Message, statusCode: 400);
        }

        int deadlineHours = configuration.GetValue("Generation:RunDeadlineHours", 6);

        // CreateOrGet is idempotent on UNIQUE(period): a re-trigger returns the EXISTING run with
        // its original id and totals: the same runId, with total_items unchanged.
        StatementRun run = await runs.CreateOrGetAsync(
            ids.NewId(), period, time.GetUtcNow().AddHours(deadlineHours), cancellationToken)
            .ConfigureAwait(false);

        bool created = run.Status == RunStatus.Planning && run.TotalItems == 0;

        // The run REQUEST is a state change (a row was written, or deliberately found existing);
        // audited transactionally per ADR-0025. The repository's CreateOrGet manages its own
        // conflict handling, so the audit rides its own short transaction here - the run row is
        // already durable either way, and the audit records the STAFF ACTION, not the insert.
        await audit.RecordAsync(
            http, AuditAction.StatementRunRequested, AuditOutcome.Success, null, null,
            detail: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["run_id"] = run.Id.ToString("D"),
                ["period"] = period.ToString(),
                ["pre_existing"] = !created,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        RunResponse response = await BuildResponseAsync(run, runs, configuration, cancellationToken)
            .ConfigureAwait(false);

        return created
            ? Results.Created(
                string.Create(CultureInfo.InvariantCulture, $"/v1/statement-runs/{run.Id:D}"), response)
            : Results.Ok(response);
    }

    private static async Task<IResult> GetAsync(
        Guid runId,
        IStatementRunRepository runs,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        StatementRun? run = await runs.FindAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(await BuildResponseAsync(run, runs, configuration, cancellationToken)
            .ConfigureAwait(false));
    }

    private static async Task<IResult> ListFailuresAsync(
        Guid runId,
        IStatementRunRepository runs,
        IConfiguration configuration,
        CancellationToken cancellationToken,
        long cursor = 0,
        int limit = 50)
    {
        StatementRun? run = await runs.FindAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return Results.NotFound();
        }

        int maxAttempts = configuration.GetValue("Generation:MaxAttempts", DefaultMaxAttempts);

        IReadOnlyList<FailedItem> page = await runs
            .ListFailuresAsync(runId, maxAttempts, cursor, limit, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new
        {
            items = page,

            // Keyset cursor: the last item id, or null when the page is not full.
            nextCursor = page.Count == Math.Clamp(limit, 1, 500) ? page[^1].ItemId : (long?)null,
        });
    }

    private static async Task<IResult> RetryFailuresAsync(
        Guid runId,
        RetryFailuresRequest request,
        HttpContext http,
        IStatementRunRepository runs,
        IUnitOfWork unitOfWork,
        RequestAudit audit,
        CancellationToken cancellationToken)
    {
        if (request is null || (!request.All && request.ItemIds is not { Count: > 0 }))
        {
            return Results.Problem(
                title: "Nothing to retry",
                detail: "Provide itemIds, or set all=true.",
                statusCode: 400);
        }

        StatementRun? run = await runs.FindAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return Results.NotFound();
        }

        // The reset and its audit record commit together (ADR-0025): quarantined items going back
        // into circulation with no record of who sent them would be an unexplained retry storm in
        // next month's incident review. Audit appended LAST, for the chain-head lock.
        int reset = await unitOfWork.ExecuteAsync(
            async (NpgsqlTransaction transaction, CancellationToken token) =>
            {
                int affected = await runs.RetryFailuresAsync(
                    runId, request.All ? null : request.ItemIds, transaction, token).ConfigureAwait(false);

                if (affected > 0)
                {
                    _ = await audit.RecordAsync(
                        http, AuditAction.StatementRunRetried, AuditOutcome.Success, null, null, transaction,
                        detail: new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["run_id"] = runId.ToString("D"),
                            ["items_reset"] = affected,
                            ["scope"] = request.All ? "all" : "selected",
                        },
                        cancellationToken: token).ConfigureAwait(false);
                }

                return affected;
            },
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(new { itemsReset = reset });
    }

    private static async Task<RunResponse> BuildResponseAsync(
        StatementRun run,
        IStatementRunRepository runs,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        int maxAttempts = configuration.GetValue("Generation:MaxAttempts", DefaultMaxAttempts);
        RunCounters counters = await runs.CountersAsync(run.Id, maxAttempts, cancellationToken)
            .ConfigureAwait(false);

        return new RunResponse(
            run.Id, run.Status, run.PeriodStart, run.PeriodEnd, run.TotalItems,
            counters.Queued, counters.Rendering, counters.Done,
            counters.FailedRetryable, counters.FailedFinal);
    }
}
