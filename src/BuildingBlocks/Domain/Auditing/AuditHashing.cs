using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StatementDelivery.Domain.Exceptions;

namespace StatementDelivery.Domain.Auditing;

/// <summary>
/// The definition of the audit hash chain: chain assignment, genesis, canonical form, and hash.
/// </summary>
/// <remarks>
/// <para>
/// This type IS the specification. If it changes, every previously written record stops verifying,
/// so treat it as frozen once the first record exists. A change would need a format version column
/// and a verifier that can compute both.
/// </para>
/// <para>
/// It lives in the domain, and uses only the base class library, because the hash definition is a
/// business rule about evidence rather than a storage detail. Putting it next to the SQL would
/// make the rule look like an implementation choice of one adapter.
/// </para>
/// </remarks>
public static class AuditHashing
{
    /// <summary>The default number of independent chains.</summary>
    /// <remarks>
    /// SIXTEEN, because a hash chain requires strict ordering, ordering requires serialisation, and
    /// serialisation means a lock. ONE global chain would put a single hot row in front of every
    /// write path in the system - at ~470 million events a year, that row IS the throughput
    /// ceiling. Sixteen independent chains give sixteen independent locks.
    /// <para>
    /// What is given up: total ordering ACROSS chains. What is kept - and it is the property that
    /// actually matters - is that no record can be deleted or altered without detection.
    /// See docs/adr/0010-sharded-audit-hash-chains.md.
    /// </para>
    /// </remarks>
    public const int DefaultChainCount = 16;

    /// <summary>
    /// The field delimiter in the canonical form: ASCII 0x1F, UNIT SEPARATOR.
    /// </summary>
    /// <remarks>
    /// NOT a comma, and NOT a pipe. With a printable delimiter, a field value containing that
    /// character forges a field boundary: an actor id of <c>"a,b"</c> and the pair
    /// <c>("a", "b")</c> produce the same canonical string and therefore the same hash, so one can
    /// be substituted for the other without breaking the chain. The unit separator is not valid in
    /// any of these fields, and <see cref="Canonicalise"/> rejects any value containing it rather
    /// than escaping it - escaping would reintroduce the ambiguity one level down.
    /// </remarks>
    public const char FieldDelimiter = '\u001F';

    private const string GenesisPrefix = "statement-delivery:audit:chain:";

    /// <summary>
    /// Computes the genesis hash that seeds a chain.
    /// </summary>
    /// <remarks>
    /// NOT ZEROS, and the difference matters. With a shared genesis, record 1 of chain 3 and
    /// record 1 of chain 7 hash over the same predecessor, so a record can be lifted from one chain
    /// and replayed into another and both chains still verify. A chain-specific genesis makes the
    /// hashes fail to line up, which is exactly the detection this exists for.
    /// </remarks>
    /// <param name="chainId">The chain.</param>
    /// <returns>The 32-byte genesis hash.</returns>
    public static byte[] Genesis(short chainId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            GenesisPrefix + chainId.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Chooses the chain for an entry, from the subject entity.
    /// </summary>
    /// <remarks>
    /// BY ENTITY, NOT ROUND-ROBIN. Every event about a given statement must land in the same chain,
    /// so verifying one statement's history is a single-chain walk rather than an N-chain merge -
    /// and so that a gap in one statement's history is visible as a chain break rather than being
    /// spread across sixteen chains where nothing looks wrong.
    /// <para>
    /// Statement first, then customer, then a constant. FNV-1a rather than <c>Guid.GetHashCode</c>
    /// because the latter is not stable across processes or runs: the same statement would land in
    /// different chains after a restart, and its history would fragment.
    /// </para>
    /// </remarks>
    /// <param name="statementId">The statement subject, if any.</param>
    /// <param name="customerId">The customer subject, used when there is no statement.</param>
    /// <param name="chainCount">How many chains exist.</param>
    /// <returns>The chain identifier.</returns>
    public static short AssignChain(Guid? statementId, Guid? customerId, int chainCount = DefaultChainCount)
    {
        if (chainCount is < 1 or > short.MaxValue)
        {
            throw new InvariantViolationException(
                $"Chain count must be between 1 and {short.MaxValue}, got {chainCount.ToString(CultureInfo.InvariantCulture)}.");
        }

        Guid subject = statementId ?? customerId ?? Guid.Empty;
        return (short)(StableHash(subject) % (uint)chainCount);
    }

    /// <summary>
    /// FNV-1a over the big-endian bytes of a <see cref="Guid"/>.
    /// </summary>
    /// <remarks>
    /// Big-endian, so the result does not depend on .NET's mixed-endian in-memory layout. This is a
    /// distribution function, not a security primitive: the inputs are server-generated UUIDv7
    /// values, so an attacker cannot choose which chain to load.
    /// </remarks>
    private static uint StableHash(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!value.TryWriteBytes(bytes, bigEndian: true, out _))
        {
            throw new InvariantViolationException("Failed to read identifier bytes for chain assignment.");
        }

        const uint OffsetBasis = 2166136261;
        const uint Prime = 16777619;

        uint hash = OffsetBasis;
        foreach (byte b in bytes)
        {
            hash ^= b;
            hash *= Prime;
        }

        return hash;
    }

    /// <summary>
    /// Produces the canonical string a record's hash is computed over.
    /// </summary>
    /// <remarks>
    /// Field order is fixed and must never change. Nulls become the empty string rather than being
    /// omitted, so that a present-but-empty field and an absent one are the same - which is safe
    /// only because the delimiter count stays constant either way.
    /// </remarks>
    /// <param name="chainId">The chain.</param>
    /// <param name="chainSeq">The position within the chain.</param>
    /// <param name="entry">The record.</param>
    /// <returns>The canonical string.</returns>
    /// <exception cref="InvariantViolationException">A field contains the delimiter.</exception>
    public static string Canonicalise(short chainId, long chainSeq, AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string[] fields =
        [
            chainId.ToString(CultureInfo.InvariantCulture),
            chainSeq.ToString(CultureInfo.InvariantCulture),
            entry.Id.Value.ToString("D"),
            entry.StatementId?.Value.ToString("D") ?? string.Empty,
            entry.CustomerId?.Value.ToString("D") ?? string.Empty,
            entry.ActorType,
            entry.ActorId ?? string.Empty,
            entry.Action,
            entry.Outcome,
            entry.DenialReasonCode ?? string.Empty,
            entry.SourceIp ?? string.Empty,
            entry.UserAgentHash ?? string.Empty,

            // Round-trip "O" in UTC, TRUNCATED TO MICROSECONDS. Two rules, each learned the hard
            // way. UTC: a local-time stamp hashes differently on two servers in different zones for
            // the same instant. Microseconds: .NET carries 100 ns ticks but timestamptz stores
            // microseconds, so hashing full tick precision meant the verifier - which rebuilds the
            // entry from the STORED value - recomputed a different canonical string for roughly
            // nine records in ten. The chain must hash what the evidence can actually retain.
            TruncateToMicroseconds(entry.OccurredAt.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture),
            CanonicalJson(entry.Context),
        ];

        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i].Contains(FieldDelimiter, StringComparison.Ordinal))
            {
                // Rejected, not escaped. Escaping moves the ambiguity rather than removing it.
                throw new InvariantViolationException(
                    $"Audit field {i.ToString(CultureInfo.InvariantCulture)} contains the unit separator, which would forge a field boundary in the canonical form.");
            }
        }

        return string.Join(FieldDelimiter, fields);
    }

    /// <summary>
    /// Truncates to microseconds - the precision the canonical form commits to.
    /// </summary>
    /// <remarks>
    /// Public because the WRITER must persist exactly this value: PostgreSQL ROUNDS sub-microsecond
    /// input while the canonical form truncates, so inserting the raw tick value would store a
    /// timestamp one microsecond above the one that was hashed for half of the odd-tick cases.
    /// </remarks>
    /// <param name="value">The timestamp.</param>
    /// <returns>The value with sub-microsecond ticks removed.</returns>
    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % (TimeSpan.TicksPerMillisecond / 1000)));

    /// <summary>
    /// Computes a record's hash: <c>SHA256(previousHash || UTF8(canonical))</c>.
    /// </summary>
    /// <remarks>
    /// The previous hash is prepended as RAW BYTES rather than as hex inside the canonical string.
    /// Keeping it outside means the chain link cannot be affected by any field value, however
    /// crafted.
    /// </remarks>
    /// <param name="previousHash">The preceding record's hash, or the chain genesis.</param>
    /// <param name="canonical">The canonical form of this record.</param>
    /// <returns>The 32-byte hash.</returns>
    public static byte[] ComputeHash(ReadOnlySpan<byte> previousHash, string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);

        int canonicalByteCount = Encoding.UTF8.GetByteCount(canonical);
        byte[] buffer = new byte[previousHash.Length + canonicalByteCount];

        previousHash.CopyTo(buffer);
        _ = Encoding.UTF8.GetBytes(canonical, buffer.AsSpan(previousHash.Length));

        return SHA256.HashData(buffer);
    }

    /// <summary>
    /// Serialises the context bag deterministically: keys sorted, no whitespace, numbers normalised.
    /// </summary>
    /// <remarks>
    /// Without this the hash is meaningless. Two serialisers - or the same serialiser on two
    /// runtimes - can emit semantically identical JSON with different key order, and the record
    /// would fail to verify despite nothing having changed.
    /// <para>
    /// Object keys are sorted ordinally. ARRAY ORDER IS PRESERVED, because order is semantic in an
    /// array and sorting it would make two genuinely different contexts hash the same.
    /// </para>
    /// </remarks>
    /// <param name="context">The context bag.</param>
    /// <returns>The canonical JSON.</returns>
    public static string CanonicalJson(IReadOnlyDictionary<string, object?>? context)
    {
        if (context is null || context.Count == 0)
        {
            return "{}";
        }

        // Round-trip through JsonDocument first, so that whatever the caller put in the bag is
        // reduced to plain JSON values before canonicalisation. Canonicalising the CLR objects
        // directly would make the output depend on their runtime types.
        string raw = JsonSerializer.Serialize(context, ContextSerializerOptions);
        using var document = JsonDocument.Parse(raw);

        var builder = new StringBuilder();
        WriteCanonical(document.RootElement, builder);
        return builder.ToString();
    }

    private static readonly JsonSerializerOptions ContextSerializerOptions = new()
    {
        WriteIndented = false,
    };

    private static void WriteCanonical(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                bool firstProperty = true;

                foreach (JsonProperty property in element.EnumerateObject()
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty)
                    {
                        builder.Append(',');
                    }

                    firstProperty = false;
                    WriteJsonString(property.Name, builder);
                    builder.Append(':');
                    WriteCanonical(property.Value, builder);
                }

                builder.Append('}');
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                bool firstItem = true;

                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    WriteCanonical(item, builder);
                }

                builder.Append(']');
                break;

            case JsonValueKind.String:
                WriteJsonString(element.GetString() ?? string.Empty, builder);
                break;

            case JsonValueKind.Number:
                builder.Append(NormaliseNumber(element));
                break;

            case JsonValueKind.True:
                builder.Append("true");
                break;

            case JsonValueKind.False:
                builder.Append("false");
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                builder.Append("null");
                break;
        }
    }

    /// <summary>
    /// Normalises a JSON number so that 1, 1.0 and 1e0 produce identical output.
    /// </summary>
    private static string NormaliseNumber(JsonElement element)
    {
        if (element.TryGetInt64(out long integral))
        {
            return integral.ToString(CultureInfo.InvariantCulture);
        }

        if (element.TryGetDecimal(out decimal exact))
        {
            string text = exact.ToString(CultureInfo.InvariantCulture);

            // decimal preserves scale, so 1.500m formats as "1.500". Strip it: the value is the
            // same number and the hash must agree.
            if (text.Contains('.', StringComparison.Ordinal))
            {
                text = text.TrimEnd('0').TrimEnd('.');
            }

            return text.Length == 0 ? "0" : text;
        }

        // Beyond decimal range. "R" round-trips, which is the best determinism available here.
        return element.GetDouble().ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Writes a JSON string with a fixed, minimal escape set.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than delegating to a serialiser, because the canonical form must not
    /// change if a future runtime alters its default escaping policy. Only the characters JSON
    /// REQUIRES to be escaped are escaped; everything else, including non-ASCII, is emitted
    /// literally and carried by UTF-8.
    /// </remarks>
    private static void WriteJsonString(string value, StringBuilder builder)
    {
        builder.Append('"');

        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
