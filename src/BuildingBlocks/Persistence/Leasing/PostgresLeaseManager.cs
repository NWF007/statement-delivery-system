using System.Globalization;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using StatementDelivery.Persistence.Connections;

namespace StatementDelivery.Persistence.Leasing;

/// <summary>
/// <see cref="ILeaseManager"/> implemented against the <c>distributed_lease</c> table.
/// </summary>
public sealed partial class PostgresLeaseManager : ILeaseManager
{
    /// <summary>
    /// Acquire-or-renew, as one atomic statement.
    /// </summary>
    /// <remarks>
    /// The WHERE clause on the DO UPDATE branch is what makes this safe. It fires only when the
    /// row already belongs to this holder (a renewal) or when the existing lease has expired (a
    /// takeover). Any other case updates no rows and RETURNING yields nothing, which is the
    /// signal that somebody else is the leader. There is no read-then-write, so there is no race
    /// to lose. Read/write intent is Write because this must never touch a replica.
    /// </remarks>
    private const string AcquireOrRenewSql = """
        INSERT INTO distributed_lease (lease_name, holder_id, acquired_at, expires_at, fence_token)
        VALUES (@name, @holder, now(), now() + @ttl, 1)
        ON CONFLICT (lease_name) DO UPDATE
        SET holder_id   = EXCLUDED.holder_id,
            acquired_at = now(),
            expires_at  = EXCLUDED.expires_at,
            fence_token = distributed_lease.fence_token + 1
        WHERE distributed_lease.holder_id = EXCLUDED.holder_id
           OR distributed_lease.expires_at < now()
        RETURNING fence_token;
        """;

    /// <summary>
    /// Courtesy release. Expiring the row rather than deleting it keeps the fence token, so the
    /// next holder's token is still strictly greater than every token ever issued.
    /// </summary>
    private const string ReleaseSql = """
        UPDATE distributed_lease
        SET expires_at = now()
        WHERE lease_name = @name
          AND holder_id = @holder;
        """;

    private readonly IDbConnectionFactory _connections;
    private readonly LeaseOptions _options;
    private readonly string _holderId;
    private readonly ILogger<PostgresLeaseManager> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Initialises a new instance of the <see cref="PostgresLeaseManager"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    /// <param name="options">Lease options.</param>
    /// <param name="loggerFactory">Logger factory, used for the per-handle renewal logger.</param>
    public PostgresLeaseManager(
        IDbConnectionFactory connections,
        IOptions<LeaseOptions> options,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _connections = connections;
        _options = options.Value;
        _holderId = string.IsNullOrWhiteSpace(_options.HolderId) ? DefaultHolderId : _options.HolderId;
        HolderId = _holderId;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PostgresLeaseManager>();
    }

    /// <summary>
    /// Gets the default holder identity: host name plus process id.
    /// </summary>
    /// <remarks>
    /// In Kubernetes the host name is the pod name, so the lease table names the leading pod
    /// without an operator having to correlate anything.
    /// </remarks>
    public static string DefaultHolderId { get; } = string.Create(
        CultureInfo.InvariantCulture,
        $"{Environment.MachineName}:{Environment.ProcessId}");

    /// <summary>
    /// Gets the identity this instance writes into the lease row.
    /// </summary>
    /// <remarks>
    /// Overridable through <see cref="LeaseOptions.HolderId"/>. Two managers sharing an identity
    /// would each treat the other's lease as their own to RENEW rather than contend for, so both
    /// would believe they were the leader. Separate processes get distinct identities from the
    /// default; anything hosting two managers in one process must set this explicitly.
    /// </remarks>
    public string HolderId { get; private init; } = DefaultHolderId;

    /// <inheritdoc />
    public async Task<ILeaseHandle?> TryAcquireAsync(string leaseName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseName);

        long? fenceToken = await AcquireOrRenewAsync(leaseName, cancellationToken).ConfigureAwait(false);
        if (fenceToken is null)
        {
            LogLeaseHeldElsewhere(_logger, leaseName);
            return null;
        }

        LogLeaseAcquired(_logger, leaseName, _holderId, fenceToken.Value);

        return new LeaseHandle(this, leaseName, fenceToken.Value, _options, _loggerFactory.CreateLogger<LeaseHandle>());
    }

    private async Task<long?> AcquireOrRenewAsync(string leaseName, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        var command = new CommandDefinition(
            AcquireOrRenewSql,
            new { name = leaseName, holder = _holderId, ttl = _options.TimeToLive },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken);

        return await connection.ExecuteScalarAsync<long?>(command).ConfigureAwait(false);
    }

    private async Task ReleaseAsync(string leaseName)
    {
        // Deliberately not passing the caller's cancellation token: release runs during shutdown,
        // when that token is already cancelled. A lease that is not released simply expires, so
        // failure here costs one time-to-live of failover delay and nothing else.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using NpgsqlConnection connection =
                await _connections.OpenAsync(ConnectionIntent.Write, timeout.Token).ConfigureAwait(false);

            var command = new CommandDefinition(
                ReleaseSql,
                new { name = leaseName, holder = _holderId },
                commandTimeout: 5,
                cancellationToken: timeout.Token);

            _ = await connection.ExecuteAsync(command).ConfigureAwait(false);
            LogLeaseReleased(_logger, leaseName, _holderId);
        }
        catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or TimeoutException)
        {
            LogLeaseReleaseFailed(_logger, ex, leaseName, _options.TimeToLive);
        }
    }

    private sealed partial class LeaseHandle : ILeaseHandle
    {
        private readonly PostgresLeaseManager _owner;
        private readonly LeaseOptions _options;
        private readonly ILogger<LeaseHandle> _logger;
        private readonly CancellationTokenSource _lost = new();
        private readonly CancellationTokenSource _stopRenewal = new();
        private readonly Task _renewalLoop;

        public LeaseHandle(
            PostgresLeaseManager owner,
            string leaseName,
            long fenceToken,
            LeaseOptions options,
            ILogger<LeaseHandle> logger)
        {
            _owner = owner;
            _options = options;
            _logger = logger;
            LeaseName = leaseName;
            FenceToken = fenceToken;
            _renewalLoop = RenewAsync(_stopRenewal.Token);
        }

        public string LeaseName { get; }

        public string HolderId => _owner.HolderId;

        public long FenceToken { get; private set; }

        public CancellationToken LeaseLost => _lost.Token;

        public async ValueTask DisposeAsync()
        {
            await _stopRenewal.CancelAsync().ConfigureAwait(false);
            try
            {
                await _renewalLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the renewal loop was stopped on purpose.
            }

            await _owner.ReleaseAsync(LeaseName).ConfigureAwait(false);

            _stopRenewal.Dispose();
            _lost.Dispose();
        }

        private async Task RenewAsync(CancellationToken cancellationToken)
        {
            using var ticker = new PeriodicTimer(_options.RenewalInterval);

            try
            {
                while (await ticker.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    long? token = await _owner.AcquireOrRenewAsync(LeaseName, cancellationToken).ConfigureAwait(false);
                    if (token is null)
                    {
                        // Zero rows means the lease expired and somebody else took it over. The work
                        // this handle guards must stop now: the process is fine, but it is no longer
                        // the leader, and carrying on would mean two leaders.
                        LogLeaseLost(_logger, LeaseName, HolderId);
                        await _lost.CancelAsync().ConfigureAwait(false);
                        return;
                    }

                    FenceToken = token.Value;
                    LogLeaseRenewed(_logger, LeaseName, HolderId, token.Value);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (NpgsqlException ex)
            {
                // A renewal that cannot reach the database is indistinguishable from a lost lease
                // as far as safety goes: another replica may already have taken over. Fail closed.
                LogLeaseRenewalUnreachable(_logger, ex, LeaseName);
                await _lost.CancelAsync().ConfigureAwait(false);
            }
        }

        [LoggerMessage(
            EventId = 1210,
            Level = LogLevel.Error,
            Message = "lease lost: {LeaseName} is no longer held by {HolderId}. Leased work must stop.")]
        private static partial void LogLeaseLost(ILogger logger, string leaseName, string holderId);

        [LoggerMessage(
            EventId = 1211,
            Level = LogLevel.Debug,
            Message = "Lease {LeaseName} renewed by {HolderId}, fence token {FenceToken}.")]
        private static partial void LogLeaseRenewed(ILogger logger, string leaseName, string holderId, long fenceToken);

        [LoggerMessage(
            EventId = 1212,
            Level = LogLevel.Error,
            Message = "Renewal of lease {LeaseName} failed to reach the database. Treating the lease as lost.")]
        private static partial void LogLeaseRenewalUnreachable(ILogger logger, Exception exception, string leaseName);
    }

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Debug,
        Message = "Lease {LeaseName} is held by another replica. This replica will not run the leased work.")]
    private static partial void LogLeaseHeldElsewhere(ILogger logger, string leaseName);

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Information,
        Message = "lease acquired: {LeaseName} by {HolderId} with fence token {FenceToken}")]
    private static partial void LogLeaseAcquired(ILogger logger, string leaseName, string holderId, long fenceToken);

    [LoggerMessage(
        EventId = 1202,
        Level = LogLevel.Information,
        Message = "lease released: {LeaseName} by {HolderId}")]
    private static partial void LogLeaseReleased(ILogger logger, string leaseName, string holderId);

    [LoggerMessage(
        EventId = 1203,
        Level = LogLevel.Warning,
        Message = "Could not release lease {LeaseName} cleanly. It will expire within {TimeToLive}.")]
    private static partial void LogLeaseReleaseFailed(ILogger logger, Exception exception, string leaseName, TimeSpan timeToLive);
}
