using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace SecurityTests;

/// <summary>
/// Security controls that live in configuration rather than in code.
/// </summary>
/// <remarks>
/// <para>
/// The brief describes this project as empty and wired into CI. It is not empty, for two reasons.
/// A test project with no tests makes <c>dotnet test</c> exit non-zero on the .NET 10 testing
/// platform ("Zero tests ran"), so an empty project is a permanently red gate. And every control
/// asserted here already exists today - none of them are aspirational.
/// </para>
/// <para>
/// What is NOT here: anything about tokens, encryption, key management or the audit trail. None
/// of that exists yet, and a security test that passes because the feature is absent is worse
/// than no test - it reads as coverage. See docs/THREAT-MODEL.md for the deferred list.
/// </para>
/// </remarks>
public sealed partial class SupplyChainAndConfigurationTests
{
    [Fact]
    public void AppSettings_ContainNoSecrets()
    {
        // Configuration that ships in the image must be inert. Every credential arrives from the
        // environment, so a leaked repository is not also a leaked database.
        var offenders = new List<string>();

        foreach (string path in RepositoryFiles.Find("appsettings*.json"))
        {
            string contents = File.ReadAllText(path);

            if (SecretLikeKey().IsMatch(contents))
            {
                offenders.Add(Path.GetRelativePath(RepositoryFiles.Root, path));
            }
        }

        offenders.ShouldBeEmpty(
            "no appsettings file may contain a password, connection string, signing key or access key");
    }

    [Fact]
    public void EnvFile_IsIgnored_AndTheExampleIsCommitted()
    {
        string gitignore = RepositoryFiles.Read(".gitignore");

        gitignore.ShouldContain(".env");
        gitignore.ShouldContain("!.env.example", customMessage: "the example must be exempted from the ignore rule");

        File.Exists(Path.Combine(RepositoryFiles.Root, ".env.example"))
            .ShouldBeTrue(".env.example must be committed so a clean clone can start");
    }

    [Fact]
    public void ComposeFile_TakesEveryCredentialFromTheEnvironment()
    {
        // A password written into docker-compose.yml is a password in git history forever, and the
        // history is the part you cannot rotate.
        string compose = RepositoryFiles.Read("docker-compose.yml");

        foreach (Match match in HardCodedPassword().Matches(compose))
        {
            match.Groups["value"].Value
                .ShouldStartWith("${", customMessage: $"credential must be interpolated from .env, found: {match.Value.Trim()}");
        }
    }

    [Theory]
    [InlineData("src/Services/Delivery.Api/Dockerfile")]
    [InlineData("src/Services/Download.Gateway/Dockerfile")]
    [InlineData("src/Services/Generation.Worker/Dockerfile")]
    [InlineData("src/Services/Retention.Worker/Dockerfile")]
    [InlineData("src/Migrations/Db.Migrator/Dockerfile")]
    [InlineData("tools/seed/Dockerfile")]
    public void EveryImage_IsChiseled_AndRunsAsNonRoot(string dockerfile)
    {
        string contents = RepositoryFiles.Read(dockerfile);

        // Chiseled: no shell, no package manager, no busybox. If something achieves remote code
        // execution there is nothing in the image to execute.
        contents.ShouldContain("-chiseled", customMessage: $"{dockerfile} must use a chiseled runtime base image");

        // $APP_UID is baked into the Microsoft base images (uid 1654). A container running as root
        // turns a container escape into a host compromise.
        contents.ShouldContain("USER $APP_UID", customMessage: $"{dockerfile} must drop to the non-root application user");

        contents.ShouldNotContain("USER root");
    }

    [Theory]
    [InlineData("src/Services/Delivery.Api/Dockerfile")]
    [InlineData("src/Services/Download.Gateway/Dockerfile")]
    [InlineData("src/Services/Generation.Worker/Dockerfile")]
    [InlineData("src/Services/Retention.Worker/Dockerfile")]
    [InlineData("src/Migrations/Db.Migrator/Dockerfile")]
    [InlineData("tools/seed/Dockerfile")]
    public void RuntimeStages_DoNotReintroduceAShellOrPackageManager(string dockerfile)
    {
        // The single most common way a chiseled image stops being chiseled is somebody adding curl
        // back so the container health check works. The health check here is the application
        // probing itself instead - see HealthCheckProbe.
        //
        // Comments are stripped first: these Dockerfiles DISCUSS curl at length, explaining why it
        // is absent, and a test that cannot tell an instruction from an explanation would push the
        // next author to delete the explanation.
        string instructions = string.Join(
            '\n',
            RepositoryFiles.Read(dockerfile)
                .ReplaceLineEndings("\n")
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith('#')));

        foreach (string forbidden in (string[])["apt-get", "apk add", "yum install", "curl", "wget"])
        {
            instructions.ShouldNotContain(
                forbidden,
                customMessage: $"{dockerfile} must not reintroduce {forbidden} into the image");
        }
    }

    [Fact]
    public void ServiceImages_ExposeOnlyTheUnprivilegedPort()
    {
        foreach (string dockerfile in (string[])
        [
            "src/Services/Delivery.Api/Dockerfile",
            "src/Services/Download.Gateway/Dockerfile",
            "src/Services/Generation.Worker/Dockerfile",
            "src/Services/Retention.Worker/Dockerfile",
        ])
        {
            string contents = RepositoryFiles.Read(dockerfile);

            // 8080, not 80. A non-root process cannot bind a privileged port, so exposing 80 would
            // mean either running as root or an image that cannot start.
            contents.ShouldContain("EXPOSE 8080");
            contents.ShouldNotContain("EXPOSE 80\n");
        }
    }

    [Fact]
    public void PgBouncer_RunsInTransactionMode_WithScramAuthentication()
    {
        string ini = RepositoryFiles.Read("deploy/pgbouncer/pgbouncer.ini");

        // Session pooling would remove the traps documented in NpgsqlConnectionFactory and also
        // remove the entire reason PgBouncer is here - see ADR-0008.
        ini.ShouldContain("pool_mode = transaction");

        // md5 is deprecated and offline-crackable.
        ini.ShouldContain("auth_type = scram-sha-256");
        ini.ShouldNotContain("auth_type = trust");
        ini.ShouldNotContain("auth_type = md5");

        // Above zero, which is what makes Npgsql auto-prepare safe behind a transaction pooler.
        ini.ShouldMatch(@"max_prepared_statements\s*=\s*[1-9]");
    }

    [Fact]
    public void PgBouncer_PartitionsPoolsPerWorkload()
    {
        // The month-end failure this exists to prevent: 400 render workers exhaust a shared pool
        // and the customer-facing API starts timing out. Separate aliases with separate pool_size
        // make that impossible.
        string ini = RepositoryFiles.Read("deploy/pgbouncer/pgbouncer.ini");

        foreach (string workload in (string[])["statements_delivery", "statements_download", "statements_generation", "statements_retention"])
        {
            ini.ShouldContain(workload);
        }

        ini.ShouldNotContain("\"userlist.txt\"", customMessage: "the auth file is generated from .env, never committed");
        File.Exists(Path.Combine(RepositoryFiles.Root, "deploy", "pgbouncer", "userlist.txt"))
            .ShouldBeFalse("a committed userlist.txt would be committed plaintext passwords");
    }

    [Fact]
    public void Services_ReachPostgresOnlyThroughPgBouncer()
    {
        // Only db-migrator may connect to 5432. A service that bypassed the pooler would open a
        // backend per client connection, which is the failure the whole persistence design is
        // built around avoiding.
        string compose = RepositoryFiles.Read("docker-compose.yml");

        foreach (Match match in ServiceConnectionString().Matches(compose))
        {
            string value = match.Groups["value"].Value;
            value.ShouldContain("Host=pgbouncer", customMessage: $"service connection strings must target PgBouncer: {value}");
            value.ShouldContain("Port=6432");
        }

        compose.ShouldContain(
            "Migration__ConnectionString: Host=postgres;Port=5432",
            customMessage: "the migrator is the one component that connects directly, because DDL needs a session it owns");
    }

    [GeneratedRegex(
        @"""(?i:password|pwd|secret|signingkey|accesskey|secretkey|connectionstring)""\s*:\s*""[^""]+""",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex SecretLikeKey();

    [GeneratedRegex(
        @"(?i:password|secret|signingkey|_key)\s*:\s*(?<value>\S+)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex HardCodedPassword();

    [GeneratedRegex(
        @"Postgres__PrimaryConnectionString:\s*(?<value>.+)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex ServiceConnectionString();
}
