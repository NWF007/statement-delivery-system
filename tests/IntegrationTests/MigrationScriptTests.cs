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
                     "V013__customer_key_material",
                     "V014__customer_key_cohort_index",
                     "V015__statement_content_digest_required",
                     "V016__validate_deferred_constraints",
                     "V017__statement_runs",
                 ])
        {
            names.ShouldContain(name => name.Contains(expected, StringComparison.Ordinal));
        }

        names.Count.ShouldBe(17);
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
        // Rule 1 of the migrations README. A plain CREATE INDEX is safe only on a table this same
        // script created, because only then is the table provably empty. The moment an index is
        // added to a table that already holds rows, a plain CREATE INDEX blocks writes for the
        // whole build - and DbUp runs each script in a transaction, which CREATE INDEX
        // CONCURRENTLY cannot do, so it needs its own non-transactional script.
        foreach ((string name, string sql) in EmbeddedScripts())
        {
            string code = StripComments(sql);

            foreach (Match match in CreateIndex().Matches(code))
            {
                string table = match.Groups["table"].Value;
                code.ShouldContain(
                    $"CREATE TABLE IF NOT EXISTS {table}",
                    customMessage: $"{name} indexes {table} without creating it in the same script; use CREATE INDEX CONCURRENTLY in a script of its own");
            }
        }
    }

    [Fact]
    public void EveryAddedConstraint_IsNotValid()
    {
        // RULE 4 OF THE MIGRATIONS README, ENFORCED.
        //
        // A plain ADD CONSTRAINT validates existing rows in the same statement, holding ACCESS
        // EXCLUSIVE for the length of a full table scan. On `statement` that is 2.5 billion rows,
        // and the lock blocks every read and every write for the duration - a migration that looked
        // like three lines takes the platform down.
        //
        // The rule was documented from Prompt 1 and broken by the first script that added a
        // constraint to a table it did not create. Documented and unenforced is how that happens, so
        // it is a test now.
        foreach ((string name, string sql) in EmbeddedScripts())
        {
            string code = StripComments(sql);

            foreach (Match match in AddConstraint().Matches(code))
            {
                // Everything up to the statement terminator must carry NOT VALID.
                int start = match.Index;
                int end = code.IndexOf(';', start);
                string statement = end < 0 ? code[start..] : code[start..end];

                statement.Contains("NOT VALID", StringComparison.OrdinalIgnoreCase)
                    .ShouldBeTrue(
                        $"{name} adds {match.Groups["constraint"].Value} without NOT VALID; add it here and "
                        + "VALIDATE CONSTRAINT in a later script, which is where the transaction boundary is");
            }
        }
    }

    [Fact]
    public void EveryNotValidConstraint_IsValidatedByALaterScript()
    {
        // The other half. NOT VALID without a later VALIDATE leaves the constraint permanently
        // unproven against existing rows - enforced going forward, silently unverified behind. That
        // is a worse end state than the lock it was avoiding, because nothing ever surfaces it.
        List<(string Name, string Sql)> scripts = [.. EmbeddedScripts()];

        var added = new List<(string Script, string Constraint)>();
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, string sql) in scripts)
        {
            string code = StripComments(sql);

            foreach (Match match in AddConstraint().Matches(code))
            {
                added.Add((name, match.Groups["constraint"].Value));
            }

            foreach (Match match in ValidateConstraint().Matches(code))
            {
                _ = validated.Add(match.Groups["constraint"].Value);
            }
        }

        added.ShouldNotBeEmpty("this rule must have constraints to check, or it is passing over an empty set");

        foreach ((string script, string constraint) in added)
        {
            validated.ShouldContain(
                constraint,
                $"{script} adds {constraint} as NOT VALID but no script ever validates it");
        }
    }

    [Fact]
    public void ConcurrentIndexBuilds_LiveInNonTransactionalScripts()
    {
        // The other half of rule 1, and the half that fails at runtime rather than at review.
        // PostgreSQL rejects CREATE INDEX CONCURRENTLY inside a transaction outright, and the
        // migrator wraps every ordinary script in one - so a concurrent build in a normally-named
        // script does not merely run slowly, it fails on every environment it reaches. The
        // `.notx.sql` suffix is what routes a script to the non-transactional pass; see Program.cs.
        foreach ((string name, string sql) in EmbeddedScripts())
        {
            if (!StripComments(sql).Contains("CONCURRENTLY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            name.EndsWith(".notx.sql", StringComparison.Ordinal)
                .ShouldBeTrue($"{name} builds an index CONCURRENTLY but is not named *.notx.sql, so it would run inside a transaction and fail");
        }
    }

    [Fact]
    public void NonTransactionalScripts_AreOrderIndependent()
    {
        // The cost of the two-pass runner, asserted rather than merely documented. Non-transactional
        // scripts run AFTER every transactional one regardless of version number, so anything in
        // one that depends on ordering would work on an upgraded database and break on a fresh one.
        // Restricting them to index builds keeps that difference unobservable.
        foreach ((string name, string sql) in EmbeddedScripts())
        {
            if (!name.EndsWith(".notx.sql", StringComparison.Ordinal))
            {
                continue;
            }

            string code = StripComments(sql).Trim();

            foreach (string forbidden in (string[])["INSERT", "UPDATE", "DELETE", "ALTER TABLE", "CREATE TABLE", "GRANT", "REVOKE"])
            {
                code.Contains(forbidden, StringComparison.OrdinalIgnoreCase)
                    .ShouldBeFalse($"{name} runs in the order-independent pass and must contain nothing but an index build, but it contains {forbidden}");
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

    // CONCURRENTLY is excluded: a concurrent build is the SANCTIONED way to index a populated
    // table, so matching it here would flag the correct answer as the violation.
    [GeneratedRegex(@"ADD\s+CONSTRAINT\s+(?<constraint>\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AddConstraint();

    [GeneratedRegex(@"VALIDATE\s+CONSTRAINT\s+(?<constraint>\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ValidateConstraint();

    [GeneratedRegex(@"CREATE\s+INDEX\s+(?!CONCURRENTLY)(?:IF\s+NOT\s+EXISTS\s+)?\w+\s+ON\s+(?<table>\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CreateIndex();
}
