using System.Security.Cryptography;
using StatementDelivery.Domain.Identifiers;

namespace StatementDelivery.Crypto.Keys;

/// <summary>
/// Maps a customer to one of a fixed number of cohort key encryption keys.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ CHANGING <see cref="CohortCount"/> AFTER ANY CEK EXISTS IS AN UNRECOVERABLE DATA-LOSS EVENT.
/// </para>
/// <para>
/// Read that again, because nothing else in this repository is quite as unforgiving. A customer's
/// CEK is wrapped by the cohort KEK this function selects. Change the modulus and most customers
/// map to a DIFFERENT cohort, whose KEK cannot unwrap the CEK that was written under the old one.
/// The CEK is then gone; every statement that customer ever had is permanently unreadable; and no
/// backup helps, because the wrapped bytes in the backup are wrapped under a key the new mapping
/// will never select. It is indistinguishable from crypto-erasing millions of customers by
/// accident, and it would be discovered one support ticket at a time.
/// </para>
/// <para>
/// Which is why it is a <c>const</c>, not configuration, and why
/// <c>CohortAssignment_CountIsPinned</c> asserts its exact value. Changing it must be a deliberate
/// act with a failing test attached, never a config edit that ships on a Friday. If the count ever
/// genuinely needs to change, the migration is: introduce a SECOND mapping, re-wrap every CEK under
/// the new cohort while both KEK sets exist, then retire the old one. That is a project, not a
/// constant.
/// </para>
/// <para>
/// The mapping is over SHA-256 rather than over the raw UUID bytes. UUIDv7 leads with a
/// millisecond timestamp, so its high bytes are near-identical for customers created in the same
/// window and its low bytes are the random ones - taking either end directly would produce a
/// distribution that reflects sign-up patterns rather than an even spread. Hashing first removes
/// any such structure, which is what makes an even distribution provable rather than hoped for.
/// </para>
/// </remarks>
public static class CohortAssignment
{
    /// <summary>
    /// The number of cohort key encryption keys. PINNED. See the remarks on the class.
    /// </summary>
    /// <remarks>
    /// 1,024 is where two costs cross. Cohort KEKs are customer-managed KMS keys at roughly one
    /// dollar each per month, so this is about $1,024/month - visible but unremarkable. Fewer keys
    /// would save nothing worth having; more would multiply a bill that buys nothing extra, because
    /// the granularity that actually matters for erasure is the per-customer CEK sitting underneath.
    /// </remarks>
    public const int CohortCount = 1024;

    /// <summary>
    /// Returns the cohort for a customer. Stable for all time.
    /// </summary>
    /// <param name="id">The customer.</param>
    /// <param name="cohortCount">
    /// The cohort count. Defaulted so that callers never pass it; the parameter exists only so the
    /// distribution can be tested at other sizes.
    /// </param>
    /// <returns>The cohort index, in <c>[0, cohortCount)</c>.</returns>
    public static short ForCustomer(CustomerId id, int cohortCount = CohortCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cohortCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cohortCount, short.MaxValue);

        // Big-endian, canonical RFC 4122 byte order - the same choice, for the same reason, as the
        // AAD construction. Guid.ToByteArray()'s platform layout would make this mapping depend on
        // the endianness of whatever machine happened to run it, and a mapping that differs between
        // machines is the data-loss event described above happening quietly.
        Span<byte> bytes = stackalloc byte[16];
        _ = id.Value.TryWriteBytes(bytes, bigEndian: true, out _);

        Span<byte> hash = stackalloc byte[32];
        _ = SHA256.HashData(bytes, hash);

        // Four bytes of digest is 4.29 billion values against 1,024 buckets: the modulo bias is
        // roughly one part in four million, which is far below any distribution test and irrelevant
        // to a load-spreading decision.
        uint value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash[..4]);

        return (short)(value % (uint)cohortCount);
    }

    /// <summary>
    /// The key management service identifier for a cohort.
    /// </summary>
    /// <param name="cohort">The cohort index.</param>
    /// <returns>An alias of the form <c>alias/statement-cek-0000</c>.</returns>
    /// <remarks>
    /// An ALIAS, not a key id. Aliases survive key rotation, and a rotation that changed every
    /// stored identifier would be a rewrite of the whole <c>customer_key</c> table.
    /// </remarks>
    public static string KekIdFor(short cohort)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cohort);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"alias/statement-cek-{cohort:D4}");
    }
}
