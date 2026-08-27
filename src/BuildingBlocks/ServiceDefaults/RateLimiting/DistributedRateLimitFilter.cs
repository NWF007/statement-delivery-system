using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace StatementDelivery.ServiceDefaults.RateLimiting;

/// <summary>
/// Applies distributed, identity-scoped rate limits to one endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Complements, rather than replaces, the ASP.NET rate limiter each service configures. That one is
/// in-process and guards the SERVICE: a fixed or sliding window per node, sized to what one replica
/// can absorb. This one guards an IDENTITY - a customer, an address - and must therefore be counted
/// once across the whole fleet, or a caller simply spreads their attempts over the replicas.
/// </para>
/// <para>
/// Runs as an endpoint filter rather than middleware so the partition can be derived from route and
/// authentication data, which are not yet resolved when middleware runs.
/// </para>
/// </remarks>
public sealed class DistributedRateLimitFilter : IEndpointFilter
{
    private readonly IDistributedRateLimiter _limiter;
    private readonly IReadOnlyList<RateLimitRule> _rules;
    private readonly Func<HttpContext, string> _partition;

    /// <summary>Initialises a new instance of the <see cref="DistributedRateLimitFilter"/> class.</summary>
    /// <param name="limiter">The shared limiter.</param>
    /// <param name="rules">The rules to apply. All must pass.</param>
    /// <param name="partition">Derives the partition key from the request.</param>
    public DistributedRateLimitFilter(
        IDistributedRateLimiter limiter,
        IReadOnlyList<RateLimitRule> rules,
        Func<HttpContext, string> partition)
    {
        _limiter = limiter;
        _rules = rules;
        _partition = partition;
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        HttpContext http = context.HttpContext;

        RateLimitDecision decision = await _limiter
            .CheckAsync(_partition(http), _rules, http.RequestAborted)
            .ConfigureAwait(false);

        if (decision.Allowed)
        {
            return await next(context).ConfigureAwait(false);
        }

        // Retry-After on EVERY 429, without exception. A 429 with no Retry-After tells a client to
        // back off by an amount it has to guess, and the guess is usually "immediately" - which
        // turns a rate limit into a retry storm against the endpoint it was meant to protect.
        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(decision.RetryAfter.TotalSeconds))
            .ToString(CultureInfo.InvariantCulture);

        // The rule name is NOT returned. Which limit was hit is operator information; telling a
        // caller whether they tripped the per-minute or the per-hour budget hands an enumeration
        // script the shape of the control it is trying to stay under.
        return Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many requests",
            detail: "Too many requests. Retry after the interval in the Retry-After header.");
    }
}

/// <summary>
/// Route builder extensions for distributed rate limiting.
/// </summary>
public static class DistributedRateLimitExtensions
{
    /// <summary>
    /// Applies fleet-wide rate limits partitioned by the caller's authenticated subject.
    /// </summary>
    /// <param name="builder">The route builder.</param>
    /// <param name="rules">The rules to apply.</param>
    /// <returns>The builder, for chaining.</returns>
    public static TBuilder RequireDistributedRateLimitPerSubject<TBuilder>(
        this TBuilder builder,
        params RateLimitRule[] rules)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireDistributedRateLimit(rules, static http =>
            http.User.FindFirst("sub")?.Value
            ?? http.Connection.RemoteIpAddress?.ToString()
            ?? "unknown-subject");

    /// <summary>
    /// Applies fleet-wide rate limits partitioned by the caller's address.
    /// </summary>
    /// <param name="builder">The route builder.</param>
    /// <param name="rules">The rules to apply.</param>
    /// <returns>The builder, for chaining.</returns>
    public static TBuilder RequireDistributedRateLimitPerAddress<TBuilder>(
        this TBuilder builder,
        params RateLimitRule[] rules)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireDistributedRateLimit(rules, static http =>
            // A null address collapses to ONE shared partition, not a fresh one per request. The
            // conservative direction is the only safe one here: unknown callers sharing a bucket are
            // throttled together, whereas unknown callers each getting a new bucket are not limited.
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown-address");

    private static TBuilder RequireDistributedRateLimit<TBuilder>(
        this TBuilder builder,
        IReadOnlyList<RateLimitRule> rules,
        Func<HttpContext, string> partition)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(rules);

        builder.AddEndpointFilterFactory((factoryContext, next) =>
        {
            return async invocationContext =>
            {
                IDistributedRateLimiter limiter = invocationContext.HttpContext.RequestServices
                    .GetRequiredService<IDistributedRateLimiter>();

                var filter = new DistributedRateLimitFilter(limiter, rules, partition);
                return await filter.InvokeAsync(invocationContext, next).ConfigureAwait(false);
            };
        });

        return builder;
    }
}
