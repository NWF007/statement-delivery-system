using System.Globalization;
using StatementDelivery.Domain.Abstractions;
using StatementDelivery.Persistence.Retention;

namespace Delivery.Api.Retention;

/// <summary>Reconciliation: request a run, read the latest results. Staff scope.</summary>
/// <remarks>
/// The API only ENQUEUES and READS. The checks themselves need the object store, and this
/// service deliberately holds no read credentials for it — the leader-elected retention worker
/// executes runs and writes findings. A 202 with the run id is the honest contract for work
/// that walks bounded samples of a 2.5-billion-object system.
/// </remarks>
public static class ReconciliationEndpoints
{
    private const int FindingsCap = 500;

    /// <summary>Maps the reconciliation endpoints, behind the staff policy.</summary>
    /// <param name="app">The web application.</param>
    /// <returns>The application, for chaining.</returns>
    public static WebApplication MapReconciliationEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        _ = app.MapPost("/v1/admin/reconciliation/run", RunAsync)
            .RequireAuthorization(global::Delivery.Api.Configuration.DeliveryApiExtensions.StaffPolicy)
            .WithName("RequestReconciliation")
            .WithSummary("Enqueues a reconciliation run. The retention worker executes it.");

        _ = app.MapGet("/v1/admin/reconciliation/latest", LatestAsync)
            .RequireAuthorization(global::Delivery.Api.Configuration.DeliveryApiExtensions.StaffPolicy)
            .WithName("LatestReconciliation")
            .WithSummary("The most recent completed run and its findings.");

        return app;
    }

    private static async Task<IResult> RunAsync(
        HttpContext http,
        ReconciliationRepository reconciliation,
        IIdGenerator ids,
        CancellationToken cancellationToken)
    {
        var runId = ids.NewId();
        await reconciliation.EnqueueAsync(
            runId,
            http.User.FindFirst("sub")?.Value ?? "staff-unknown",
            cancellationToken).ConfigureAwait(false);

        return Results.Accepted(
            string.Create(CultureInfo.InvariantCulture, $"/v1/admin/reconciliation/latest"),
            new { runId, status = "REQUESTED" });
    }

    private static async Task<IResult> LatestAsync(
        ReconciliationRepository reconciliation,
        CancellationToken cancellationToken)
    {
        ReconciliationRunRow? run = await reconciliation.FindLatestAsync(cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return Results.NotFound(new { detail = "No reconciliation run has completed yet." });
        }

        IReadOnlyList<ReconciliationFinding> findings = await reconciliation.ListFindingsAsync(
            run.Id, FindingsCap, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new
        {
            runId = run.Id,
            run.Status,
            run.RequestedBy,
            run.RequestedAt,
            run.StartedAt,
            run.CompletedAt,
            criticalFindings = findings.Count(f => f.Severity == "CRITICAL"),
            findings = findings.Select(f => new
            {
                check = f.CheckName,
                f.Severity,
                f.Subject,
                f.Detail,
            }),
        });
    }
}
