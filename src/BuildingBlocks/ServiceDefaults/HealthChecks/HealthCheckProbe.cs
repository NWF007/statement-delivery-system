using System.Globalization;
using System.Net;

namespace StatementDelivery.ServiceDefaults.HealthChecks;

/// <summary>
/// A self-probe that lets a chiseled container report its own health.
/// </summary>
/// <remarks>
/// <para>
/// Chiseled images have NO SHELL and NO CURL. That is the point of them: nothing to execute if
/// something does get remote code execution. The usual
/// <c>HEALTHCHECK CMD curl -f http://localhost:8080/health/live</c> simply cannot run, and adding
/// curl back would undo the reason for choosing the base image.
/// </para>
/// <para>
/// So the application probes itself. <c>dotnet Delivery.Api.dll --healthcheck</c> runs this,
/// performs one HTTP GET against the liveness endpoint and exits 0 or 1. No new binaries, no
/// package manager, no shell - the runtime that is already in the image is the whole health-check
/// client.
/// </para>
/// <para>
/// In Kubernetes none of this is needed: an <c>httpGet</c> probe is performed by the kubelet from
/// outside the container. This exists for Docker Compose, where the health check has to run as a
/// command inside the container.
/// </para>
/// </remarks>
public static class HealthCheckProbe
{
    /// <summary>The argument that switches a service into probe mode.</summary>
    public const string Argument = "--healthcheck";

    /// <summary>Environment variable overriding the probe URL.</summary>
    public const string UrlVariable = "HEALTHCHECK_URL";

    private const string DefaultUrl = "http://localhost:8080/health/live";

    /// <summary>
    /// Determines whether this process was started to probe rather than to serve.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <returns><see langword="true"/> when the process should probe and exit.</returns>
    public static bool IsProbe(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Array.Exists(args, argument => string.Equals(argument, Argument, StringComparison.Ordinal));
    }

    /// <summary>
    /// Performs one liveness request and returns a process exit code.
    /// </summary>
    /// <returns>0 when the endpoint reports healthy, 1 otherwise.</returns>
    public static async Task<int> RunAsync()
    {
        string url = Environment.GetEnvironmentVariable(UrlVariable) ?? DefaultUrl;

        // Short and fixed. A probe that can hang for the default 100 seconds is worse than no
        // probe: Docker would keep it in "starting" while the container is already unusable.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        try
        {
            using HttpResponseMessage response = await client.GetAsync(new Uri(url)).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                return 0;
            }

            await Console.Error.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"healthcheck: {url} returned {(int)response.StatusCode}")).ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            await Console.Error.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"healthcheck: {url} unreachable: {ex.Message}")).ConfigureAwait(false);
            return 1;
        }
    }
}
