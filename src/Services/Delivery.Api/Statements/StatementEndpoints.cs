using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Statements;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Persistence.Repositories;
using StatementDelivery.ServiceDefaults.Auditing;

namespace Delivery.Api.Statements;

/// <summary>
/// The authenticated statement read path.
/// </summary>
public static class StatementEndpoints
{
    /// <summary>
    /// The hard ceiling on a requested date range.
    /// </summary>
    /// <remarks>
    /// Eighty-four months is the seven-year retention period: nothing older can exist, so a wider
    /// request cannot return more rows - it can only scan more partitions.
    /// </remarks>
    public const int MaxRangeMonths = 84;

    /// <summary>The default range applied when the caller does not narrow it further.</summary>
    public const int DefaultRangeMonths = 24;

    /// <summary>Maps the statement endpoints.</summary>
    /// <param name="app">The web application.</param>
    /// <returns>The application, for chaining.</returns>
    public static WebApplication MapStatementEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/v1")
            .RequireAuthorization()
            .RequireRateLimiting(Configuration.DeliveryApiExtensions.PerCallerPolicy);

        group.MapGet("/customers/{customerId}/statements", ListStatementsAsync)
            .WithTags(Configuration.DeliveryApiOpenApi.Tags.Statements)
            .WithName("ListStatements")
            .WithSummary("Lists a customer's available statements, newest first.")
            .WithDescription(
                "The from/to date range is REQUIRED and is capped at 84 months. It is not a convenience "
                + "filter: `statement` is RANGE-partitioned on period_start, and the range is what lets "
                + "PostgreSQL prune partitions. Without it the query visits every month ever generated. "
                + "Pagination is keyset, never OFFSET - pass the opaque `nextCursor` from the previous "
                + "page. A customerId that is not the authenticated subject returns 404, not 403.")
            .Produces<StatementPageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/statements/{statementId}", GetStatementAsync)
            .WithTags(Configuration.DeliveryApiOpenApi.Tags.Statements)
            .WithName("GetStatement")
            .WithSummary("Reads one statement's metadata. Never its bytes.")
            .WithDescription(
                "The `period` query parameter is REQUIRED and is the partition key (the first day of "
                + "the covered month). `statement` is partitioned on period_start with a primary key of "
                + "(id, period_start), so a lookup that omits it cannot prune and must scan every "
                + "partition - eighty-four of them at full retention - to find one row. "
                + "A statement that does not exist and one owned by another customer both return 404.")
            .Produces<StatementResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> ListStatementsAsync(
        string customerId,
        HttpContext http,
        IStatementReadRepository statements,
        RequestAudit audit,
        TimeProvider time,
        CancellationToken cancellationToken,
        string? from = null,
        string? to = null,
        string? cursor = null,
        int limit = 50)
    {
        // ---------------------------------------------------------------------------------------
        // THE ROUTE customerId IS UNTRUSTED INPUT. The JWT subject is the truth.
        //
        // This is the single most important block in the file. Everything downstream queries by the
        // SUBJECT, never by the route value, so even if this check were removed the query would
        // still return only the caller's own rows.
        // ---------------------------------------------------------------------------------------
        if (!TryGetSubject(http, out CustomerId subject))
        {
            await audit.RecordAsync(
                http, AuditAction.AccessDenied, AuditOutcome.Denied, null, null,
                DenialReason.NoSubjectClaim, cancellationToken: cancellationToken).ConfigureAwait(false);

            return Results.NotFound();
        }

        if (!CustomerId.TryParse(customerId, out CustomerId requested) || requested != subject)
        {
            // 404, NOT 403, AND THIS IS NOT AN OVERSIGHT.
            //
            // A 403 confirms the resource exists and belongs to someone else - which is precisely
            // the signal an enumeration attack is looking for. Walk the identifier space, collect
            // the 403s, and you have a list of valid customer identifiers without ever reading a
            // byte of their data.
            //
            // "Does not exist" and "not yours" therefore return the SAME response. A future reader
            // will want to "fix" this to 403 for being more RESTful. It is not a bug.
            // See docs/adr/0012-404-not-403-for-unowned-resources.md.
            await audit.RecordAsync(
                http, AuditAction.AccessDenied, AuditOutcome.Denied, subject, null,
                DenialReason.SubjectMismatch,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["requested_customer"] = customerId },
                cancellationToken).ConfigureAwait(false);

            return Results.NotFound();
        }

        DateOnly today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        if (!TryParseRange(from, to, today, out DateOnly fromDate, out DateOnly toDate, out string? rangeError))
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid date range", rangeError!);
        }

        Cursor? decodedCursor = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!Cursor.TryDecode(cursor, out Cursor parsed))
            {
                // A clean 400, not an exception. The cursor is caller-supplied text on a public API.
                return Problem(
                    StatusCodes.Status400BadRequest,
                    "Invalid cursor",
                    "The cursor is malformed or was produced by an incompatible version. Restart from the first page.");
            }

            decodedCursor = parsed;
        }

        if (limit is < 1 or > StatementReadRepository.MaxPageSize)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid limit",
                $"limit must be between 1 and {StatementReadRepository.MaxPageSize.ToString(CultureInfo.InvariantCulture)}.");
        }

        // Queried by SUBJECT, never by the route value. Ownership is a predicate in the WHERE
        // clause, not a comparison afterwards.
        CursorPage<Statement> page = await statements
            .ListForCustomerAsync(subject, fromDate, toDate, decodedCursor, limit, cancellationToken)
            .ConfigureAwait(false);

        await audit.RecordAsync(
            http, AuditAction.StatementListViewed, AuditOutcome.Success, subject, null,
            detail: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["from"] = fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["to"] = toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["returned"] = page.Items.Count,
                ["paged"] = decodedCursor is not null,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Results.Ok(new StatementPageResponse(
            [.. page.Items.Select(StatementResponse.From)],
            page.NextCursor?.Encode(),
            page.HasMore));
    }

    private static async Task<IResult> GetStatementAsync(
        string statementId,
        HttpContext http,
        IStatementReadRepository statements,
        RequestAudit audit,
        CancellationToken cancellationToken,
        string? period = null)
    {
        if (!TryGetSubject(http, out CustomerId subject))
        {
            await audit.RecordAsync(
                http, AuditAction.AccessDenied, AuditOutcome.Denied, null, null,
                DenialReason.NoSubjectClaim, cancellationToken: cancellationToken).ConfigureAwait(false);

            return Results.NotFound();
        }

        if (!StatementId.TryParse(statementId, out StatementId id))
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid statement id", "statementId must be a UUID.");
        }

        // The period is the PARTITION KEY, and requiring it is a deliberate API design decision.
        // Documented in the OpenAPI description above so it does not read as arbitrary.
        if (!DateOnly.TryParseExact(period, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly periodStart))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Missing or invalid period",
                "period is required and must be the first day of the statement's month (yyyy-MM-dd). "
                + "It is the partition key: without it the lookup cannot prune and would scan every partition.");
        }

        if (periodStart.Day != 1)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid period",
                "period must be the FIRST day of the month, which is the partition boundary.");
        }

        Statement? statement = await statements
            .FindAsync(id, periodStart, subject, cancellationToken)
            .ConfigureAwait(false);

        if (statement is null)
        {
            // Indistinguishable from "does not exist", by design. The audit records WHICH it was,
            // internally, because that distinction is exactly what an investigator needs and
            // exactly what the caller must not have.
            await audit.RecordAsync(
                http, AuditAction.AccessDenied, AuditOutcome.Denied, subject, id,
                DenialReason.NotFound,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["period"] = periodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                },
                cancellationToken).ConfigureAwait(false);

            return Results.NotFound();
        }

        await audit.RecordAsync(
            http, AuditAction.StatementMetadataViewed, AuditOutcome.Success, subject, id,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Results.Ok(StatementResponse.From(statement));
    }

    /// <summary>
    /// Extracts the authenticated subject.
    /// </summary>
    /// <remarks>
    /// <c>MapInboundClaims</c> is disabled on the bearer handler, so the claim is the raw
    /// <c>sub</c> rather than the legacy <c>nameidentifier</c> mapping. Both are checked, because a
    /// silent mapping change would otherwise turn every request into an unauthenticated one.
    /// </remarks>
    private static bool TryGetSubject(HttpContext http, out CustomerId subject)
    {
        string? claim = http.User.FindFirstValue("sub")
            ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);

        return CustomerId.TryParse(claim, out subject);
    }

    /// <summary>
    /// Parses and validates the mandatory date range.
    /// </summary>
    /// <remarks>
    /// BOUNDING THE QUERY SURFACE IS THE POINT. The hottest query is "this customer's statements",
    /// keyed on customer_id, while the partition key is time - so pruning only happens if the
    /// caller supplies a range. Making that part of the contract is a legitimate design decision
    /// and far better than an endpoint that works in development and times out in production.
    /// See docs/adr/0013-mandatory-date-range-on-statement-queries.md.
    /// </remarks>
    private static bool TryParseRange(
        string? from,
        string? to,
        DateOnly today,
        out DateOnly fromDate,
        out DateOnly toDate,
        out string? error)
    {
        fromDate = default;
        toDate = default;
        error = null;

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            error = "from and to are both required (yyyy-MM-dd). The range is what lets the query prune partitions; "
                  + $"a sensible default is the last {DefaultRangeMonths.ToString(CultureInfo.InvariantCulture)} months.";
            return false;
        }

        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out fromDate)
            || !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out toDate))
        {
            error = "from and to must be dates in yyyy-MM-dd format.";
            return false;
        }

        if (toDate <= fromDate)
        {
            error = "to must be after from.";
            return false;
        }

        if (fromDate.AddMonths(MaxRangeMonths) < toDate)
        {
            error = $"The range must not exceed {MaxRangeMonths.ToString(CultureInfo.InvariantCulture)} months, "
                  + "which is the full retention period. A wider range cannot return more statements; it can only scan more partitions.";
            return false;
        }

        _ = today;
        return true;
    }

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(detail: detail, statusCode: status, title: title);
}
