using System.Diagnostics;

namespace IntegrationTests;

/// <summary>
/// Detects whether a usable Docker daemon is present.
/// </summary>
/// <remarks>
/// Integration tests here run against real containers, so on a machine without Docker they must
/// SKIP rather than fail. A red build that means "your laptop has no Docker" trains people to
/// ignore red builds, which costs far more than the coverage gained.
/// </remarks>
public static class DockerAvailability
{
    /// <summary>
    /// Gets a value indicating whether Docker is usable. Referenced by
    /// <c>[Fact(SkipUnless = ...)]</c> so skipped tests are reported as skipped, with a reason,
    /// rather than quietly not existing.
    /// </summary>
    public static bool IsAvailable { get; } = Probe();

    /// <summary>The reason shown when tests are skipped.</summary>
    public const string SkipReason =
        "Requires a running Docker daemon. Set SKIP_DOCKER_TESTS=false and start Docker to run these.";

    private static bool Probe()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SKIP_DOCKER_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(20_000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
