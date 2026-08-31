using System.Diagnostics;
using Shouldly;
using Xunit;

namespace UnitTests.Rendering;

/// <summary>
/// Byte determinism across a REAL process restart.
/// </summary>
/// <remarks>
/// The in-process double render in <see cref="StatementRenderingTests"/> cannot prove this:
/// statics, font caches, interned strings and JIT state all survive within one process and could
/// mask a drift that only appears across restarts - which is exactly the drift that matters,
/// because regeneration happens days or months after the original render, in a different process
/// on a different machine. So this launches the <c>renderhash</c> tool twice as separate child
/// processes and compares their output hashes.
/// </remarks>
public sealed class CrossProcessDeterminismTests
{
    [Fact]
    public async Task Render_AcrossProcessRestart_ProducesIdenticalBytes()
    {
        string first = await RunRenderHashAsync().ConfigureAwait(true);
        string second = await RunRenderHashAsync().ConfigureAwait(true);

        first.Length.ShouldBe(64, $"expected a SHA-256 hex digest, got: {first}");
        second.ShouldBe(first, "the same document must render to identical bytes across process restarts");
    }

    private static async Task<string> RunRenderHashAsync()
    {
        // The tool is a ProjectReference, so its binary sits beside this test assembly.
        string toolPath = Path.Combine(AppContext.BaseDirectory, "renderhash.dll");
        File.Exists(toolPath).ShouldBeTrue($"renderhash.dll not found at {toolPath}");

        var startInfo = new ProcessStartInfo("dotnet", $"\"{toolPath}\" hash 100")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start renderhash.");

        string output = (await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false)).Trim();
        string errors = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        process.ExitCode.ShouldBe(0, $"renderhash failed: {errors}");
        return output;
    }
}
