using System.Text.RegularExpressions;

namespace StatementDelivery.ServiceDefaults.Logging;

/// <summary>
/// The redaction rules applied to every telemetry signal this platform emits.
/// </summary>
/// <remarks>
/// <para>
/// THE TOKEN NOW EXISTS, AND THESE RULES HAVE BEEN VERIFIED AGAINST IT. They were written in Prompt
/// 1, before there was anything to redact - redaction that arrives after the feature is redaction
/// that already leaked, into a log store with a retention period measured in months. Prompt 3 was
/// the moment the claim had to be turned into evidence, because a rule that matches nothing looks
/// exactly like a rule that works.
/// </para>
/// <para>
/// Verified against the real signals the redemption path produces, not against invented names:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>url.path</c> and <c>url.full</c> on the ASP.NET server span, which carry the request line and
/// therefore the plaintext token - caught by <see cref="RedactDownloadPath"/>.
/// </description></item>
/// <item><description>
/// The <c>token</c> route value, and any <c>*.token</c> attribute an enricher adds - caught by
/// <see cref="IsSensitiveName"/> segment matching.
/// </description></item>
/// <item><description>
/// <c>ProblemDetails.Instance</c> and the exception-handler log message, which default to the
/// request path - redacted at the source in GlobalExceptionHandler.
/// </description></item>
/// <item><description>
/// Log message bodies and formatted messages that embed a URL - redacted by
/// RedactingLogProcessor before any exporter sees them.
/// </description></item>
/// </list>
/// <para>
/// The evidence is in tests/SecurityTests: TokenPlaintextNeverLeaksTests drives a real redemption
/// through the real pipeline with an in-memory log provider and an in-memory OpenTelemetry
/// exporter, then asserts the plaintext appears in NEITHER. The remaining sinks - database columns,
/// metric labels, exception messages - have a test each.
/// </para>
/// <para>
/// Two rules, deliberately blunt. Blunt over-redaction costs an operator one debugging session.
/// A precise rule with a gap costs a customer their statement.
/// </para>
/// </remarks>
public static partial class SensitiveDataRedactor
{
    /// <summary>The marker substituted for redacted content.</summary>
    /// <remarks>
    /// A fixed marker rather than an empty string, so that a redacted field is visibly redacted
    /// rather than looking like a field that was never populated.
    /// </remarks>
    public const string RedactedMarker = "[REDACTED]";

    // "secret" and "plaintext" were added in Prompt 3 alongside TokenSecret: the type's own name is
    // the most likely thing to end up as an attribute key when somebody logs one by accident.
    private static readonly string[] SensitiveNames =
        ["token", "dek", "kek", "password", "secret", "plaintext"];
    private static readonly char[] NameSeparators = ['.', '_', ':', '-', '/'];

    /// <summary>
    /// Attribute and property keys whose values are always replaced, matched case-insensitively
    /// against any dot-, underscore-, colon-, dash- or slash-separated segment of the key.
    /// </summary>
    /// <remarks>
    /// Segment matching rather than exact matching, so <c>download.token</c>,
    /// <c>Statement_DEK</c> and <c>db.password</c> are all caught without anybody having to
    /// remember to add each variant.
    /// </remarks>
    /// <param name="name">The attribute or property name.</param>
    /// <returns><see langword="true"/> when the value must be redacted.</returns>
    public static bool IsSensitiveName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (Range segment in name.AsSpan().SplitAny(NameSeparators))
        {
            if (ContainsSensitiveWord(name.AsSpan()[segment]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits a separator-delimited segment further on camel-case boundaries and matches each word.
    /// </summary>
    /// <remarks>
    /// <c>TokenSecret</c> and <c>downloadToken</c> are single segments to a separator split, so
    /// whole-segment matching would miss both - and those are exactly the names a developer writes
    /// when they log the object rather than a field of it. Splitting on the case boundary catches
    /// them while leaving <c>tokenizer</c> and <c>customerId</c> alone, which a substring match
    /// would not.
    /// </remarks>
    /// <param name="segment">One separator-delimited segment of the name.</param>
    /// <returns><see langword="true"/> when any word in the segment is sensitive.</returns>
    private static bool ContainsSensitiveWord(ReadOnlySpan<char> segment)
    {
        int start = 0;

        for (int i = 1; i <= segment.Length; i++)
        {
            bool boundary = i == segment.Length
                || (char.IsUpper(segment[i]) && !char.IsUpper(segment[i - 1]))
                || (char.IsAsciiDigit(segment[i]) != char.IsAsciiDigit(segment[i - 1]));

            if (!boundary)
            {
                continue;
            }

            ReadOnlySpan<char> word = segment[start..i];
            foreach (string sensitive in SensitiveNames)
            {
                if (word.Equals(sensitive, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            start = i;
        }

        return false;
    }

    /// <summary>
    /// Replaces everything after a <c>/v1/d/</c> path prefix.
    /// </summary>
    /// <remarks>
    /// <c>/v1/d/{token}</c> is the public download-redemption route. The token IS the credential
    /// there - there is no bearer header to strip - so the URL itself is a secret. It arrives in
    /// access logs, in the <c>url.path</c> and <c>url.full</c> span attributes, and in exception
    /// messages, and every one of those is a place a valid credential must not be written down.
    /// </remarks>
    /// <param name="value">A URL, path, or a message that may embed one.</param>
    /// <returns>The value with any download path redacted.</returns>
    public static string? RedactDownloadPath(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return DownloadPath().Replace(value, "${prefix}" + RedactedMarker);
    }

    /// <summary>
    /// Redacts a request path for use where the path may be ECHOED BACK to a caller: the
    /// download rule first, then every GUID path segment.
    /// </summary>
    /// <remarks>
    /// ProblemDetails.Instance is the consumer. A denial that echoes the identifier it denies
    /// knowing is an existence oracle, and denial bodies must not vary by resource - but the
    /// route SHAPE stays, so an operator can still see which endpoint produced the document.
    /// </remarks>
    /// <param name="value">A URL path.</param>
    /// <returns>The path with the download tail and all GUID segments redacted.</returns>
    public static string? RedactPathIdentifiers(string? value)
    {
        string? redacted = RedactDownloadPath(value);
        return string.IsNullOrEmpty(redacted) ? redacted : GuidSegment().Replace(redacted, RedactedMarker);
    }

    /// <summary>
    /// Applies both rules to one telemetry attribute.
    /// </summary>
    /// <param name="key">The attribute key.</param>
    /// <param name="value">The attribute value.</param>
    /// <returns>The value to emit, redacted where a rule matched.</returns>
    public static object? Redact(string key, object? value)
    {
        if (IsSensitiveName(key))
        {
            return RedactedMarker;
        }

        return value is string text ? RedactDownloadPath(text) : value;
    }

    /// <summary>
    /// Matches the download route prefix and everything following it, up to a query or fragment.
    /// </summary>
    /// <summary>Matches a GUID wherever it appears as its own path segment.</summary>
    [GeneratedRegex(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex GuidSegment();

    [GeneratedRegex(
        @"(?<prefix>/v1/d/)[^\s?#]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex DownloadPath();
}
