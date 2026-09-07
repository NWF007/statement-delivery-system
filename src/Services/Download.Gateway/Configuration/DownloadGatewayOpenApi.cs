using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Download.Gateway.Configuration;

/// <summary>
/// Shapes the gateway's OpenAPI document so its Scalar page explains itself.
/// </summary>
/// <remarks>
/// One endpoint, no authentication, and the one thing a reader must know: the link is consumed on
/// first use. See <c>DeliveryApiOpenApi</c> for why the document carries a "start here".
/// </remarks>
public static class DownloadGatewayOpenApi
{
    /// <summary>The single tag the redemption endpoint sits under.</summary>
    public const string DownloadsTag = "Downloads";

    /// <summary>Registers the document transformer.</summary>
    /// <param name="options">The OpenAPI options for the <c>v1</c> document.</param>
    public static void Configure(OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddDocumentTransformer(static (document, _, _) =>
        {
            document.Info.Title = "Statement Download Gateway";
            document.Info.Description = """
                The public, **unauthenticated** side of statement delivery. There is no Authorize
                button here because the link is the credential: a single-use token minted by the
                Delivery API (`POST /v1/statements/{statementId}/download-links` on port 8081).

                **Start there, not here.** Issue a link on the Delivery API's Scalar page
                (`http://localhost:8081/scalar/v1`), then paste the `url` it returns into a browser
                tab. A PDF downloads. **Paste it again: 404.** The token was consumed atomically on
                the first request, and every failure (consumed, expired, revoked, never existed)
                is the same 404 with the same timing floor, so a replay learns nothing.

                Both the download and the denial are recorded in the audit chain.
                """;

            var tags = new HashSet<OpenApiTag>
            {
                new()
                {
                    Name = DownloadsTag,
                    Description = "Redeem a single-use link. Try it twice: the second attempt is a 404 by design.",
                },
            };

            foreach (OpenApiTag tag in document.Tags ?? new HashSet<OpenApiTag>())
            {
                if (!string.Equals(tag.Name, DownloadsTag, StringComparison.Ordinal))
                {
                    _ = tags.Add(tag);
                }
            }

            document.Tags = tags;
            return Task.CompletedTask;
        });
    }
}
