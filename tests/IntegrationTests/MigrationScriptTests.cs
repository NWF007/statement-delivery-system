using System.Reflection;
using System.Text.RegularExpressions;
using Db.Migrator;
using DbUp.Engine.Preprocessors;
using Shouldly;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// Static checks on the migration scripts. No database and no Docker required.
/// </summary>
/// <remarks>
/// These run everywhere, including on a machine with no container runtime, because the failures
/// they catch are authoring mistakes rather than behaviour: a script that does not substitute, a
/// dollar-quoted block DbUp will eat, or a rule from the migrations README quietly broken by the
/// next person to add a script.
/// </remarks>
public sealed partial class MigrationScriptTests
{
    private static readonly Dictionary<string, string> Variables = new(StringComparer.Ordinal)
    {
        ["appDeliveryPassword"] = "substituted-delivery",
        ["appDownloadPassword"] = "substituted-download",
        ["appGenerationPassword"] = "substituted-generation",
        ["appRetentionPassword"] = "substituted-retention",
        ["appMigratorPassword"] = "substituted-migrator",
    };

    private static IEnumerable<(string Name, string Sql)> EmbeddedScripts()
    {
        Assembly assembly = typeof(MigrationOptions).Assembly;

        foreach (string name in assembly.GetManifestResourceNames()
                     .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return (name, reader.ReadToEnd());
        }
    }

    private static string StripComments(string sql) =>
        string.Join(
            '\n',
            sql.ReplaceLineEndings("\n").Split('\n')
                .Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

    [Fact]
    public void EveryScript_IsEmbeddedInTheMigratorAssembly()
    {
        // Loose files next to the binary would mean the migrator's behaviour depends on the
        // filesystem it lands on. Embedding makes the image the unit of truth - so it matters that
        // the EmbeddedResource glob is actually matching.
        List<string> names = [.. EmbeddedScripts().Select(script => script.Name)];

        foreach (string expected in (string[])
                 [
                     "V001__roles_and_grants",
                     "V002__distributed_lease",
                     "V003__partition_helper_functions",
                     "V004__outbox",
                     "V005__customer_and_account",
                     "V006__statement",
                     "V007__audit_event",
                     "V008__legal_hold_and_customer_key",
                     "V009__grants",
                     "V010__download_token",
                     "V011__token_grants",
                     "V012__audit_verify_grant",
                 ])
        {
            names.ShouldContain(name => name.Contains(expected, StringComparison.Ordinal));
        }

        names.Count.ShouldBe(12);
    }

    [Fact]
    public void ResourceNames_SortIntoExecutionOrder()
    {
        // DbUp orders by name. If a script were ever named without a zero-padded number, V10 would
        // run before V2 and the schema would be built in the wrong order - once, silently, on a
        // fresh environment only.
        List<string> names = [.. EmbeddedScripts().Select(script => script.Name)];

        names.ShouldBe([.. names.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void EveryScript_SurvivesVariableSubstitution()
    {
        var preprocessor = new VariableSubstitutionPreprocessor(Variables);

        foreach ((string name, string sql) in EmbeddedScripts())
        {
            // Throwing here means a $token$ that is not a declared variable - almost always a
            // dollar-quoted block written with a named tag.
            string processed = Should.NotThrow(() => preprocessor.Process(sql), $"{name} must preprocess cleanly");

            DollarTag().Matches(StripComments(processed))
                .Select(match => match.Value)
                .ShouldBeEmpty($"{name} leaves an unsubstituted variable in executable SQL");
        }
    }

    [Fact]
    public void RoleScript_ActuallySubstitutesEveryPassword()
    {
        // The counterpart to the test above: substitution that silently matches nothing would also
        // "survive". V001 must genuinely receive all five role passwords.
        var preprocessor = new VariableSubstitutionPreprocessor(Variables);

        (string _, string sql) = EmbeddedScripts().Single(script => script.Name.Contains("V001", StringComparison.Ordinal));
        string processed = preprocessor.Process(sql);

        foreach (string expected in Variables.Values)
        {
            processed.ShouldContain(expected);
        }
    }

    [Fact]
    public void DollarQuotedBlocks_UseABareTag()
    {
        // THE RULE FROM src/Migrations/Db.Migrator/README.md, enforced.
        //
        // DbUp substitutes $name$ variables in these scripts, so a named dollar tag such as $do$
        // or $body$ looks exactly like a variable and is eaten - or, if no such variable exists,
        // throws at migration time. A bare $$ has no word characters and cannot match.
        //
        // Comments are exempt, and demonstrably so: the scripts DISCUSS $body$ in order to warn
        // about it, and DbUp does not substitute inside a -- comment.
        foreach ((string name, string sql) in EmbeddedScripts())
        {
            NamedDollarTag().Matches(StripComments(sql))
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .ShouldBeEmpty($"{name} uses a named dollar-quote tag; use a bare $$ instead");
        }
    }

    [Fact]
    public void NoScript_CreatesAnIndexWithoutConcurrently_OnAPopulatedTable()
    {
        // Rule 1 of the migrations README. It is safe today only because these tables are created
        // empty in the same script. The moment an index is added to a table that already holds
        // rows, a plain CREATE INDEX blocks writes for the whole build - and DbUp runs each script
        // in a transaction, which CREATE INDEX CONCURRENTLY cannot do, so it needs its own script.
        foreach ((string name, string sql) in EmbeddedScripts())
        {
            string code = StripComments(sql);

            foreach (Match match in CreateIndex().Matches(code))
            {
                // Every index here is created in the same script that creates its table, so the
                // table is provably empty. Flag anything that is not.
                string table = match.Groups["table"].Value;
                code.ShouldContain(
                    $"CREATE TABLE IF NOT EXISTS {table}",
                    customMessage: $"{name} indexes {table} without creating it in the same script; use CREATE INDEX CONCURRENTLY in a script of its own");
            }
        }
    }

    [Fact]
    public void SecurityDefinerFunctions_PinTheirSearchPath()
    {
        // A SECURITY DEFINER function without a pinned search_path can be redirected at a schema
        // the CALLER controls, which turns the definer's privileges into the caller's.
        foreach ((string name, string sql) in EmbeddedScripts())
        {
            string code = StripComments(sql);

            if (!code.Contains("SECURITY DEFINER", StringComparison.Ordinal))
            {
                continue;
            }

            code.ShouldContain(
                "SET search_path",
                customMessage: $"{name} declares SECURITY DEFINER without pinning search_path");
            code.ShouldContain(
                "OWNER TO app_migrator",
                customMessage: $"{name} must pin the function owner, or it would run as whoever applied the migration");
        }
    }

    [GeneratedRegex(@"\$[A-Za-z_][A-Za-z0-9_]*\$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DollarTag();

    [GeneratedRegex(@"\$[A-Za-z_][A-Za-z0-9_]*\$\s*(?:BEGIN|DECLARE|SELECT)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamedDollarTag();

    [GeneratedRegex(@"CREATE\s+INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?\w+\s+ON\s+(?<table>\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CreateIndex();
}
