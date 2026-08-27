using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace ArchitectureTests;

/// <summary>
/// Every Dockerfile must copy the whole project closure it is about to restore.
/// </summary>
/// <remarks>
/// <para>
/// THE BUG THIS CLOSES, AND WHY NOTHING ELSE CAUGHT IT. Each service Dockerfile copies project
/// manifests first, restores, then copies sources - so a source-only change reuses the restore
/// layer. That optimisation depends on the copied list being the complete transitive closure of
/// what the restore touches.
/// </para>
/// <para>
/// Prompt 2 added <c>Domain</c>, which <c>Persistence</c> and <c>ServiceDefaults</c> both reference,
/// and no Dockerfile was updated. Every image build then failed with NETSDK1004 - assets file not
/// found - which means `docker compose up` from a clean clone, a stated acceptance criterion, had
/// been broken ever since. It was invisible locally because this host has no container runtime, and
/// invisible in CI because CI had never run.
/// </para>
/// <para>
/// This test reads the same files Docker does and needs neither a daemon nor a build, so the next
/// project that joins the graph fails here in seconds rather than in an image build nobody can run.
/// </para>
/// </remarks>
public sealed partial class DockerfileRestoreClosureTests
{
    public static TheoryData<string> Dockerfiles() =>
    [
        "src/Services/Delivery.Api/Dockerfile",
        "src/Services/Download.Gateway/Dockerfile",
        "src/Services/Generation.Worker/Dockerfile",
        "src/Services/Retention.Worker/Dockerfile",
        "src/Migrations/Db.Migrator/Dockerfile",
    ];

    [Theory]
    [MemberData(nameof(Dockerfiles))]
    public void EveryDockerfile_CopiesItsFullProjectClosure(string dockerfile)
    {
        string path = Path.Combine(SolutionGraph.RepositoryRoot, dockerfile.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"{dockerfile} must exist");

        string contents = File.ReadAllText(path);

        // What the image actually publishes, taken from the Dockerfile rather than assumed - so a
        // renamed project cannot leave this test asserting against something that no longer exists.
        Match published = PublishTarget().Match(contents);
        published.Success.ShouldBeTrue($"{dockerfile} must contain a `dotnet publish <project>.csproj`");

        string projectName = Path.GetFileNameWithoutExtension(published.Groups["project"].Value);

        ProjectNode root = SolutionGraph.Projects.SingleOrDefault(project =>
            string.Equals(project.Name, projectName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"{projectName} is not in the solution graph.");

        HashSet<string> copied = [.. CopiedProject().Matches(contents).Select(match => match.Groups["name"].Value)];

        foreach (string required in Closure(root))
        {
            copied.ShouldContain(
                required,
                $"{dockerfile} restores {projectName}, which reaches {required} - so "
                + $"{required}.csproj must be copied before the restore, or the publish fails with "
                + "NETSDK1004 and `docker compose up` is broken from a clean clone");
        }
    }

    /// <summary>The transitive project closure, including the root itself.</summary>
    private static HashSet<string> Closure(ProjectNode root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<ProjectNode>([root]);

        while (pending.Count > 0)
        {
            ProjectNode current = pending.Pop();

            if (!seen.Add(current.Name))
            {
                continue;
            }

            foreach (string reference in current.ProjectReferences)
            {
                ProjectNode? next = SolutionGraph.Projects
                    .SingleOrDefault(project => string.Equals(project.Name, reference, StringComparison.Ordinal));

                if (next is not null)
                {
                    pending.Push(next);
                }
            }
        }

        return seen;
    }

    [GeneratedRegex(@"dotnet publish\s+(?<project>[\w./-]+\.csproj)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PublishTarget();

    [GeneratedRegex(@"COPY\s+[\w./-]*?(?<name>[\w.]+)\.csproj\s", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CopiedProject();
}
