using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Options;
using StatementDelivery.Domain.Identifiers;

namespace StatementDelivery.Crypto.Keys;

/// <summary>A borrowed data key and the wrapped form that must be stored alongside the object.</summary>
/// <param name="Key">The key to encrypt with. Owned by the lease; disposed with it.</param>
/// <param name="WrappedDek">The wrapped key, for the statement row.</param>
/// <param name="Algorithm">The wrapping algorithm, recorded so a future change is decodable.</param>
/// <remarks>
/// The lease holds its OWN COPY of the key material rather than a reference to the cached one. Two
/// dozen bytes copied per object is nothing, and it removes a genuine lifetime hazard: the cache may
/// evict and wipe an entry at any moment, and a write already in flight would otherwise find its key
/// zeroed mid-stream. Ownership is unambiguous - the lease wipes its copy, the cache wipes its own.
/// </remarks>
public sealed record DataKeyLease(DataKey Key, byte[] WrappedDek, string Algorithm) : IDisposable
{
    /// <summary>Wipes this lease's copy of the key.</summary>
    public void Dispose() => Key.Dispose();
}

/// <summary>Supplies data keys for encrypting and decrypting statement objects.</summary>
public interface IDataKeyBroker
{
    /// <summary>Takes a data key for writing one object.</summary>
    /// <param name="customer">Whose CEK wraps it.</param>
    /// <param name="expectedBytes">Roughly how much will be encrypted, for the byte budget.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A lease. Dispose it when the write completes.</returns>
    Task<DataKeyLease> AcquireAsync(CustomerId customer, long expectedBytes, CancellationToken ct);

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
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private bool _disposed;

    /// <summary>Initialises a new instance of the <see cref="DataKeyCache"/> class.</summary>
    /// <param name="customerKeys">The customer key service.</param>
    /// <param name="options">Cache bounds.</param>
    /// <param name="time">Time provider.</param>
    public DataKeyCache(
        ICustomerKeyService customerKeys,
        IOptions<DataKeyCacheOptions> options,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);

        _customerKeys = customerKeys;
        _options = options.Value;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<DataKeyLease> AcquireAsync(CustomerId customer, long expectedBytes, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedBytes);

        if (TryTake(customer.Value, expectedBytes, out DataKeyLease? cached))
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

        return TryTake(customer.Value, expectedBytes, out DataKeyLease? fresh)
            ? fresh

            // Reachable only if the budget is smaller than a single object, which is a configuration
            // error rather than a runtime condition. Failing loudly beats silently spending a KMS
            // call per object and wondering later why the render throttles.
            : throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"A freshly minted data key could not cover {expectedBytes} bytes; Crypto:DekCache:MaxBytesPerKey is {_options.MaxBytesPerKey}."));
    }

    /// <inheritdoc />
    public async Task<DataKey> UnwrapDekAsync(CustomerId customer, ReadOnlyMemory<byte> wrappedDek, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using DataKey cek = await _customerKeys.UnwrapCekAsync(customer, ct).ConfigureAwait(false);
        return KeyWrap.Unwrap(cek.Span, wrappedDek.Span, ContextFor(customer));
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

    private bool TryTake(Guid customer, long expectedBytes, [NotNullWhen(true)] out DataKeyLease? lease)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(customer, out Entry? entry))
            {
                lease = null;
                return false;
            }

            bool expired = _time.GetUtcNow() - entry.CreatedAt >= _options.MaxAge;
            bool objectBudgetSpent = entry.ObjectsUsed + 1 > _options.MaxObjectsPerKey;
            bool byteBudgetSpent = entry.BytesUsed + expectedBytes > _options.MaxBytesPerKey;

            if (expired || objectBudgetSpent || byteBudgetSpent)
            {
                // Evicted AND wiped. Dropping the reference alone would leave the key sitting in the
                // heap until a collection that may never come.
                entry.Key.Dispose();
                _ = _entries.Remove(customer);

                lease = null;
                return false;
            }

            entry.ObjectsUsed++;
            entry.BytesUsed += expectedBytes;

            lease = new DataKeyLease(DataKey.CopyFrom(entry.Key.Span), [.. entry.WrappedDek], KeyWrap.AlgorithmName);
            return true;
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
