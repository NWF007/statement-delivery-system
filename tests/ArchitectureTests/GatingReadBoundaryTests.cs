using Mono.Cecil;
using Shouldly;
using Xunit;

namespace ArchitectureTests;

/// <summary>
/// A read that gates an access decision must run inside the transaction that makes it.
/// </summary>
/// <remarks>
/// <para>
/// <c>IStatementReadRepository.FindAsync</c> has two overloads with the same name and the same SQL.
/// The three-argument one opens its own connection at <c>ConnectionIntent.ReadEventual</c> and is
/// correct for the catalogue. The four-argument one runs on the caller's transaction and is the
/// only one correct for the download gateway, where the result decides whether to serve bytes.
/// </para>
/// <para>
/// THE TWO ARE ONE KEYSTROKE APART AND THE WRONG ONE COMPILES. That is exactly the shape a rule
/// has to catch, because nothing else will: the wrong overload type-checks, passes every unit
/// test, and works perfectly on a laptop with no replica. It fails only under replication lag, in
/// production, and it fails by burning a customer's single-use token and raising a ciphertext
/// integrity alert for what is actually a database routing bug.
/// </para>
/// <para>
/// Checked against the compiled IL rather than the source, so a call that exists on a branch
/// nobody executes is still caught. See
/// docs/adr/0024-security-gating-reads-run-in-the-callers-transaction.md.
/// </para>
/// </remarks>
public sealed class GatingReadBoundaryTests
{
    private const string FindMember = "IStatementReadRepository::FindAsync";
    private const string TransactionParameter = "NpgsqlTransaction";

    [Fact]
    public void DownloadGateway_MustNotCall_TransactionlessStatementRead()
    {
        IReadOnlyList<string> calls = FindAsyncCalls("Download.Gateway");

        // Without this the rule passes vacuously the day someone renames the method, moves the
        // call, or splits the endpoint into a class this test no longer reads.
        calls.ShouldNotBeEmpty(
            "Download.Gateway must call IStatementReadRepository.FindAsync - otherwise this rule is "
            + "inspecting the wrong assembly and would pass no matter what the gateway did");

        calls
            .Where(static call => !call.Contains(TransactionParameter, StringComparison.Ordinal))
            .ShouldBeEmpty(
                "Download.Gateway must use the transaction-accepting FindAsync overload. The "
                + "transactionless one opens a second connection at ReadEventual: under replication "
                + "lag it denies a statement that exists while the consume still commits, burning a "
                + "single-use token, and it deadlocks the pool at concurrency >= pool_size.");
    }

    [Fact]
    public void TheTransactionalOverload_IsTheOneTheGatewayUses()
    {
        // The positive half. The rule above proves the gateway calls no BAD overload; this proves
        // it calls the GOOD one, so "no bad calls" cannot be satisfied by making no calls at all.
        FindAsyncCalls("Download.Gateway")
            .ShouldContain(
                static call => call.Contains(TransactionParameter, StringComparison.Ordinal),
                "Download.Gateway must resolve the statement through the transactional overload");
    }

    /// <summary>
    /// Every <c>FindAsync</c> call site in an assembly, as a full signature.
    /// </summary>
    /// <remarks>
    /// The FULL name, deliberately - <c>Type::Member</c> alone cannot tell two overloads apart, and
    /// telling them apart is the whole job here. The parameter list is what carries
    /// <c>NpgsqlTransaction</c>.
    /// </remarks>
    private static IReadOnlyList<string> FindAsyncCalls(string assemblyName)
    {
        using ModuleDefinition module = ModuleDefinition.ReadModule(LocateAssembly(assemblyName));

        return
        [
            .. module.GetMemberReferences()
                .Select(static reference => reference.FullName)
                .Where(static name => name.Contains(FindMember, StringComparison.Ordinal)),
        ];
    }

    private static string LocateAssembly(string assemblyName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        File.Exists(path).ShouldBeTrue($"{assemblyName}.dll was not found in {AppContext.BaseDirectory}");
        return path;
    }
}
