using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace ArchitectureTests;

/// <summary>
/// The query-discipline rules from <c>src/BuildingBlocks/Persistence/README.md</c>, enforced.
/// </summary>
/// <remarks>
/// Each rule describes a query that works on a laptop against a thousand rows and falls over on a
/// table holding 2.5 billion. None of them fail loudly when broken - that is precisely why they are
/// tests rather than a document: the first symptom of an unbounded query is a production incident,
/// and by then it is in fifty call sites.
/// </remarks>
public sealed partial class QueryDisciplineTests
{
    private const string SelfFileName = "QueryDisciplineTests.cs";

    /// <summary>
    /// C# and SQL sources, with comment lines removed so that a rule can be DISCUSSED without
    /// tripping the test that enforces it.
    /// </summary>
    private static IEnumerable<(string Path, string Code)> Sources()
    {
        string root = SolutionGraph.RepositoryRoot;
        string bin = string.Concat(Path.DirectorySeparatorChar, "bin", Path.DirectorySeparatorChar);
        string obj = string.Concat(Path.DirectorySeparatorChar, "obj", Path.DirectorySeparatorChar);

        foreach (string path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                              || p.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                     .Where(p => !p.Contains(bin, StringComparison.Ordinal))
                     .Where(p => !p.Contains(obj, StringComparison.Ordinal))

                     // This file states the forbidden patterns in order to detect them, so it
                     // matches every rule it enforces. Excluded by name rather than by weakening
                     // the patterns - a rule loosened to stop a test tripping over itself is a
                     // rule with a hole in it.
                     .Where(p => !p.EndsWith(SelfFileName, StringComparison.Ordinal))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            string[] lines = File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n');

            string code = string.Join(
                '\n',
                lines.Where(line =>
                {
                    string trimmed = line.TrimStart();
                    return !trimmed.StartsWith("//", StringComparison.Ordinal)
                        && !trimmed.StartsWith("--", StringComparison.Ordinal)
                        && !trimmed.StartsWith("///", StringComparison.Ordinal)
                        && !trimmed.StartsWith('*')
                        && !trimmed.StartsWith(';');
                }));

            yield return (Path.GetRelativePath(root, path), code);
        }
    }

    [Fact]
    public void NoQuery_UsesSelectStar()
    {
        // Adding a column is a routine migration. With SELECT * it silently changes the shape of
        // every result set touching the table - more bytes on the wire, a broken positional
        // mapping, and a column that was never meant to leave the database turning up in a
        // projection nobody re-reviewed. In this system that could be an encrypted blob or a
        // token hash.
        var offenders = new List<string>();

        foreach ((string path, string code) in Sources())
        {
            if (SelectStar().IsMatch(code))
            {
                offenders.Add(path);
            }
        }

        offenders.ShouldBeEmpty("SELECT * is forbidden; list columns explicitly");
    }

    [Fact]
    public void NoQuery_UsesOffsetPagination()
    {
        // OFFSET on a live table with concurrent inserts produces duplicates and gaps, degrades
        // linearly, and defeats partition pruning. Cursor.cs is the supported alternative.
        var offenders = new List<string>();

        foreach ((string path, string code) in Sources())
        {
            if (OffsetPagination().IsMatch(code))
            {
                offenders.Add(path);
            }
        }

        offenders.ShouldBeEmpty("OFFSET pagination is forbidden; use the keyset Cursor");
    }

    [Fact]
    public void EveryCommandDefinition_SetsAnExplicitTimeout()
    {
        // A query with no timeout does not merely run slowly. It holds a pooled connection, and
        // behind PgBouncer that connection is one of a few dozen shared by the whole fleet, so one
        // unbounded query becomes everybody's outage.
        var offenders = new List<string>();

        foreach ((string path, string code) in Sources())
        {
            foreach (Match match in CommandDefinition().Matches(code))
            {
                string arguments = ArgumentSpan(code, match.Index + match.Length - 1);

                if (!arguments.Contains("commandTimeout", StringComparison.Ordinal))
                {
                    offenders.Add($"{path}: {Compact(arguments)}");
                }
            }
        }

        offenders.ShouldBeEmpty("every CommandDefinition must pass commandTimeout, sized for its ConnectionIntent");
    }

    [Fact]
    public void ThePersistenceRulesAreDocumentedWhereAnAuthorWillLookForThem()
    {
        // A rule enforced by a test but explained nowhere leaves the next author knowing only THAT
        // the build is red, not why. The reasoning lives next to the code it governs.
        string readme = File.ReadAllText(
            Path.Combine(SolutionGraph.RepositoryRoot, "src", "BuildingBlocks", "Persistence", "README.md"));

        foreach (string rule in (string[])["OFFSET", "SELECT *", "timeout", "LIST", "log_min_duration_statement"])
        {
            readme.ShouldContain(rule);
        }
    }

    /// <summary>
    /// Returns the argument text of a call, starting at its opening parenthesis, by balancing
    /// parentheses. A regex cannot do this: <c>new CommandDefinition(...)</c> spans several lines
    /// and contains nested calls.
    /// </summary>
    private static string ArgumentSpan(string code, int openParenIndex)
    {
        int depth = 0;

        for (int i = openParenIndex; i < code.Length; i++)
        {
            if (code[i] == '(')
            {
                depth++;
            }
            else if (code[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return code[openParenIndex..(i + 1)];
                }
            }
        }

        return code[openParenIndex..];
    }

    private static string Compact(string value) =>
        Whitespace().Replace(value, " ").Trim() is { Length: > 90 } long_
            ? long_[..90] + "..."
            : Whitespace().Replace(value, " ").Trim();

    [GeneratedRegex(@"SELECT\s+\*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SelectStar();

    [GeneratedRegex(@"\bOFFSET\s+[@:$\d]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex OffsetPagination();

    [GeneratedRegex(@"new CommandDefinition\s*\(", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CommandDefinition();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();
}
