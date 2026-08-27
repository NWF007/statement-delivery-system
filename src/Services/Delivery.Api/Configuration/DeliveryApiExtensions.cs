using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Threading.RateLimiting;
using Asp.Versioning;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using StatementDelivery.ServiceDefaults.RateLimiting;

namespace Delivery.Api.Configuration;

/// <summary>
/// Rate limiting configuration for the authenticated API.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "RateLimiting";

    /// <summary>Gets or sets the number of requests permitted per partition per window.</summary>
    [Range(1, 1_000_000)]
    public int PermitLimit { get; set; } = 120;

    /// <summary>Gets or sets the window length, in seconds.</summary>
    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets how many requests may wait rather than being rejected outright.
    /// </summary>
    /// <remarks>
    /// Zero on purpose. Queueing under a limiter converts a fast 429 - which a client can back off
    /// from - into a slow response that occupies a connection and a thread. On a customer-facing
    /// API, rejecting immediately is the kinder failure.
    /// </remarks>
    [Range(0, 10_000)]
    public int QueueLimit { get; set; }
}

/// <summary>
/// Service-specific wiring for the authenticated delivery API.
/// </summary>
public static class DeliveryApiExtensions
{
    /// <summary>
    /// The per-caller rate limiting policy. Partitioned by authenticated subject where there is
    /// one, and by remote address otherwise.
    /// </summary>
    public const string PerCallerPolicy = "per-caller";

    /// <summary>
    /// The authorization policy for operator-facing endpoints.
    /// </summary>
    /// <remarks>
    /// A dedicated scope rather than "any authenticated caller", because on this platform every
    /// authenticated caller is a CUSTOMER. Without it, /v1/audit/verify would be readable by anyone
    /// holding a valid bearer token, which is everyone the system exists to serve.
    /// </remarks>
    public const string StaffPolicy = "staff";

    /// <summary>The scope value <see cref="StaffPolicy"/> requires.</summary>
    public const string StaffScope = "audit.verify";

    // =========================================================================================
    //  WHAT EACH LIMIT IS ACTUALLY FOR
    //
    //  | Surface       | Partitioned by | Budget           | Threat it addresses                  |
    //  |---------------|----------------|------------------|--------------------------------------|
    //  | Issue         | customer       | 10/min, 100/hour | A compromised session mass-generating |
    //  |               |                |                  | links to exfiltrate a whole history   |
    //  | Redeem        | IP ADDRESS     | 30/min           | Token guessing and enumeration - the  |
    //  |               |                |                  | real control (Download.Gateway)       |
    //  | Redeem global | endpoint       | configurable     | Service-level DoS (Download.Gateway)  |
    //
    //  ⚠  DO NOT ADD A PER-TOKEN ATTEMPT LIMIT AND IMAGINE IT HELPS AGAINST GUESSING.
    //
    //  It looks protective, which is exactly why it is worth stating that it is not. An attacker
    //  guessing tokens presents a DIFFERENT value on every attempt, so it hashes to a different key
    //  every time and a per-token counter never reaches two. A million guesses would leave a million
    //  counters each sitting at one, and every single one of them under the limit.
    //
    //  Per-token limiting only constrains an attacker who already HOLDS a valid token - which is the
    //  case where they have already won. Per-IP is the control that bites, because the one thing an
    //  enumeration script cannot avoid is making its attempts from somewhere.
    // =========================================================================================

    /// <summary>
    /// The per-customer budget for issuing links, counted across the whole fleet.
    /// </summary>
    /// <remarks>
    /// Two windows, not one. 10/minute alone would let a script issue 14,400 links a day at a rate
    /// that never trips it; 100/hour alone would let it burn the whole hour's budget in two seconds.
    /// Together they bound both the burst and the sustained total.
    /// </remarks>
    public static readonly RateLimitRule[] IssueLinkRules =
    [
        new("issue-link-minute", Limit: 10, TimeSpan.FromMinutes(1)),
        new("issue-link-hour", Limit: 100, TimeSpan.FromHours(1)),
    ];

    /// <summary>
    /// Adds JWT bearer authentication, rate limiting, API versioning, OpenAPI and validation.
    /// </summary>
    /// <param name="builder">The web application builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static WebApplicationBuilder AddDeliveryApi(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<JwtOptions>()
            .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();

        builder.Services.AddOptions<RateLimitOptions>()
            .Bind(builder.Configuration.GetSection(RateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddOptions<Downloads.DownloadLinkOptions>()
            .Bind(builder.Configuration.GetSection(Downloads.DownloadLinkOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.AddAuthentication();
        builder.AddRateLimiting();
        builder.AddVersioningAndOpenApi();

        // No validators exist yet - there is nothing to validate until the first request contract
        // lands. Registering the assembly scan now means the first one is discovered rather than
        // needing this line to be remembered along with it.
        builder.Services.AddValidatorsFromAssemblyContaining<JwtOptions>(includeInternalTypes: true);

        // Scoped: it captures nothing per-request itself, but it is the natural lifetime for
        // something that records one request, and it keeps the door open for per-request context
        // (correlation id, tenant) without a lifetime change later.
        builder.Services.AddScoped<StatementDelivery.ServiceDefaults.Auditing.RequestAudit>();

        return builder;
    }

    private static void AddAuthentication(this WebApplicationBuilder builder)
    {
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                JwtOptions jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

                options.Authority = jwt.Authority;
                options.Audience = jwt.Audience;
                options.RequireHttpsMetadata = jwt.RequireHttpsMetadata;
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(jwt.ClockSkewSeconds),

                    // Pin the algorithm. Without this, a token presented with "alg":"none" or a
                    // symmetric algorithm against an asymmetric key is a classic confusion attack.
                    ValidAlgorithms = string.IsNullOrWhiteSpace(jwt.DevelopmentSigningKey)
                        ? [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256]
                        : [SecurityAlgorithms.HmacSha256],
                };

                if (!string.IsNullOrWhiteSpace(jwt.DevelopmentSigningKey))
                {
                    // Development only. JwtOptionsValidator fails startup if this is set anywhere else.
                    options.TokenValidationParameters.IssuerSigningKey =
                        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.DevelopmentSigningKey));
                }
            });

        builder.Services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            options.AddPolicy(StaffPolicy, policy => policy
                .RequireAuthenticatedUser()

                // Space-delimited, per RFC 8693: `scope` is one claim holding many values, so a
                // RequireClaim with an exact value would reject "statements.read audit.verify" -
                // which is what a real staff token looks like.
                .RequireAssertion(static context =>
                    context.User.FindAll("scope")
                        .Any(claim => claim.Value
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Contains(StaffScope, StringComparer.Ordinal))));
        });
    }

    private static void AddRateLimiting(this WebApplicationBuilder builder)
    {
        builder.Services.AddRateLimiter(limiter =>
        {
            // 429, not 503. A client that sees 503 retries assuming the server is broken; one that
            // sees 429 with Retry-After backs off, which is the behaviour the limit exists to cause.
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

            limiter.AddPolicy(PerCallerPolicy, static httpContext =>
            {
                IOptions<RateLimitOptions> options =
                    httpContext.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>();
                RateLimitOptions limits = options.Value;

                // Partition by authenticated subject first. Partitioning an authenticated API by IP
                // alone punishes every customer behind one corporate NAT for the behaviour of one.
                string partition = httpContext.User.FindFirst("sub")?.Value
                    ?? httpContext.Connection.RemoteIpAddress?.ToString()
                    ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.PermitLimit,
                    Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                    QueueLimit = limits.QueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });
            });
        });
    }

    private static void AddVersioningAndOpenApi(this WebApplicationBuilder builder)
    {
        builder.Services
            .AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.ReportApiVersions = true;

                // Deliberately false. An unversioned request is a client that has not decided which
                // contract it wants; answering it with "whatever is current" is how a client breaks
                // on a deploy it did not ask to be part of.
                options.AssumeDefaultVersionWhenUnspecified = false;

                options.ApiVersionReader = ApiVersionReader.Combine(
                    new UrlSegmentApiVersionReader(),
                    new HeaderApiVersionReader("X-Api-Version"));
            });

        builder.Services.AddOpenApi();
    }
}
