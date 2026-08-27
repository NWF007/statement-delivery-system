using Dapper;
using Npgsql;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Statements;

namespace StatementDelivery.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IStatementWriteRepository"/>.
/// </summary>
public sealed class StatementWriteRepository : IStatementWriteRepository
{
    // No storage or crypto columns. They are populated by the generation path, and a repository
    // that could write them today would let a caller claim bytes exist before anything wrote them.
    private const string InsertSql = """
        INSERT INTO statement (
            id, account_id, customer_id, period_start, period_end,
            version, status, retain_until, generated_at)
        VALUES (
            @id, @accountId, @customerId, @periodStart, @periodEnd,
            @version, @status, @retainUntil, @generatedAt);
        """;

    // period_start is in the predicate, not because id is insufficient to identify the row, but
    // because without it PostgreSQL must visit every partition to find out which one holds it.
    private const string UpdateStatusSql = """
        UPDATE statement
           SET status = @status
         WHERE id = @id
           AND period_start = @periodStart;
        """;

    private readonly IDbConnectionFactoryTimeouts _timeouts;

    /// <summary>Initialises a new instance of the <see cref="StatementWriteRepository"/> class.</summary>
    /// <param name="timeouts">Supplies the command timeout budget for write intent.</param>
    public StatementWriteRepository(IDbConnectionFactoryTimeouts timeouts) => _timeouts = timeouts;

    /// <inheritdoc />
    public async Task InsertAsync(Statement statement, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(transaction);

        _ = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            InsertSql,
            new
            {
                id = statement.Id.Value,
                accountId = statement.AccountId.Value,
                customerId = statement.CustomerId.Value,
                periodStart = statement.Period.Start,
                periodEnd = statement.Period.End,
                version = statement.Version,
                status = statement.Status.ToString().ToUpperInvariant(),
                retainUntil = statement.RetainUntil,
                generatedAt = statement.GeneratedAt,
            },
            transaction: transaction,
            commandTimeout: _timeouts.WriteCommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> UpdateStatusAsync(
        StatementId id,
        DateOnly partitionKey,
        StatementStatus status,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            UpdateStatusSql,
            new
            {
                id = id.Value,
                periodStart = partitionKey,
                status = status.ToString().ToUpperInvariant(),
            },
            transaction: transaction,
            commandTimeout: _timeouts.WriteCommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}

/// <summary>
/// Exposes the configured command-timeout budgets to components that operate on a caller-supplied
/// transaction rather than opening their own connection.
/// </summary>
/// <remarks>
/// A write repository never calls <c>OpenAsync</c> - it is handed a transaction - so it cannot get
/// its timeout from the connection string the way a read repository does. It still must set one:
/// "every query has an explicit timeout" has no exceptions, because a query with no timeout holds
/// a pooled connection that the whole fleet shares.
/// </remarks>
public interface IDbConnectionFactoryTimeouts
{
    /// <summary>Gets the command timeout for write intent, in seconds.</summary>
    int WriteCommandTimeoutSeconds { get; }
}
