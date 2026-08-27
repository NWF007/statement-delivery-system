using System.Globalization;
using DbUp.Engine;

namespace Db.Migrator;

/// <summary>
/// Prepends lock and statement timeouts to every migration script.
/// </summary>
/// <remarks>
/// <para>
/// Applied as a preprocessor rather than set once on the connection, so the guards are visible in
/// the script text DbUp logs. When a migration fails on a lock timeout, the reason is right there
/// in the output instead of buried in a connection string somebody has to go and find.
/// </para>
/// <para>
/// SET rather than SET LOCAL: DbUp runs each script in its own transaction, and SET LOCAL would
/// only cover that transaction. Both are session-scoped here, which is what we want, and this is
/// a direct connection so the transaction-pooling caveat about SET does not apply.
/// </para>
/// </remarks>
public sealed class SessionGuardPreprocessor : IScriptPreprocessor
{
    private readonly string _prelude;

    /// <summary>Initialises a new instance of the <see cref="SessionGuardPreprocessor"/> class.</summary>
    /// <param name="lockTimeoutSeconds">Lock timeout, in seconds.</param>
    /// <param name="statementTimeoutSeconds">Statement timeout, in seconds.</param>
    public SessionGuardPreprocessor(int lockTimeoutSeconds, int statementTimeoutSeconds) =>
        _prelude = string.Create(
            CultureInfo.InvariantCulture,
            $"SET lock_timeout = '{lockTimeoutSeconds}s';\nSET statement_timeout = '{statementTimeoutSeconds}s';\n\n");

    /// <inheritdoc />
    public string Process(string contents) => _prelude + contents;
}
