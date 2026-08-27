namespace SecurityTests;

/// <summary>
/// Locates repository files so that tests can assert against what is actually committed.
/// </summary>
/// <remarks>
/// Several controls in this system live in configuration rather than in code - a base image
/// choice, a gitignore entry, a pooling mode. Those are exactly the controls that get changed by
/// someone in a hurry, and asserting on the files is the only way to notice.
/// </remarks>
public static class RepositoryFiles
{
    private static readonly Lazy<string> RootValue = new(FindRoot);

    private static readonly string BinSegment =
        string.Concat(Path.DirectorySeparatorChar, "bin", Path.DirectorySeparatorChar);

    private static readonly string ObjSegment =
        string.Concat(Path.DirectorySeparatorChar, "obj", Path.DirectorySeparatorChar);

    /// <summary>Gets the repository root.</summary>
    public static string Root => RootValue.Value;

    /// <summary>Reads a repository-relative file.</summary>
    /// <param name="relativePath">Path relative to the repository root, using forward slashes.</param>
    /// <returns>The file contents.</returns>
    public static string Read(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>Enumerates repository files matching a pattern, skipping build output.</summary>
    /// <param name="pattern">A file-name pattern, for example <c>appsettings*.json</c>.</param>
    /// <returns>Absolute paths.</returns>
    public static IEnumerable<string> Find(string pattern) =>
        Directory.EnumerateFiles(Root, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains(BinSegment, StringComparison.Ordinal))
            .Where(path => !path.Contains(ObjSegment, StringComparison.Ordinal));

    private static string FindRoot()
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

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
