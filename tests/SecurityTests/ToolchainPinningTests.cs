using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace SecurityTests;

/// <summary>
/// Supply-chain pinning: the toolchain and the base images must be reproducible, and must agree
/// with each other.
/// </summary>
public sealed partial class ToolchainPinningTests
{
    [Fact]
    public void GlobalJson_PinsAnSdkVersionAndRollForwardPolicy()
    {
        using JsonDocument document = JsonDocument.Parse(RepositoryFiles.Read("global.json"));
        JsonElement sdk = document.RootElement.GetProperty("sdk");

        string version = sdk.GetProperty("version").GetString().ShouldNotBeNull();
        version.ShouldMatch(@"^10\.0\.\d+$");
        sdk.GetProperty("rollForward").GetString().ShouldBe("latestPatch");
    }

    [Fact]
    public void GlobalJson_OptsIntoTheMicrosoftTestingPlatformRunner()
    {
        // Without this, `dotnet test` routes to VSTest, which Microsoft.Testing.Platform refuses to
        // support on the .NET 10 SDK. The failure mode is a build error from deep inside an MSBuild
        // targets file, which is not where anybody looks first.
        using JsonDocument document = JsonDocument.Parse(RepositoryFiles.Read("global.json"));

        document.RootElement.GetProperty("test").GetProperty("runner").GetString()
            .ShouldBe("Microsoft.Testing.Platform");
    }

    [Theory]
    [InlineData("src/Services/Delivery.Api/Dockerfile")]
    [InlineData("src/Services/Download.Gateway/Dockerfile")]
    [InlineData("src/Services/Generation.Worker/Dockerfile")]
    [InlineData("src/Services/Retention.Worker/Dockerfile")]
    [InlineData("src/Migrations/Db.Migrator/Dockerfile")]
    public void SdkImage_MatchesTheFeatureBandGlobalJsonPins(string dockerfile)
    {
        // THE TRAP THIS CLOSES. global.json pins a version with rollForward: latestPatch, which
        // rolls forward INSIDE a feature band (10.0.4xx) and never across one. A Dockerfile using
        // the moving `sdk:10.0` tag therefore works until the next feature band ships, at which
        // point every container build fails with "a compatible .NET SDK was not found" while local
        // builds keep working - the worst kind of breakage, because nothing in the repository
        // changed.
        using JsonDocument document = JsonDocument.Parse(RepositoryFiles.Read("global.json"));
        string pinned = document.RootElement.GetProperty("sdk").GetProperty("version").GetString().ShouldNotBeNull();

        Match match = SdkImageTag().Match(RepositoryFiles.Read(dockerfile));

        match.Success.ShouldBeTrue($"{dockerfile} must build on a .NET SDK image");

        string imageVersion = match.Groups["version"].Value;
        imageVersion.ShouldNotBe("10.0", $"{dockerfile} must not use the moving SDK tag");

        FeatureBand(imageVersion).ShouldBe(
            FeatureBand(pinned),
            $"{dockerfile} pins SDK {imageVersion} but global.json pins {pinned}; they must share a feature band");
    }

    [Theory]
    [InlineData("src/Services/Delivery.Api/Dockerfile")]
    [InlineData("src/Services/Download.Gateway/Dockerfile")]
    [InlineData("src/Services/Generation.Worker/Dockerfile")]
    [InlineData("src/Services/Retention.Worker/Dockerfile")]
    [InlineData("src/Migrations/Db.Migrator/Dockerfile")]
    public void RuntimeImage_TracksThePatchLine(string dockerfile)
    {
        // The opposite policy from the SDK image, deliberately. The runtime image SHIPS, so it
        // should pick up CVE fixes without a commit; and any 10.0.x runtime satisfies an app
        // targeting net10.0, so there is no band to keep in step.
        string contents = RepositoryFiles.Read(dockerfile);

        contents.ShouldMatch(@"FROM mcr\.microsoft\.com/dotnet/(aspnet|runtime):10\.0-noble-chiseled");
    }

    [Fact]
    public void ComposeFile_PinsEveryThirdPartyImageExactly()
    {
        // Third-party images can change behaviour between releases in ways a patch tag does not
        // signal. Microsoft's .NET runtime images are the deliberate exception above.
        string compose = RepositoryFiles.Read("docker-compose.yml");

        foreach (Match match in ImageReference().Matches(compose))
        {
            string image = match.Groups["image"].Value;

            image.ShouldContain(":", customMessage: $"{image} must carry an explicit tag, never an implicit :latest");
            image.ShouldNotEndWith(":latest", customMessage: $"{image} must not float on :latest");
        }
    }

    [Fact]
    public void Workflow_PinsEveryActionAndEveryImage()
    {
        // THE GAP THIS CLOSES, AND HOW IT WAS FOUND.
        //
        // ComposeFile_PinsEveryThirdPartyImageExactly enforced "no floating tags" against
        // docker-compose.yml and nothing else. The CI workflow ran `docker run aquasec/trivy:latest`
        // - a floating tag on a security scanner, mounted against the Docker socket - and no test
        // read that file, so the rule was enforced in one place and silently absent in the other.
        //
        // It surfaced as GHSA-69fq-xp46-6x23 on the first push: the Trivy supply chain had been
        // briefly compromised. A rule enforced in only one of the two files that need it is not
        // really enforced; this reads the workflow.
        string workflow = RepositoryFiles.Read(".github/workflows/ci.yml");

        // Comments stripped first, so the note explaining what used to be here does not trip the
        // rule it exists to describe.
        string executable = string.Join(
            '\n',
            workflow.Split('\n').Where(static line => !line.TrimStart().StartsWith('#')));

        // Matched on ":latest" ANYWHERE rather than by parsing `docker run` lines. The original
        // offender wrapped its command, putting the image on a different line from `docker run` - so
        // a rule keyed to that command would have read clean while the floating tag sat right there.
        foreach (Match match in FloatingTag().Matches(executable))
        {
            string reference = match.Value;

            RunnerLabels.ShouldContain(
                reference,
                $"{reference} floats on :latest; pin it, a security scanner most of all");
        }

        MatchCollection actions = WorkflowAction().Matches(workflow);
        actions.Count.ShouldBeGreaterThan(0, "the workflow must use actions for this rule to mean anything");

        foreach (Match match in actions)
        {
            string reference = match.Groups["ref"].Value;

            // A branch reference re-resolves on every run, so a compromise upstream reaches this
            // repository without a commit here. A tag or a SHA at least makes the change visible.
            reference.ShouldNotBe("main");
            reference.ShouldNotBe("master");
            reference.ShouldNotBe("latest");
        }

        // The specific advisory, asserted by version rather than by absence: anything below 0.35.0
        // is the compromised range, and a well-meaning downgrade would reintroduce it.
        if (workflow.Contains("aquasecurity/trivy-action@", StringComparison.Ordinal))
        {
            foreach (Match match in TrivyAction().Matches(workflow))
            {
                Version.Parse(match.Groups["version"].Value)
                    .ShouldBeGreaterThanOrEqualTo(
                        new Version(0, 35, 0),
                        "trivy-action below 0.35.0 is GHSA-69fq-xp46-6x23");
            }
        }
    }

    [Fact]
    public void CentralPackageManagement_IsEnabled_AndEveryVersionIsPinned()
    {
        string props = RepositoryFiles.Read("Directory.Packages.props");

        props.ShouldContain("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>");

        // No wildcards and no floating ranges: "no floating versions, no wildcards" is the rule,
        // and a range makes a build unreproducible in exactly the way a lock file exists to prevent.
        foreach (Match match in PackageVersion().Matches(props))
        {
            string version = match.Groups["version"].Value;
            version.ShouldNotContain("*");
            version.ShouldNotContain("[");
            version.ShouldNotContain(",");
        }
    }

    private static string FeatureBand(string version)
    {
        string[] parts = version.Split('.');
        int patch = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
        return $"{parts[0]}.{parts[1]}.{patch / 100}xx";
    }

    [GeneratedRegex(@"FROM mcr\.microsoft\.com/dotnet/sdk:(?<version>[^\s]+) AS build", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SdkImageTag();

    [GeneratedRegex(@"^\s+image:\s*(?<image>\S+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ImageReference();

    [GeneratedRegex(@"<PackageVersion\s+Include=""[^""]+""\s+Version=""(?<version>[^""]+)""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PackageVersion();

    /// <summary>Runner labels legitimately end in <c>-latest</c> and are not image references.</summary>
    private static readonly string[] RunnerLabels = ["ubuntu-latest", "windows-latest", "macos-latest"];

    [GeneratedRegex(@"[\w./-]+:latest|[\w-]+-latest", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex FloatingTag();

    [GeneratedRegex(@"uses:\s*[\w.-]+/[\w.-]+@(?<ref>[\w.-]+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex WorkflowAction();

    [GeneratedRegex(@"aquasecurity/trivy-action@v?(?<version>\d+\.\d+\.\d+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex TrivyAction();
}
