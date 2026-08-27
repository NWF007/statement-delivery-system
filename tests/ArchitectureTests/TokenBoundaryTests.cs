using Mono.Cecil;
using Shouldly;
using Xunit;

namespace ArchitectureTests;

/// <summary>
/// The boundaries that keep the plaintext download token in one place.
/// </summary>
/// <remarks>
/// <para>
/// The type system already does most of this work: <c>TokenSecret</c> is a <c>ref struct</c>, so it
/// cannot be boxed, stored in a field, captured by a lambda, put in a collection or used as a
/// generic argument. Trying to persist one is a compile error rather than a review comment.
/// </para>
/// <para>
/// These tests cover what the type system cannot: that the assemblies which have no business
/// touching the plaintext at all do not reference it, and that the service which redeems tokens
/// cannot mint them. Both are checked against the compiled IL, so a reference that exists but is
/// never executed still fails.
/// </para>
/// </remarks>
public sealed class TokenBoundaryTests
{
    [Fact]
    public void Persistence_ShouldNotReference_TokenSecret()
    {
        // The repositories see HASHES ONLY. If persistence code could take a TokenSecret it could
        // also write one to a column, and the single most important invariant in this design -
        // the plaintext exists in exactly one place and never at rest - would rest on nobody
        // making that mistake.
        IReadOnlyList<string> references = ReferencedNames("StatementDelivery.Persistence");

        references.ShouldNotBeEmpty("the Persistence assembly must be readable for this rule to mean anything");

        references
            .Where(static name => name.Contains("TokenSecret", StringComparison.Ordinal))
            .ShouldBeEmpty("Persistence must never reference TokenSecret - repositories handle TokenHash only");

        // It must reference the hash, or the assembly under test is not the one that stores tokens
        // and this rule is passing over the wrong file.
        references.ShouldContain(
            static name => name.Contains("TokenHash", StringComparison.Ordinal),
            "Persistence should reference TokenHash - otherwise this test is inspecting the wrong assembly");
    }

    [Fact]
    public void DownloadGateway_ShouldNotReference_TokenIssuance()
    {
        // The gateway REDEEMS. It parses an incoming token and hashes it, so it legitimately
        // references TokenSecret - but it must not be able to create one, mint a DownloadToken, or
        // insert a row. Its database role cannot INSERT either (V011), so this is the compile-time
        // half of a rule the database enforces at runtime.
        IReadOnlyList<string> members = ReferencedMembers("Download.Gateway");

        members.ShouldNotBeEmpty("the Download.Gateway assembly must be readable for this rule to mean anything");

        string[] issuanceMembers =
        [
            "TokenSecret::Generate",
            "DownloadToken::Issue",
            "IDownloadTokenRepository::InsertAsync",
            "IRandomBytes::Fill",
        ];

        foreach (string member in issuanceMembers)
        {
            members
                .Where(name => name.Contains(member, StringComparison.Ordinal))
                .ShouldBeEmpty($"Download.Gateway must not reference {member} - it redeems tokens, it does not mint them");
        }

        // And it must reference the redemption side, or this rule is vacuous.
        members.ShouldContain(
            static name => name.Contains("TokenSecret::TryParse", StringComparison.Ordinal),
            "Download.Gateway should parse tokens - otherwise this test is inspecting the wrong assembly");
    }

    [Fact]
    public void DeliveryApi_ShouldNotReference_TheContentStore()
    {
        // Issuing a link must not be able to read a statement's bytes. Only the gateway streams
        // content, and keeping that true means a compromise of the authenticated API yields links
        // rather than documents.
        IReadOnlyList<string> references = ReferencedNames("Delivery.Api");

        references.ShouldNotBeEmpty("the Delivery.Api assembly must be readable for this rule to mean anything");

        references
            .Where(static name => name.Contains("IStatementContentStore", StringComparison.Ordinal))
            .ShouldBeEmpty("Delivery.Api must not reference the content store - it issues links, it does not serve bytes");
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

    private static IReadOnlyList<string> ReferencedMembers(string assemblyName)
    {
        using ModuleDefinition module = ModuleDefinition.ReadModule(LocateAssembly(assemblyName));

        // Normalised to "Type::Member" so a rule can name a method without also naming its return
        // type and parameter list, which change whenever a signature does.
        return
        [
            .. module.GetMemberReferences()
                .Select(static reference => $"{reference.DeclaringType.Name}::{reference.Name}"),
        ];
    }

    /// <summary>
    /// Finds a compiled assembly next to the test binary.
    /// </summary>
    /// <remarks>
    /// Reads the IL rather than loading the assembly, so this works for projects the test assembly
    /// references and for ones it merely sits alongside in the output directory.
    /// </remarks>
    private static string LocateAssembly(string assemblyName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        File.Exists(path).ShouldBeTrue($"{assemblyName}.dll was not found in {AppContext.BaseDirectory}");
        return path;
    }
}
