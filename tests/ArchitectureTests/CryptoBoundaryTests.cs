using Mono.Cecil;
using Shouldly;
using Xunit;

namespace ArchitectureTests;

/// <summary>
/// The boundaries that keep plaintext key material inside one assembly.
/// </summary>
/// <remarks>
/// <para>
/// The type system cannot help here the way it does for <c>TokenSecret</c>. A download token is a
/// <c>ref struct</c>, so the compiler forbids storing one; a <c>DataKey</c> is a class, because it
/// has to be disposable and has to be handed to an <c>AesGcm</c> instance that outlives a single
/// stack frame. Nothing stops a service from taking one as a dependency and putting it in a field.
/// </para>
/// <para>
/// So the rule is enforced here instead, against the compiled IL - which catches a reference that
/// exists and is never executed, and which is the shape this mistake actually takes: not a service
/// that logs a key, but a service that accepts one because it was convenient, and then logs it two
/// years later.
/// </para>
/// </remarks>
public sealed class CryptoBoundaryTests
{
    /// <summary>
    /// The only assemblies permitted to touch plaintext key material, and why each one is.
    /// </summary>
    /// <remarks>
    /// A DENY-LIST WOULD BE THE WRONG SHAPE HERE, and it was: the first version of this rule listed
    /// the five assemblies that must NOT reference DataKey, which meant its coverage was defined by
    /// an array somebody had to remember to extend. It silently omitted ServiceDefaults - which does
    /// reference DataKey - and both workers, one of which is the service Prompt 5 will build around
    /// the key hierarchy. A rule that cannot fire for the project most likely to break it is not a
    /// rule.
    ///
    /// Inverted, it is an ALLOW-LIST checked against every assembly actually present in the build
    /// output, so a new project is covered the day it exists rather than the day someone remembers.
    /// </remarks>
    private static readonly Dictionary<string, string> KeyMaterialExemptions = new(StringComparer.Ordinal)
    {
        ["StatementDelivery.Crypto"] =
            "owns the type: mints, uses and wipes key material inside a using block",
        ["StatementDelivery.ServiceDefaults"] =
            "hosts the encrypting S3 adapter, which must unwrap a DEK to decrypt an object",
    };

    [Fact]
    public void NoProjectOutsideCrypto_ShouldReference_DataKey()
    {
        // DataKey holds PLAINTEXT key material. Every use of one belongs inside the crypto building
        // block - or, for exactly one adapter, in ServiceDefaults, which is listed above WITH ITS
        // REASON so the exemption is reviewed rather than assumed.
        //
        // Persistence is deliberately NOT exempt: it supplies the customer_key adapter and handles
        // WRAPPED bytes only. If it ever appears here, key material has reached the layer that talks
        // to SQL parameters.
        List<string> offenders = [];
        int inspected = 0;

        foreach (string assembly in SolutionAssemblies())
        {
            if (KeyMaterialExemptions.ContainsKey(assembly))
            {
                continue;
            }

            inspected++;

            if (ReferencedNames(assembly).Any(static name => name.Contains("Crypto.Keys.DataKey", StringComparison.Ordinal)))
            {
                offenders.Add(assembly);
            }
        }

        inspected.ShouldBeGreaterThan(8, "the build output must contain the solution's assemblies for this rule to mean anything");

        offenders.ShouldBeEmpty(
            "plaintext key material stays inside StatementDelivery.Crypto. If one of these genuinely "
            + "needs it, add it to KeyMaterialExemptions WITH A REASON rather than deleting this test");
    }

    [Fact]
    public void EveryKeyMaterialExemption_IsStillNeeded()
    {
        // The other half of an allow-list: an exemption nobody needs any more is an exemption nobody
        // notices being used again. If ServiceDefaults stops unwrapping DEKs, this fails and the
        // entry gets removed rather than quietly widening the rule forever.
        foreach ((string assembly, string reason) in KeyMaterialExemptions)
        {
            reason.ShouldNotBeNullOrWhiteSpace();

            ReferencedNames(assembly)
                .ShouldContain(
                    name => name.Contains("Crypto.Keys.DataKey", StringComparison.Ordinal),
                    $"{assembly} is exempt from the DataKey rule ({reason}) but no longer references it - remove the exemption");
        }
    }

    [Fact]
    public void Persistence_ShouldHandleWrappedKeysOnly()
    {
        // The counterpart to the rule above, and the reason Persistence is exempt from it: the
        // adapter reads and writes a BYTEA column holding a wrapped CEK. It must never touch the
        // plaintext, which would mean a key existing on the same code path as a SQL parameter.
        IReadOnlyList<string> references = ReferencedNames("StatementDelivery.Persistence");

        references
            .Where(static name => name.Contains("Crypto.Keys.DataKey", StringComparison.Ordinal))
            .ShouldBeEmpty("Persistence must not reference DataKey - it stores wrapped bytes, never plaintext keys");

        // And it must reference the record it persists, or this rule is inspecting the wrong file.
        references.ShouldContain(
            static name => name.Contains("CustomerKeyRecord", StringComparison.Ordinal),
            "Persistence should reference CustomerKeyRecord - otherwise this test is inspecting the wrong assembly");
    }

    [Fact]
    public void DownloadGateway_ShouldNotReference_IKeyProvider()
    {
        // THE GATEWAY GOES THROUGH THE PORT AND NOTHING ELSE. It asks IStatementContentStore for
        // bytes at a location; whether those bytes were encrypted, which key opened them and where
        // that key came from are all the adapter's business.
        //
        // If this rule ever fails, the port was the wrong shape - the gateway will have reached
        // around it for something the interface did not provide, and the whole claim that the
        // encryption swap required no logic change will have stopped being true.
        IReadOnlyList<string> references = ReferencedNames("Download.Gateway");

        references.ShouldNotBeEmpty("the Download.Gateway assembly must be readable for this rule to mean anything");

        string[] forbidden =
        [
            "IKeyProvider",
            "IDataKeyBroker",
            "ICustomerKeyService",
            "DataKey",
            "KeyWrap",
            "CohortAssignment",
        ];

        foreach (string type in forbidden)
        {
            references
                .Where(name => name.Contains("Crypto.Keys." + type, StringComparison.Ordinal))
                .ShouldBeEmpty($"Download.Gateway must not reference {type} - it reads bytes through IStatementContentStore and knows nothing about keys");
        }

        // It legitimately references the integrity exception: a decryption failure has to be caught
        // somewhere, and the gateway is where the response is decided. That is the port's error
        // contract, not a key-handling concern.
        references.ShouldContain(
            static name => name.Contains("CiphertextIntegrityException", StringComparison.Ordinal),
            "Download.Gateway should handle CiphertextIntegrityException - a decryption failure must not become a 500");
    }

    [Fact]
    public void DeliveryApi_ShouldNotReference_AnyCryptoType()
    {
        // The catalogue and link-issue service decrypts nothing, so it needs none of this. The
        // database agrees: V013 revokes its table-level SELECT on customer_key so it cannot read
        // wrapped key material even if some future code tried.
        IReadOnlyList<string> references = ReferencedNames("Delivery.Api");

        references.ShouldNotBeEmpty("the Delivery.Api assembly must be readable for this rule to mean anything");

        references
            .Where(static name => name.Contains("StatementDelivery.Crypto.Keys", StringComparison.Ordinal))
            .ShouldBeEmpty("Delivery.Api must not reference the key hierarchy - it issues links, it does not decrypt");
    }

    [Fact]
    public void Domain_ShouldStillHaveNoPackageDependencies()
    {
        // Re-asserted after Prompt 4, because this is exactly the prompt that would have broken it.
        // Crypto references Domain; Domain must not have acquired a reference back, nor a package,
        // in order to describe a CryptoEnvelope. It describes one with byte arrays and strings.
        ProjectNode domain = SolutionGraph.Projects.Single(project => project.Name == "Domain");

        domain.ProjectReferences.ShouldBeEmpty("Domain must not reference any other project");
        domain.PackageReferences.ShouldBeEmpty("Domain must not reference any NuGet package");
    }

    [Fact]
    public void Crypto_ShouldDependOnDomainOnly()
    {
        // The cipher and its tamper tests must stay runnable without a database, a bucket or an HTTP
        // stack. A project reference to Persistence or ServiceDefaults would end that quietly - and
        // would also create a cycle, since both of those now reference Crypto.
        ProjectNode crypto = SolutionGraph.Projects.Single(project => project.Name == "Crypto");

        crypto.ProjectReferences.ShouldBe(["Domain"]);

        crypto.PackageReferences
            .Where(static package => package.Contains("Npgsql", StringComparison.Ordinal)
                || package.Contains("AspNetCore", StringComparison.Ordinal)
                || package.Contains("AWSSDK.S3", StringComparison.Ordinal))
            .ShouldBeEmpty("Crypto must not take a dependency on the database, the web framework or object storage");
    }

    /// <summary>
    /// Every assembly this repository builds, discovered from the test output directory.
    /// </summary>
    /// <remarks>
    /// Discovered, not listed. The project graph is read from the .csproj files by SolutionGraph, and
    /// the assembly name matches the project name for every project here except the building blocks,
    /// which carry the StatementDelivery. prefix - so both spellings are probed and whichever .dll
    /// exists is the one inspected.
    /// </remarks>
    private static IEnumerable<string> SolutionAssemblies()
    {
        foreach (ProjectNode project in SolutionGraph.Projects)
        {
            if (project.Name.EndsWith("Tests", StringComparison.Ordinal) || project.Name == "SeedTool")
            {
                continue;
            }

            string? found = null;

            foreach (string candidate in (string[])[project.Name, "StatementDelivery." + project.Name])
            {
                // Case-insensitive on purpose: RenderHash's csproj sets AssemblyName to
                // lowercase `renderhash` for CLI ergonomics, and File.Exists("RenderHash.dll")
                // finds it only on a case-insensitive filesystem. This rule's own first Linux
                // execution (every earlier CI run failed the unit step first and skipped this
                // one) is what exposed the Windows-only probe.
                string? actual = OutputAssemblies.Value.GetValueOrDefault(candidate);
                if (actual is not null)
                {
                    found = actual;
                    break;
                }
            }

            // LOUD, NOT SILENT. A missing dll means this project is not referenced by
            // ArchitectureTests, so its IL never reaches the rule - and the rule passes, reporting
            // nothing, having inspected nothing. That is how Generation.Worker sat outside the
            // DataKey rule while the rule looked green. If a new project appears, this fails until
            // somebody decides, explicitly, whether it is in scope.
            found.ShouldNotBeNull(
                $"{project.Name} builds no assembly in the test output, so no IL-level rule can see it. "
                + "Add a ProjectReference to ArchitectureTests.csproj, or exclude it here with a reason.");

            yield return found;
        }
    }

    private static IReadOnlyList<string> ReferencedNames(string assemblyName)
    {
        using ModuleDefinition module = ModuleDefinition.ReadModule(LocateAssembly(assemblyName));

        return
        [
            .. module.GetTypeReferences().Select(static reference => reference.FullName),
            .. module.GetMemberReferences().Select(static reference => reference.FullName),
        ];
    }

    /// <summary>Assembly base names in the test output, keyed case-insensitively, valued with real casing.</summary>
    private static readonly Lazy<Dictionary<string, string>> OutputAssemblies = new(static () =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Select(static file => Path.GetFileNameWithoutExtension(file))
            .ToDictionary(static name => name, static name => name, StringComparer.OrdinalIgnoreCase));

    private static string LocateAssembly(string assemblyName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        File.Exists(path).ShouldBeTrue($"{assemblyName}.dll was not found in {AppContext.BaseDirectory}");
        return path;
    }
}
