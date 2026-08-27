using System.Security.Cryptography;

namespace StatementDelivery.Crypto.Keys;

/// <summary>
/// Plaintext symmetric key material, held in a pinned buffer and wiped on dispose.
/// </summary>
/// <remarks>
/// <para>
/// THE RULES THIS TYPE EXISTS TO ENFORCE, all of which are checked somewhere rather than merely
/// hoped for:
/// </para>
/// <para>
/// IT IS NEVER A PROPERTY ON A PERSISTED ENTITY. Nothing that gets written to a row, serialised to
/// JSON or put on a queue may hold one. Only the WRAPPED form is ever persisted, and the wrapped
/// form is a plain <c>byte[]</c> that carries no such danger.
/// </para>
/// <para>
/// NO PROJECT OUTSIDE <c>StatementDelivery.Crypto</c> REFERENCES IT. An architecture test asserts
/// this. That is what keeps key material from drifting into a service, where it would eventually be
/// logged, traced or returned.
/// </para>
/// <para>
/// EVERY USE IS INSIDE A <c>using</c>. The lifetime of plaintext key material is the lifetime of
/// the operation that needs it, and no longer.
/// </para>
/// <para>
/// <see cref="ToString"/> IS OVERRIDDEN, and that small override is worth more than it looks.
/// Structured loggers serialise objects they are handed. A class holding a <c>byte[]</c> has a
/// harmless default <c>ToString()</c> today - but converting this to a <c>record</c> in some future
/// tidy-up would silently give it a synthesised one that prints its members, and a single
/// interpolation into a log line would then publish a live key to the log aggregator. Overriding it
/// now closes that door before anyone opens it.
/// </para>
/// <para>
/// The buffer is PINNED. An unpinned array can be relocated by a compacting collection, which
/// leaves the old bytes lying in the heap where nothing will ever wipe them; pinning means
/// <see cref="Dispose"/> zeroes the one and only copy. This is a mitigation, not a guarantee - the
/// key still exists in process memory while in use and a core dump taken at the wrong moment
/// contains it. Guaranteeing more than that needs an HSM, which is the trade ADR-0020 examines.
/// </para>
/// </remarks>
public sealed class DataKey : IDisposable
{
    private readonly byte[] _material;
    private bool _disposed;

    private DataKey(byte[] pinnedMaterial) => _material = pinnedMaterial;

    /// <summary>Gets the key length in bytes.</summary>
    public int Length => _material.Length;

    /// <summary>
    /// Gets the key material.
    /// </summary>
    /// <remarks>
    /// A span rather than an array, so a caller cannot keep a reference that outlives the
    /// <c>using</c> block and read it after <see cref="Dispose"/> has wiped it.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The key has been disposed.</exception>
    public ReadOnlySpan<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _material;
        }
    }

    /// <summary>Copies key material into a new pinned, wipeable buffer.</summary>
    /// <param name="material">The key bytes. The caller remains responsible for their own copy.</param>
    /// <returns>A new key.</returns>
    public static DataKey CopyFrom(ReadOnlySpan<byte> material)
    {
        ArgumentOutOfRangeException.ThrowIfZero(material.Length);

        byte[] pinned = GC.AllocateUninitializedArray<byte>(material.Length, pinned: true);
        material.CopyTo(pinned);

        return new DataKey(pinned);
    }

    /// <summary>Generates a fresh key from the platform CSPRNG.</summary>
    /// <param name="lengthInBytes">The key length. 32 unless there is a reason.</param>
    /// <returns>A new key.</returns>
    public static DataKey Generate(int lengthInBytes = 32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lengthInBytes, 16);

        byte[] pinned = GC.AllocateUninitializedArray<byte>(lengthInBytes, pinned: true);
        RandomNumberGenerator.Fill(pinned);

        return new DataKey(pinned);
    }

    /// <summary>Wipes the key material. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Not Array.Clear. CryptographicOperations.ZeroMemory is documented not to be optimised away
        // by the JIT, which an ordinary write to memory that is never read again legitimately can be.
        CryptographicOperations.ZeroMemory(_material);
    }

    /// <summary>Returns a redacted placeholder. NEVER the key material.</summary>
    /// <returns>The literal string <c>DataKey[REDACTED]</c>.</returns>
    public override string ToString() => "DataKey[REDACTED]";
}

/// <summary>
/// A freshly generated data key, in both the forms the caller needs.
/// </summary>
/// <param name="Plaintext">The usable key. The caller owns it and must dispose it.</param>
/// <param name="Wrapped">The encrypted key, which is the only form that may be persisted.</param>
/// <param name="KekId">Which key encryption key wrapped it. Needed to unwrap it later.</param>
/// <remarks>
/// Both forms come back from one call because generating a key and wrapping it is a single
/// operation on the KMS side - and because an API that returned only the plaintext would invite a
/// caller to persist THAT.
/// </remarks>
public sealed record GeneratedDataKey(DataKey Plaintext, byte[] Wrapped, string KekId) : IDisposable
{
    /// <summary>Disposes the plaintext key.</summary>
    public void Dispose() => Plaintext.Dispose();
}
