using Mono.Cecil;
using NetArchTest.Rules;
using Shouldly;
using StatementDelivery.Contracts;
using StatementDelivery.Messaging;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.ServiceDefaults;
using Xunit;

namespace ArchitectureTests;

/// <summary>
/// The layering rules, as executable tests.
/// </summary>
/// <remarks>
/// <para>
/// An architectural decision written only in a document is a decision that gets reversed by
/// somebody who never read it. Written as a failing build, it gets reversed only on purpose.
/// </para>
/// <para>
/// TODO(architecture): once a domain layer exists, add a test asserting that the domain project
/// references NO framework assemblies at all - not ASP.NET Core, not Npgsql, not the AWS SDK. That
/// rule cannot be written yet because there is no domain project to write it about, and a test
/// that vacuously passes over an empty set is worse than no test.
/// </para>
/// </remarks>
public sealed class LayeringTests
{
    private static readonly string[] AspNetCorePrefixes = ["Microsoft.AspNetCore"];

    [Fact]
    public void BuildingBlocks_ShouldNotDependOn_Services()
    {
        // Dependencies point inwards. A shared library that reaches back into a deployable makes
        // that deployable impossible to change without changing everything that shares the library
        // - and makes the two impossible to deploy independently, which was the entire reason for
        // splitting them.
        string[] serviceNames = [.. SolutionGraph.Services.Select(service => service.Name)];
        serviceNames.ShouldNotBeEmpty("the service projects must be discoverable for this rule to mean anything");

        foreach (ProjectNode buildingBlock in SolutionGraph.BuildingBlocks)
        {
            buildingBlock.ProjectReferences
                .Intersect(serviceNames, StringComparer.Ordinal)
                .ShouldBeEmpty($"{buildingBlock.Name} must not reference any service project");
        }
    }

    [Fact]
    public void Contracts_ShouldHaveNoProjectDependencies()
    {
        // Contracts is the one assembly every deployable may depend on, so anything added to it
        // becomes a dependency of the entire estate. It stays empty of dependencies on purpose.
        ProjectNode contracts = SolutionGraph.Projects.Single(project => project.Name == "Contracts");

        contracts.ProjectReferences.ShouldBeEmpty("Contracts must not reference any other project");
        contracts.PackageReferences.ShouldBeEmpty("Contracts must not reference any NuGet package");

        // And nothing in it may reach into another building block at the type level either.
        Types.InAssembly(typeof(IIntegrationEvent).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny("StatementDelivery.Persistence", "StatementDelivery.ServiceDefaults", "StatementDelivery.Messaging")
            .GetResult()
            .ShouldBeSuccessful();
    }

    [Fact]
    public void Services_ShouldNotReferenceEachOther()
    {
        // Four deployables that can be released independently. The moment one references another
        // they share a build, a version and a blast radius, and the microservice split has bought
        // nothing but the distributed-systems tax. Services talk over HTTP and the outbox, never
        // by linking.
        string[] serviceNames = [.. SolutionGraph.Services.Select(service => service.Name)];
        serviceNames.Length.ShouldBe(4, "there are exactly four deployable services");

        foreach (ProjectNode service in SolutionGraph.Services)
        {
            string[] others = [.. serviceNames.Where(name => !string.Equals(name, service.Name, StringComparison.Ordinal))];

            service.ProjectReferences
                .Intersect(others, StringComparer.Ordinal)
                .ShouldBeEmpty($"{service.Name} must not reference another service");
        }
    }

    [Fact]
    public void Persistence_ShouldNotDependOn_AspNetCore()
    {
        // Persistence is consumed by two web services, two workers AND the seed tool. Pulling the
        // web framework into it would make the worker images carry it for nothing, and would open
        // the door to HttpContext appearing in the data layer.
        IEnumerable<AssemblyNameReference> referenced = AssemblyReferences(typeof(IDbConnectionFactory).Assembly.Location);

        referenced
            .Where(reference => AspNetCorePrefixes.Any(prefix => reference.Name.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(reference => reference.Name)
            .ShouldBeEmpty("StatementDelivery.Persistence must not reference ASP.NET Core");

        Types.InAssembly(typeof(IDbConnectionFactory).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult()
            .ShouldBeSuccessful();
    }

    [Fact]
    public void NoProject_ShouldReference_EntityFrameworkCore()
    {
        // GUARDRAIL. See docs/adr/0002-dapper-and-dbup-over-ef-core.md. The schema uses PostgreSQL
        // features EF Core cannot express - RANGE partitioning, partial indexes, append-only
        // triggers, REVOKE grants - and the security-critical path is a hand-written atomic
        // UPDATE ... RETURNING. This is the decision most likely to be quietly reversed later, so
        // reversing it has to turn the build red.
        foreach (ProjectNode project in SolutionGraph.Projects)
        {
            project.PackageReferences
                .Where(package => package.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))
                .ShouldBeEmpty($"{project.Name} must not reference Entity Framework Core");
        }

        // Also checked against the central version file, so a pin added ahead of its first use is
        // caught too - that is exactly how this decision gets reversed by degrees.
        SolutionGraph.CentralPackageVersions
            .Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
            .ShouldBeFalse("Directory.Packages.props must not pin an Entity Framework Core version");
    }

    [Fact]
    public void NoProject_ShouldReference_FluentAssertions()
    {
        // GUARDRAIL. FluentAssertions v8+ moved to a commercial licence (roughly USD 130 per seat
        // for commercial use). Shipping it in a repository presented as production-grade is a
        // licensing problem, not a style one. Shouldly is the assertion library here;
        // AwesomeAssertions, the Apache-2.0 fork of v7, is the acceptable alternative.
        foreach (ProjectNode project in SolutionGraph.Projects)
        {
            project.PackageReferences
                .Where(package => package.Equals("FluentAssertions", StringComparison.OrdinalIgnoreCase))
                .ShouldBeEmpty($"{project.Name} must not reference FluentAssertions");
        }

        SolutionGraph.CentralPackageVersions
            .Contains("\"FluentAssertions\"", StringComparison.OrdinalIgnoreCase)
            .ShouldBeFalse("Directory.Packages.props must not pin a FluentAssertions version");
    }

    [Fact]
    public void ServiceDefaults_ShouldNotDependOn_Messaging()
    {
        // ServiceDefaults is cross-cutting infrastructure that every deployable takes. Messaging is
        // a choice about how services talk to each other. Keeping them apart means a service can
        // adopt the platform defaults without also adopting the outbox.
        ProjectNode serviceDefaults = SolutionGraph.Projects.Single(project => project.Name == "ServiceDefaults");

        serviceDefaults.ProjectReferences.ShouldNotContain("Messaging");

        Types.InAssembly(typeof(ServiceDefaultsExtensions).Assembly)
            .ShouldNot()
            .HaveDependencyOn(typeof(IIntegrationEventPublisher).Namespace)
            .GetResult()
            .ShouldBeSuccessful();
    }

    [Fact]
    public void EveryProject_ShouldUseCentralPackageManagement()
    {
        // A Version attribute on a PackageReference silently opts that one project out of central
        // management, which is how two projects end up on two versions of the same package and how
        // the vulnerability scan starts reporting something nobody can find.
        foreach (ProjectNode project in SolutionGraph.Projects)
        {
            string contents = File.ReadAllText(project.Path);

            System.Text.RegularExpressions.Regex
                .IsMatch(contents, @"<PackageReference[^>]*\sVersion\s*=", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2))
                .ShouldBeFalse($"{project.Name} must not pin a package version inline; every version lives in Directory.Packages.props");
        }
    }

    private static IEnumerable<AssemblyNameReference> AssemblyReferences(string assemblyPath)
    {
        using ModuleDefinition module = ModuleDefinition.ReadModule(assemblyPath);
        return [.. module.AssemblyReferences];
    }
}

/// <summary>
/// Shouldly helpers for NetArchTest results.
/// </summary>
internal static class TestResultExtensions
{
    /// <summary>
    /// Asserts a NetArchTest rule passed, naming the offending types when it did not.
    /// </summary>
    /// <param name="result">The rule result.</param>
    public static void ShouldBeSuccessful(this NetArchTest.Rules.TestResult result)
    {
        if (result.IsSuccessful)
        {
            return;
        }

        string offenders = string.Join(", ", result.FailingTypeNames ?? []);
        throw new ShouldAssertException($"Architecture rule failed. Offending types: {offenders}");
    }
}
