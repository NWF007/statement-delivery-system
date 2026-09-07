using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Asp.Versioning;
using FluentValidation;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using StatementDelivery.ServiceDefaults.RateLimiting;

namespace Download.Gateway.Configuration;

/// <summary>
/// Rate limiting configuration for the public, unauthenticated gateway.
/// </summary>
/// <remarks>
/// Materially tighter than the authenticated API's limits. There is no account to suspend here and
/// no bill to attach abuse to, so the limiter is the only thing standing between a script and the
/// token space.
/// </remarks>
public sealed class GatewayRateLimitOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "RateLimiting";

    /// <summary>
    /// Gets or sets the in-process request budget per client address per window.
    /// </summary>
    /// <remarks>
    /// DELIBERATELY LOOSER THAN <see cref="RedeemPerAddressPerMinute"/>. This is a service-level DoS
    /// guard - what one replica will absorb before it starts shedding - not the anti-enumeration
    /// control. If it were set below the distributed redeem budget it would fire first, and the
    /// limit that actually matters would never be reached or observed.
    /// </remarks>
    [Range(1, 100_000)]
    public int PermitLimit { get; set; } = 120;

    /// <summary>Gets or sets the window length, in seconds.</summary>
    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the number of segments the window is divided into.
    /// </summary>
    /// <remarks>
    /// A sliding window rather than a fixed one. A fixed window lets a caller spend the whole
    /// budget in the last instant of one window and the whole budget again in the first instant of
    /// the next - twice the intended rate, at the worst possible moment.
    /// </remarks>
    [Range(1, 60)]
    public int SegmentsPerWindow { get; set; } = 6;

    /// <summary>
    /// Gets or sets the maximum number of downloads streaming concurrently, process-wide.
    /// </summary>
    /// <remarks>
    /// A request budget alone does not bound a STREAMING endpoint: twenty requests that each hold a
    /// connection open for a multi-megabyte transfer occupy far more of this process than twenty
    /// requests that finish in milliseconds. This is the limit that actually protects the socket
    /// and memory budget.
    /// </remarks>
    [Range(1, 10_000)]
    public int MaxConcurrentDownloads { get; set; } = 64;

    /// <summary>
    /// Gets or sets a value indicating whether to honour X-Forwarded-For for client identity.
    /// </summary>
    /// <remarks>
    /// Only enable this when the gateway genuinely sits behind a trusted proxy whose address is
    /// listed in <see cref="KnownProxies"/>. X-Forwarded-For is caller-supplied: honouring it
    /// without pinning the proxy lets anyone put a fresh value in the header on every request and
    /// land in a fresh rate-limit partition each time, which is a limiter that limits nothing.
    /// </remarks>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// Gets or sets the fleet-wide redemption budget per client address per minute.
    /// </summary>
    /// <remarks>
    /// THE ANTI-ENUMERATION CONTROL. Counted in Redis so it holds across every replica; an
    /// in-process counter would multiply this by the replica count and a caller spreading attempts
    /// across the fleet would never see it. Thirty a minute is generous for a human following links
    /// and ruinous for a script.
    /// </remarks>
    [Range(1, 10_000)]
    public int RedeemPerAddressPerMinute { get; set; } = 30;

    /// <summary>Gets the proxy addresses permitted to set forwarded headers.</summary>
    public IList<string> KnownProxies { get; } = [];
}

/// <summary>
/// Service-specific wiring for the public download gateway.
/// </summary>
public static class DownloadGatewayExtensions
{
    /// <summary>
    /// The per-address policy applied to the token redemption route.
    /// </summary>
    public const string PerAddressPolicy = "per-address";

    // =========================================================================================
    //  ⚠  DO NOT ADD A PER-TOKEN ATTEMPT LIMIT AND IMAGINE IT HELPS AGAINST GUESSING.
    //
    //  It looks protective, which is precisely why it needs saying. A guessing attacker presents a
    //  DIFFERENT value on every attempt, so every attempt hashes to a different key and a per-token
    //  counter never reaches two. A million guesses leaves a million counters sitting at one, all of
    //  them comfortably under any limit you choose.
    //
    //  Per-token limiting constrains only an attacker who already HOLDS a valid token - and single
    //  use already reduces that attacker to exactly one redemption. PER-IP IS THE CONTROL THAT
    //  BITES, because the one thing an enumeration script cannot avoid is making requests from
    //  somewhere. That is why the redeem budget below is partitioned by address and counted in
    //  Redis rather than per process.
    // =========================================================================================

    /// <summary>
    /// Builds the fleet-wide redemption budget from configuration.
    /// </summary>
    /// <param name="limits">The configured limits.</param>
    /// <returns>The rules to apply per client address.</returns>
    public static RateLimitRule[] RedeemRules(GatewayRateLimitOptions limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return [new("redeem-address-minute", limits.RedeemPerAddressPerMinute, TimeSpan.FromMinutes(1))];
    }

    /// <summary>
    /// Adds per-address rate limiting, forwarded-header handling, API versioning and OpenAPI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THERE IS DELIBERATELY NO JWT AUTHENTICATION HERE, AND THAT IS NOT AN OVERSIGHT.
    /// </para>
    /// <para>
    /// The download link is issued to a customer and then followed from an email client, a
    /// forwarded message, a corporate mail gateway that pre-fetches links, or a phone that has
    /// never held a session cookie. There is no bearer token to present, because there is no
    /// session. THE URL TOKEN IS THE CREDENTIAL: a 256-bit CSPRNG value, single-use, short-lived,
    /// redeemed by one atomic UPDATE ... RETURNING that no replica ever serves.
    /// </para>
    /// <para>
    /// That is why this is a separate deployable rather than a route on Delivery.Api. Its threat
    /// exposure is completely different - unauthenticated, internet-facing, scriptable - and
    /// isolating it means a flood against the public endpoint exhausts this fleet's capacity and
    /// not the authenticated API's. See docs/adr/0001-microservices-over-modular-monolith.md.
    /// </para>
    /// <para>
    /// The consequences to hold onto: rate limiting is the primary control rather than a
    /// secondary one; the URL must never be written to a log or a span (see
    /// SensitiveDataRedactor); and a redemption must be atomic, because a token that can be
    /// redeemed twice is a statement delivered to someone who was forwarded the email.
    /// </para>
    /// </remarks>
    /// <param name="builder">The web application builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static WebApplicationBuilder AddDownloadGateway(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<GatewayRateLimitOptions>()
            .Bind(builder.Configuration.GetSection(GatewayRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        GatewayRateLimitOptions configured =
            builder.Configuration.GetSection(GatewayRateLimitOptions.SectionName).Get<GatewayRateLimitOptions>()
            ?? new GatewayRateLimitOptions();

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            if (!configured.TrustForwardedHeaders)
            {
                options.ForwardedHeaders = ForwardedHeaders.None;
                return;
            }

            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // Clearing the defaults and listing proxies explicitly. The framework defaults trust
            // loopback only, which silently does nothing in a container; trusting everything makes
            // the client address caller-controlled, and with it the rate-limit partition.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();

            foreach (string proxy in configured.KnownProxies)
            {
                if (IPAddress.TryParse(proxy, out IPAddress? address))
                {
                    options.KnownProxies.Add(address);
                }
            }
        });

        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.OnRejected = static (context, cancellationToken) =>
            {
                // RETRY-AFTER ON EVERY 429, INCLUDING THE ONES WITH NO METADATA TO READ.
                //
                // Window limiters publish MetadataName.RetryAfter because they know when the window
                // rolls. A CONCURRENCY limiter does not - it has no idea when a permit will free -
                // so the metadata is simply absent, and a naive `if (TryGetMetadata)` silently emits
                // a bare 429 for exactly the case that occurs under load.
                //
                // A client that gets no Retry-After has to guess, and the guess is usually
                // "immediately": the limiter that was meant to shed load becomes the trigger for a
                // retry storm. One conservative second is a far better answer than no answer.
                int seconds =
                    context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter)
                        ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                        : 1;

                context.HttpContext.Response.Headers.RetryAfter =
                    seconds.ToString(CultureInfo.InvariantCulture);

                return ValueTask.CompletedTask;
            };

            // A GLOBAL limiter, not just a named policy. A named policy only protects the routes
            // that remember to opt in; on an unauthenticated surface the default must be protected
            // and the exception must be explicit, not the other way round.
            //
            // CHAINED, because RequireRateLimiting cannot stack: attaching a second policy to the
            // redeem endpoint silently replaced the first, and the concurrency guard spent its
            // whole life dead until the first real execution showed six parallel redemptions all
            // getting through a permit of one. The global chain composes with the endpoint's
            // per-address policy instead of competing with it: leg one is the per-address window
            // below, leg two admits everything except download paths, which pass through a
            // per-address concurrency permit sized by MaxConcurrentDownloads.
            limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(static httpContext =>
                {
                    if (!httpContext.Request.Path.StartsWithSegments("/v1/d", StringComparison.OrdinalIgnoreCase))
                    {
                        return RateLimitPartition.GetNoLimiter("not-a-download");
                    }

                    IOptions<GatewayRateLimitOptions> options =
                        httpContext.RequestServices.GetRequiredService<IOptions<GatewayRateLimitOptions>>();

                    return RateLimitPartition.GetConcurrencyLimiter(
                        "download:" + ClientPartition(httpContext),
                        _ => new ConcurrencyLimiterOptions
                        {
                            PermitLimit = options.Value.MaxConcurrentDownloads,
                            QueueLimit = 0,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        });
                }),
                PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                // Health and readiness probes come from the orchestrator on a schedule and would
                // otherwise consume the node address's entire budget.
                if (httpContext.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                {
                    return RateLimitPartition.GetNoLimiter("probe");
                }

                IOptions<GatewayRateLimitOptions> options =
                    httpContext.RequestServices.GetRequiredService<IOptions<GatewayRateLimitOptions>>();
                GatewayRateLimitOptions limits = options.Value;

                return RateLimitPartition.GetSlidingWindowLimiter(
                    ClientPartition(httpContext),
                    _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = limits.PermitLimit,
                        Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                        SegmentsPerWindow = limits.SegmentsPerWindow,
                        QueueLimit = 0,
                    });
            }));

            limiter.AddPolicy(PerAddressPolicy, static httpContext =>
            {
                IOptions<GatewayRateLimitOptions> options =
                    httpContext.RequestServices.GetRequiredService<IOptions<GatewayRateLimitOptions>>();
                GatewayRateLimitOptions limits = options.Value;

                return RateLimitPartition.GetSlidingWindowLimiter(
                    ClientPartition(httpContext),
                    _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = limits.PermitLimit,
                        Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                        SegmentsPerWindow = limits.SegmentsPerWindow,
                        QueueLimit = 0,
                    });
            });

        });

        builder.Services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.ReportApiVersions = true;
            options.AssumeDefaultVersionWhenUnspecified = false;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        });

        builder.Services.AddOpenApi(DownloadGatewayOpenApi.Configure);
        builder.Services.AddOptions<Downloads.DownloadOptions>()
            .Bind(builder.Configuration.GetSection(Downloads.DownloadOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton<Downloads.DownloadMetrics>();
        builder.Services.AddScoped<StatementDelivery.ServiceDefaults.Auditing.RequestAudit>();

        builder.Services.AddValidatorsFromAssemblyContaining<GatewayRateLimitOptions>(includeInternalTypes: true);

        return builder;
    }

    /// <summary>
    /// The rate-limit partition key for a request.
    /// </summary>
    /// <remarks>
    /// A null remote address collapses to one shared partition rather than to a per-request one.
    /// The conservative direction matters: an unknown caller sharing a bucket is throttled, whereas
    /// an unknown caller getting a fresh bucket every request is not limited at all.
    /// </remarks>
    private static string ClientPartition(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-address";
}
