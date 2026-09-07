using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Delivery.Api.Configuration;

/// <summary>
/// Shapes the OpenAPI document so that the Scalar page is usable without the README.
/// </summary>
/// <remarks>
/// <para>
/// A reviewer's most likely path is the Scalar page, not curl. Everything here exists because a
/// walkthrough of that page from a fresh session (docs/reviews/2026-09-07-scalar-walkthrough.md)
/// found five things a stranger could not do: authenticate (no security scheme, so no Authorize
/// button), find a starting point (no document description, tags in registration order), get a
/// first request to succeed (no example values; <c>from</c>/<c>to</c> declared optional so the
/// client dropped them), discover the token endpoint (excluded from the document), and learn that
/// a download link is meant to be redeemed twice.
/// </para>
/// <para>
/// Nothing here changes runtime behaviour. Security schemes in the document describe how the API
/// already authenticates; they do not enforce anything. Enforcement is <c>RequireAuthorization</c>
/// on the endpoints and the <c>EndpointDataSource</c> enumeration test.
/// </para>
/// </remarks>
public static class DeliveryApiOpenApi
{
    /// <summary>The security scheme name the document and the Scalar options both refer to.</summary>
    public const string BearerScheme = "Bearer";

    /// <summary>The first demo customer the seed creates, fixed so examples can name it.</summary>
    /// <remarks>Mirrors <c>DemoRun.FirstDemoCustomerId</c> in tools/seed; the ids are ...101 to ...110.</remarks>
    public const string DemoCustomerId = "11111111-1111-1111-1111-111111111101";

    /// <summary>Tag names, in the order a reader should meet them. Registration order is not reading order.</summary>
    public static class Tags
    {
        public const string StartHere = "Start here";
        public const string Statements = "Statements";
        public const string DownloadLinks = "Download links";
        public const string Audit = "Audit";
        public const string LegalHolds = "Legal holds";
        public const string Erasure = "Erasure";
        public const string Restore = "Restore";
        public const string Reconciliation = "Reconciliation";
        public const string StatementRuns = "Statement runs";
    }

    private static readonly (string Name, string Description)[] OrderedTags =
    [
        (Tags.StartHere, "Mint a development token here, then paste it into the **Bearer Token** box at the top of the page (under *Authentication*). Development only."),
        (Tags.Statements, "The customer's catalogue. `from`/`to` are required: they are what lets PostgreSQL prune partitions."),
        (Tags.DownloadLinks, "Single-use, time-limited links. Issue one, open the `url` in a new tab, then open it again: the second attempt is a 404."),
        (Tags.Audit, "Staff scope. Re-verifies the hash-chained audit trail."),
        (Tags.LegalHolds, "Staff scope. Holds outrank retention, Object Lock and erasure."),
        (Tags.Erasure, "DPO scope. Crypto-erasure with a seven-day cooling-off window; refusals cite the statute."),
        (Tags.Restore, "Cold-storage restores. 202 means the request is queued."),
        (Tags.Reconciliation, "Staff scope. Enqueue a reconciliation run and read its findings."),
        (Tags.StatementRuns, "Staff scope. Batch generation runs and their quarantined failures."),
    ];

    /// <summary>Registers the document and operation transformers.</summary>
    /// <param name="options">The OpenAPI options for the <c>v1</c> document.</param>
    public static void Configure(OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddDocumentTransformer(DescribeDocumentAsync);
        options.AddOperationTransformer(DescribeOperationAsync);
    }

    private static Task DescribeDocumentAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "Statement Delivery API";
        document.Info.Description = """
            Secure delivery of customer account statements: generated monthly, stored encrypted
            under a write-once compliance lock, delivered through **single-use, time-limited links**
            with a tamper-evident audit trail.

            **Start here.**

            1. Open `GET /v1/dev/tokens` under *Start here*, set `customerId` to
               `11111111-1111-1111-1111-111111111101` (already seeded, with last month's statement),
               send it, and copy the `accessToken` from the response. Or paste
               `http://localhost:8081/v1/dev/tokens?customerId=11111111-1111-1111-1111-111111111101`
               into a browser tab.
            2. Paste it into the **Bearer Token** box at the top of this page, under *Authentication*.
               It stays set for every request on this page.
            3. Call `GET /v1/customers/{customerId}/statements` with that customer id and a date range;
               the examples are pre-filled. Copy the `id` and `period.start` of the statement returned.
            4. Call `POST /v1/statements/{statementId}/download-links` with that `id` and `period`.
               Copy the `url` in the response.
            5. Paste the `url` into a **new browser tab**. It is on the Download Gateway (port 8082),
               not this service. A PDF downloads.
            6. **Now paste the same URL again. It returns 404.** The link was consumed on the first
               request and cannot be replayed. Both attempts are in the audit chain; the README shows
               the query that lists them.

            Development only: this page, the token endpoint and the symmetric signing key behind it
            are all refused outside the Development environment.
            """;

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Paste the `accessToken` from `GET /v1/dev/tokens` (Development only). "
                        + "The `sub` claim is the customer; `&staff=true` adds the operator scope, `&staff=true&dpo=true` the erasure scope.",
        };

        // Tag order is reading order. Scalar renders document.tags in the order given, so a reader
        // meets "Start here" and "Statements" before the operator surfaces.
        var known = new HashSet<string>(OrderedTags.Select(static t => t.Name), StringComparer.Ordinal);
        List<OpenApiTag> existing = document.Tags?.Where(t => t.Name is not null && !known.Contains(t.Name)).ToList() ?? [];

        var ordered = new HashSet<OpenApiTag>(new TagNameComparer());
        foreach ((string name, string description) in OrderedTags)
        {
            _ = ordered.Add(new OpenApiTag { Name = name, Description = description });
        }

        foreach (OpenApiTag tag in existing)
        {
            _ = ordered.Add(tag);
        }

        document.Tags = ordered;

        return Task.CompletedTask;
    }

    private static Task DescribeOperationAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        bool anonymous = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any();

        // Every operation that is not explicitly anonymous carries the bearer requirement, which is
        // what gives Scalar its Authorize button and makes the token persist across requests.
        operation.Security = anonymous
            ? []
            : [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BearerScheme, context.Document)] = [] }];

        if (operation.Parameters is not null)
        {
            foreach (OpenApiParameter parameter in operation.Parameters.OfType<OpenApiParameter>())
            {
                ApplyParameterExample(parameter);
            }
        }

        if (operation.RequestBody?.Content is { } content
            && content.TryGetValue("application/json", out OpenApiMediaType? json)
            && json.Schema is not null
            && context.Description.RelativePath?.EndsWith("/download-links", StringComparison.Ordinal) == true)
        {
            json.Example = new JsonObject { ["ttlSeconds"] = 600 };
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Example values that make a first click return data instead of a 404, plus the required
    /// flags the handler signatures cannot express because they parse the values themselves to
    /// return a descriptive 400.
    /// </summary>
    private static void ApplyParameterExample(OpenApiParameter parameter)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly lastMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);

        switch (parameter.Name)
        {
            case "customerId":
                parameter.Example = JsonValue.Create(DemoCustomerId);
                parameter.Description ??= "The demo seed creates 11111111-1111-1111-1111-111111111101 through ...110. Must match the token's `sub`.";
                break;

            case "statementId":
                parameter.Description ??= "Copy the `id` from `GET /v1/customers/{customerId}/statements`. Ids are UUIDv7, generated when the statement is rendered, so there is no fixed example.";
                break;

            case "from":
                parameter.Required = true;
                parameter.Example = JsonValue.Create(today.AddMonths(-12).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                parameter.Description ??= "Start of the range, yyyy-MM-dd. Required. Twelve months back is a good default.";
                break;

            case "to":
                parameter.Required = true;
                parameter.Example = JsonValue.Create(today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                parameter.Description ??= "End of the range, yyyy-MM-dd. Required. Today works.";
                break;

            case "period":
                parameter.Required = true;
                parameter.Example = JsonValue.Create(lastMonth.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                parameter.Description ??= "The statement's `period.start`: the first day of its month. It is the partition key. The seeded statement is last month's.";
                break;

            default:
                break;
        }
    }

    private sealed class TagNameComparer : IEqualityComparer<OpenApiTag>
    {
        public bool Equals(OpenApiTag? x, OpenApiTag? y) => string.Equals(x?.Name, y?.Name, StringComparison.Ordinal);

        public int GetHashCode(OpenApiTag obj) => StringComparer.Ordinal.GetHashCode(obj.Name ?? string.Empty);
    }
}
