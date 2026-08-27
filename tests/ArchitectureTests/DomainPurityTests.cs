using Mono.Cecil;
using Shouldly;
using StatementDelivery.Domain.Statements;
using Xunit;

namespace ArchitectureTests;

/// <summary>
/// The domain layer references nothing. This is the test that keeps the architecture honest.
/// </summary>
/// <remarks>
/// <para>
/// Not purity for its own sake. The domain is the one layer whose rules must outlive every
/// framework decision around it, and every framework reference is a one-way door: once the model
/// knows about Npgsql, extracting it again is a rewrite rather than a refactor.
/// </para>
/// <para>
/// The failure this prevents is always small and always reasonable-looking. Somebody needs an
/// <c>ILogger</c> in an entity, or wants <c>[JsonPropertyName]</c> on a value object, or reaches
/// for <c>IOptions</c> to make a policy configurable. Each one is a single line, and each one moves
/// a decision that belongs to the business into a library that will be replaced.
/// </para>
/// </remarks>
public sealed class DomainPurityTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "Npgsql",
        "Dapper",
        "Microsoft.AspNetCore",
        "Microsoft.Extensions",
        "AWSSDK",
        "Amazon",
        "OpenTelemetry",
        "FluentValidation",
        "Polly",
        "StackExchange",
    ];

    private static ProjectNode DomainProject =>
        SolutionGraph.Projects.Single(project => project.Name == "Domain");

    private static string DomainAssemblyPath => typeof(Statement).Assembly.Location;

    private static IEnumerable<string> ReferencedAssemblies()
    {
        using ModuleDefinition module = ModuleDefinition.ReadModule(DomainAssemblyPath);
        return [.. module.AssemblyReferences.Select(reference => reference.Name)];
    }

    [Fact]
    public void Domain_ShouldHaveNoPackageDependencies()
    {
        // Zero, not "only safe ones". A permitted-list of harmless packages is how the first
        // genuinely harmful one gets in: it arrives as a small addition to an existing list.
        DomainProject.PackageReferences
            .ShouldBeEmpty("the domain must reference no NuGet package at all");

        DomainProject.ProjectReferences
            .ShouldBeEmpty("the domain must reference no other project");
    }

    [Fact]
    public void Domain_ShouldNotReference_Npgsql()
    {
        // A database type in the model means the model has a schema, and a schema change becomes a
        // domain change. It also makes every domain test need a database.
        ReferencedAssemblies()
            .Where(name => name.StartsWith("Npgsql", StringComparison.Ordinal)
                        || name.StartsWith("Dapper", StringComparison.Ordinal))
            .ShouldBeEmpty("the domain must not know how it is persisted");
    }

    [Fact]
    public void Domain_ShouldNotReference_AspNetCore()
    {
        // The domain does not know HTTP exists. That is why DomainException carries no status code:
        // the same rule violation is a 400 over HTTP and a retry in a batch worker, and only the
        // adapter knows which.
        ReferencedAssemblies()
            .Where(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ShouldBeEmpty("the domain must not know how it is invoked");
    }

    [Fact]
    public void Domain_ShouldNotReference_MicrosoftExtensions()
    {
        // The broadest rule and the one most often broken, because Microsoft.Extensions.* looks
        // like part of the platform. It is not: it is a composition-root concern. ILogger,
        // IOptions and IServiceCollection all belong to the host, not to the model.
        ReferencedAssemblies()
            .Where(name => name.StartsWith("Microsoft.Extensions", StringComparison.Ordinal))
            .ShouldBeEmpty("the domain must not depend on the hosting extensions");
    }

    [Fact]
    public void Domain_ShouldReferenceOnlyTheBaseClassLibrary()
    {
        // The catch-all. The four rules above name the libraries most likely to creep in; this one
        // catches the one nobody predicted.
        string[] offenders =
        [
            .. ReferencedAssemblies()
                .Where(name => ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))),
        ];

        offenders.ShouldBeEmpty($"the domain referenced: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void EveryOtherProject_MayDependOnDomain_ButDomainDependsOnNobody()
    {
        // Dependencies point inwards. Persistence and the services may reference Domain; nothing
        // in Domain may reference them. Checked on the project graph, so a reference the compiler
        // optimises away because nothing uses it yet is still caught.
        foreach (ProjectNode project in SolutionGraph.Projects.Where(p => p.Name != "Domain"))
        {
            DomainProject.ProjectReferences.ShouldNotContain(
                project.Name,
                $"Domain must not reference {project.Name}");
        }
    }
}
