using System.Xml.Linq;

namespace ArchitectureTests;

/// <summary>
/// The repository's project graph, read from the <c>.csproj</c> files themselves.
/// </summary>
/// <remarks>
/// Reading the project files rather than the compiled assemblies is deliberate. A reference that
/// the compiler optimises away because nothing happens to use it yet is still a reference: it is
/// in the restore graph, it ships in the image, and the day someone does use it the layering is
/// already broken with no commit to point at. This catches it while it is still one line in one
/// file.
/// </remarks>
public sealed record ProjectNode(string Name, string Path, IReadOnlyList<string> ProjectReferences, IReadOnlyList<string> PackageReferences);

/// <summary>
/// Loads and exposes the repository's project graph.
/// </summary>
public static class SolutionGraph
{
    private static readonly Lazy<string> RepositoryRootValue = new(FindRepositoryRoot);
    private static readonly Lazy<IReadOnlyList<ProjectNode>> ProjectsValue = new(LoadProjects);

    /// <summary>Gets the repository root directory.</summary>
    public static string RepositoryRoot => RepositoryRootValue.Value;

    /// <summary>Gets every project in the repository.</summary>
    public static IReadOnlyList<ProjectNode> Projects => ProjectsValue.Value;

    /// <summary>Gets the four deployable services.</summary>
    public static IEnumerable<ProjectNode> Services =>
        Projects.Where(project => project.Path.Replace('\\', '/').Contains("/src/Services/", StringComparison.Ordinal));

    /// <summary>Gets the shared building-block libraries.</summary>
    public static IEnumerable<ProjectNode> BuildingBlocks =>
        Projects.Where(project => project.Path.Replace('\\', '/').Contains("/src/BuildingBlocks/", StringComparison.Ordinal));

    /// <summary>Gets the central package version file contents.</summary>
    public static string CentralPackageVersions =>
        File.ReadAllText(Path.Combine(RepositoryRoot, "Directory.Packages.props"));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root by walking up from the test output directory looking for Directory.Packages.props.");
    }

    private static IReadOnlyList<ProjectNode> LoadProjects()
    {
        string root = RepositoryRoot;

        return
        [
            .. Directory
                .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                .Where(path => !path.Replace('\\', '/').Contains("/bin/", StringComparison.Ordinal))
                .Where(path => !path.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal))
                .Select(Load)
                .OrderBy(project => project.Name, StringComparer.Ordinal),
        ];
    }

    private static ProjectNode Load(string path)
    {
        XDocument document = XDocument.Load(path);

        List<string> projectReferences =
        [
            .. document.Descendants("ProjectReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', Path.DirectorySeparatorChar)))
                .OrderBy(name => name, StringComparer.Ordinal),
        ];

        List<string> packageReferences =
        [
            .. document.Descendants("PackageReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => include!)
                .OrderBy(name => name, StringComparer.Ordinal),
        ];

        return new ProjectNode(Path.GetFileNameWithoutExtension(path), path, projectReferences, packageReferences);
    }
}
