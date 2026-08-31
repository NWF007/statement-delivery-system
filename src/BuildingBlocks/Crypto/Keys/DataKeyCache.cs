using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StatementDelivery.Domain.Identifiers;

namespace StatementDelivery.Crypto.Keys;

/// <summary>A borrowed data key and the wrapped form that must be stored alongside the object.</summary>
/// <remarks>
/// <para>
/// The lease holds its OWN COPY of the key material rather than a reference to the cached one. Two
/// dozen bytes copied per object is nothing, and it removes a genuine lifetime hazard: the cache may
/// evict and wipe an entry at any moment, and a write already in flight would otherwise find its key
/// zeroed mid-stream. Ownership is unambiguous - the lease wipes its copy, the cache wipes its own.
/// </para>
/// <para>
/// ⚠ <see cref="RecordBytes"/> IS PART OF THE CONTRACT, NOT AN OPTION. The byte budget used to be
/// charged from a caller-supplied ESTIMATE at acquire time - and the streaming write path, which
/// cannot know its length up front, passed zero. The budget silently stopped counting, and a
/// two-bound safety margin on key reuse quietly became one (the Prompt 5 audit's HIGH 2). Now the
/// caller settles the REAL plaintext byte count after encrypting; an acquire-time estimate no
/// longer exists to be zero. A lease disposed without settling logs an ERROR - see
/// <see cref="Dispose"/> for why it logs rather than throws.
/// </para>
/// </remarks>
public sealed class DataKeyLease : IDisposable
{
    private readonly Action<long>? _settle;
    private readonly ILogger? _logger;
    private bool _recorded;

    internal DataKeyLease(DataKey key, byte[] wrappedDek, string algorithm, Action<long>? settle, ILogger? logger)
    {
        Key = key;
        WrappedDek = wrappedDek;
        Algorithm = algorithm;
        _settle = settle;
        _logger = logger;
    }

    /// <summary>Gets the key to encrypt with. Owned by the lease; disposed with it.</summary>
    public DataKey Key { get; }

    /// <summary>Gets the wrapped key, for the statement row.</summary>
    public byte[] WrappedDek { get; }

    /// <summary>Gets the wrapping algorithm, recorded so a future change is decodable.</summary>
    public string Algorithm { get; }

    /// <summary>
    /// Settles the byte budget with the ACTUAL plaintext byte count this key protected.
    /// </summary>
    /// <remarks>
    /// PLAINTEXT bytes, not ciphertext: the budget bounds how much material one key protects, and
    /// framing overhead is not material. Call exactly once, immediately after encryption succeeds -
    /// the bytes were protected the moment they were encrypted, whether or not the upload that
    /// follows ever lands. One deliberate consequence of settling AFTER the fact: a single object
    /// may overshoot the budget by its own size before the next acquire sees the spend. The budget
    /// is a rotation trigger, not a hard wall, and a one-object overshoot is the price of never
    /// again trusting an estimate that could be zero.
    /// </remarks>
    /// <param name="plaintextBytes">Bytes of plaintext encrypted under this lease's key.</param>
    public void RecordBytes(long plaintextBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plaintextBytes);

        if (_recorded)
        {
            throw new InvalidOperationException("RecordBytes was already called on this lease.");
        }

        _recorded = true;
        _settle?.Invoke(plaintextBytes);
    }

    /// <summary>Wipes this lease's copy of the key, and shouts if the budget was never settled.</summary>
    /// <remarks>
    /// LOGS AN ERROR RATHER THAN THROWING, deliberately. A throw here would fire inside `using`
    /// disposal on every failure path - encryption faulted, upload faulted - and REPLACE the real
    /// exception with a bookkeeping one, which is exactly the error-masking this remediation fixes
    /// elsewhere. A mid-encryption fault legitimately cannot settle (the count is unknowable), so
    /// unsettled-on-failure is expected; unsettled-on-SUCCESS is the forgotten-call bug, and the
    /// error log plus the seam tests are what make it loud.
    /// </remarks>
    public void Dispose()
    {
        if (!_recorded)
        {
            _logger?.UnsettledLeaseDisposed();
        }

        Key.Dispose();
    }
}

/// <summary>High-performance log messages for the data-key subsystem.</summary>
internal static partial class DataKeyLog
{
    [LoggerMessage(
        EventId = 4020,
        Level = LogLevel.Error,
        Message = "A DataKeyLease was disposed without RecordBytes. If the write SUCCEEDED, the byte "
                + "budget just under-counted and key rotation is running late - find the caller and "
                + "add the settle. (A lease abandoned by a mid-encryption failure also lands here; "
                + "correlate with the write error.)")]
    public static partial void UnsettledLeaseDisposed(this ILogger logger);
}

/// <summary>Supplies data keys for encrypting and decrypting statement objects.</summary>
public interface IDataKeyBroker
{
    /// <summary>
    /// Drops any cached key material for a customer, wiping it. Called by the erasure executor
    /// after destroying the CEK, so THIS process's cache cannot outlive the key. Other
    /// processes' caches expire on MaxAge - the cross-process gap is documented in
    /// docs/LIMITATIONS.md, and the database-side write guard (Part G) is what actually closes
    /// the dangerous path.
    /// </summary>
    /// <param name="customer">The customer.</param>
    void Evict(CustomerId customer);

    /// <summary>Takes a data key for writing one object.</summary>
    /// <remarks>
    /// NO BYTE ESTIMATE, ON PURPOSE. The previous signature took one, and the streaming caller -
    /// which cannot measure a pipe - passed zero, silently disabling the byte budget. The budget is
    /// now settled with the REAL count via <see cref="DataKeyLease.RecordBytes"/> after encryption;
    /// an estimate that could lie no longer exists.
    /// </remarks>
    /// <param name="customer">Whose CEK wraps it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A lease. Settle it with RecordBytes, then dispose it.</returns>
    Task<DataKeyLease> AcquireAsync(CustomerId customer, CancellationToken ct);

    /// <summary>Unwraps the data key recorded against an object, for reading it back.</summary>
    /// <param name="customer">Whose CEK wraps it.</param>
    /// <param name="wrappedDek">The wrapped key from the statement row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The key. The caller owns it and must dispose it.</returns>
    Task<DataKey> UnwrapDekAsync(CustomerId customer, ReadOnlyMemory<byte> wrappedDek, CancellationToken ct);
}

/// <summary>Bounds on how far one data key may be reused.</summary>
public sealed class DataKeyCacheOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Crypto:DekCache";

    /// <summary>Gets or sets how many objects one data key may encrypt.</summary>
    [Range(1, 100_000)]
    public int MaxObjectsPerKey { get; set; } = 1_000;

    /// <summary>Gets or sets how many bytes one data key may encrypt.</summary>
    [Range(1024, 64L * 1024 * 1024 * 1024)]
    public long MaxBytesPerKey { get; set; } = 256L * 1024 * 1024;

    /// <summary>Gets or sets how long a data key may live.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// A per-process cache of data keys, bounded by object count, byte count and age.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS, HONESTLY. The obvious justification is cost, and the obvious justification is
/// wrong. A <c>GenerateDataKey</c> call costs about $0.03 per 10,000 requests; at 30 million
/// statements a month that is roughly $90, which nobody would build a cache for.
/// </para>
/// <para>
/// THE REAL REASON IS KMS REQUEST-RATE QUOTA. Rendering 30 million statements inside a month-end
/// window means about 1,400 <c>GenerateDataKey</c> calls PER SECOND sustained across 400 workers.
/// KMS enforces account-level request-rate limits, and crossing one does not slow the render down
/// gracefully - it returns throttling errors, which turn into retries, which raise the rate further.
/// This is a THROUGHPUT control that happens to save money, not a cost control. Getting that the
/// wrong way round would mean tuning it against the wrong number.
/// </para>
/// <para>
/// BLAST RADIUS, stated so the bounds can be argued with rather than assumed: a data key recovered
/// from a compromised worker process decrypts at most 1,000 statements written in a 5-minute window
/// on that ONE worker. Not the corpus, not the customer's history, not the fleet.
/// </para>
/// <para>
/// REUSING A KEY ACROSS OBJECTS IS SAFE HERE, and it is worth being explicit about why, because
/// "one key, many messages" is exactly the shape of a GCM nonce-reuse disaster. It is safe because
/// every object gets a FRESH RANDOM NONCE PREFIX at encryption time - the prefix is per-OBJECT, not
/// per-key. Two objects under one key therefore share no nonce. Had the prefix been derived from the
/// key or from a counter, this cache would be a critical vulnerability rather than an optimisation.
/// </para>
/// <para>
/// SCOPE IS ONE PROCESS. Keys are never shared between services or replicas: a distributed key cache
/// would need key material to cross the network, which is precisely what the envelope design exists
/// to avoid.
/// </para>
/// </remarks>
public sealed class DataKeyCache : IDataKeyBroker, IDisposable
{
    private readonly ICustomerKeyService _customerKeys;
    private readonly DataKeyCacheOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<DataKeyCache>? _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private bool _disposed;

    /// <summary>Initialises a new instance of the <see cref="DataKeyCache"/> class.</summary>
    /// <param name="customerKeys">The customer key service.</param>
    /// <param name="options">Cache bounds.</param>
    /// <param name="time">Time provider.</param>
    /// <param name="logger">Logger for the unsettled-lease error. Optional so tests stay light.</param>
    public DataKeyCache(
        ICustomerKeyService customerKeys,
        IOptions<DataKeyCacheOptions> options,
        TimeProvider time,
        ILogger<DataKeyCache>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _customerKeys = customerKeys;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DataKeyLease> AcquireAsync(CustomerId customer, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (TryTake(customer.Value, out DataKeyLease? cached))
        {
            return cached;
        }

        // The CEK is unwrapped, used and disposed inside this method. It never enters the cache: what
        // is cached is the DEK it wraps, one tier down, where the reuse bounds apply.
        using DataKey cek = await _customerKeys.GetOrCreateCekAsync(customer, ct).ConfigureAwait(false);

        // OWNERSHIP DOES NOT PASS UNTIL Install RETURNS, so the window between them needs the same
        // guard KeyProvider.GenerateDataKeyAsync uses. If Wrap throws - a malformed CEK, a disposed
        // one - nothing else will ever dispose this key, and it sits in the pinned heap holding live
        // material until the process ends. That is exactly the leak the every-use-inside-a-using
        // rule exists to prevent, in the one place a `using` cannot express it.
        DataKey dek = DataKey.Generate();

        try
        {
            byte[] wrapped = KeyWrap.Wrap(cek.Span, dek.Span, ContextFor(customer));
            Install(customer.Value, new Entry(dek, wrapped, _time.GetUtcNow()));
        }
        catch
        {
            dek.Dispose();
            throw;
        }

        // A freshly installed entry has zero spend, so this take can only fail if another thread
        // raced it through the whole budget between Install and here - in which case looping once
        // more mints again, which is correct.
        return TryTake(customer.Value, out DataKeyLease? fresh)
            ? fresh
            : await AcquireAsync(customer, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DataKey> UnwrapDekAsync(CustomerId customer, ReadOnlyMemory<byte> wrappedDek, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using DataKey cek = await _customerKeys.UnwrapCekAsync(customer, ct).ConfigureAwait(false);
        return KeyWrap.Unwrap(cek.Span, wrappedDek.Span, ContextFor(customer));
    }

    /// <inheritdoc />
    public void Evict(CustomerId customer)
    {
        lock (_gate)
        {
            if (_entries.Remove(customer.Value, out Entry? entry))
            {
                entry.Key.Dispose();
            }
        }
    }

    /// <summary>Wipes every cached key.</summary>
    /// <remarks>
    /// ON SHUTDOWN, ZEROISE. Process exit does not clear memory; the pages go back to the operating
    /// system with the key material still in them. Wiping on the way out is cheap and closes the
    /// window where a core dump or a swapped page taken after shutdown still yields live keys.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_gate)
        {
            foreach (Entry entry in _entries.Values)
            {
                entry.Key.Dispose();
            }

            _entries.Clear();
        }
    }

    private static string ContextFor(CustomerId customer) => string.Create(
        CultureInfo.InvariantCulture,
        $"statement-dek/{customer.Value:D}");

    private bool TryTake(Guid customer, [NotNullWhen(true)] out DataKeyLease? lease)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(customer, out Entry? entry))
            {
                lease = null;
                return false;
            }

            // The byte bound now reads SETTLED spend - real bytes recorded by completed
            // encryptions - rather than a sum of estimates. A key is refused once its recorded
            // spend has reached the budget; in-flight leases settle into the entry as they finish,
            // so the worst overshoot is the objects currently in flight, not an unbounded drift.
            bool expired = _time.GetUtcNow() - entry.CreatedAt >= _options.MaxAge;
            bool objectBudgetSpent = entry.ObjectsUsed + 1 > _options.MaxObjectsPerKey;
            bool byteBudgetSpent = entry.BytesUsed >= _options.MaxBytesPerKey;

            if (expired || objectBudgetSpent || byteBudgetSpent)
            {
                // Evicted AND wiped. Dropping the reference alone would leave the key sitting in the
                // heap until a collection that may never come. In-flight leases hold their own key
                // COPY, so a settle arriving after this eviction lands nowhere - harmless, because a
                // retired key's budget no longer gates anything.
                entry.Key.Dispose();
                _ = _entries.Remove(customer);

                lease = null;
                return false;
            }

            entry.ObjectsUsed++;

            Entry settleTarget = entry;
            lease = new DataKeyLease(
                DataKey.CopyFrom(entry.Key.Span),
                [.. entry.WrappedDek],
                KeyWrap.AlgorithmName,
                bytes => Settle(customer, settleTarget, bytes),
                _logger);
            return true;
        }
    }

    /// <summary>Adds a completed encryption's real byte count to its entry's spend.</summary>
    private void Settle(Guid customer, Entry target, long plaintextBytes)
    {
        lock (_gate)
        {
            // Only if the SAME entry is still installed: a settle for a rotated-away key must not
            // charge its successor.
            if (_entries.TryGetValue(customer, out Entry? current) && ReferenceEquals(current, target))
            {
                current.BytesUsed += plaintextBytes;
            }
        }
    }

    private void Install(Guid customer, Entry entry)
    {
        lock (_gate)
        {
            // A concurrent acquire for the same customer may have installed one first. Both keys are
            // valid and both are wrapped under the same CEK, so the loser is simply discarded - one
            // wasted key generation, no correctness consequence. Serialising acquisitions to avoid it
            // would put a lock across an await on the hot path of a 400-replica render.
            if (_entries.Remove(customer, out Entry? previous))
            {
                previous.Key.Dispose();
            }

            _entries[customer] = entry;
        }
    }

    private sealed class Entry(DataKey key, byte[] wrappedDek, DateTimeOffset createdAt)
    {
        public DataKey Key { get; } = key;

        public byte[] WrappedDek { get; } = wrappedDek;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public int ObjectsUsed { get; set; }

        public long BytesUsed { get; set; }
    }
}
