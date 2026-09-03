using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace StatementDelivery.ServiceDefaults.Logging;

/// <summary>
/// Applies <see cref="SensitiveDataRedactor"/> to every log record on its way to an exporter.
/// </summary>
/// <remarks>
/// <para>
/// Redaction sits at the export boundary rather than at the call site because the call site is
/// exactly where it gets forgotten. A processor cannot be forgotten: every record produced by
/// every logger in the process passes through it, including records produced by framework code
/// nobody in this repository wrote.
/// </para>
/// <para>
/// This processor covers the OTLP pipeline ONLY - it sees records on their way to an exporter and
/// nothing else. The console sink is a separate provider and is covered by
/// <see cref="RedactingConsoleFormatter"/>, which was added once a real token existed, for
/// exactly this reason: "the console is only a development convenience" stopped being an
/// acceptable answer. Both paths are asserted in tests/SecurityTests.
/// </para>
/// </remarks>
public sealed class RedactingLogProcessor : BaseProcessor<LogRecord>
{
    /// <inheritdoc />
    public override void OnEnd(LogRecord data)
    {
        ArgumentNullException.ThrowIfNull(data);

        data.Body = SensitiveDataRedactor.RedactDownloadPath(data.Body);
        data.FormattedMessage = SensitiveDataRedactor.RedactDownloadPath(data.FormattedMessage);

        List<KeyValuePair<string, object?>>? exceptionAttributes = RedactException(data);

        IReadOnlyList<KeyValuePair<string, object?>>? attributes = data.Attributes;
        if (attributes is null || attributes.Count == 0)
        {
            if (exceptionAttributes is not null)
            {
                data.Attributes = exceptionAttributes;
            }

            return;
        }

        List<KeyValuePair<string, object?>>? replacement = null;

        for (int i = 0; i < attributes.Count; i++)
        {
            KeyValuePair<string, object?> attribute = attributes[i];
            object? redacted = SensitiveDataRedactor.Redact(attribute.Key, attribute.Value);

            if (ReferenceEquals(redacted, attribute.Value))
            {
                continue;
            }

            // Copy on first difference only. The overwhelming majority of records contain nothing
            // sensitive, and this runs on every single one of them.
            replacement ??= [.. attributes];
            replacement[i] = new KeyValuePair<string, object?>(attribute.Key, redacted);
        }

        if (exceptionAttributes is not null)
        {
            replacement ??= [.. attributes];
            replacement.AddRange(exceptionAttributes);
        }

        if (replacement is not null)
        {
            data.Attributes = replacement;
        }
    }

    /// <summary>
    /// Moves an exception off the record and onto redacted attributes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AN EXCEPTION MESSAGE IS A LOG LINE. <c>IOException: failed reading /v1/d/{token}</c> carries
    /// a live credential, and an exception object cannot be redacted in place - its message is
    /// immutable and its type must not be replaced with a stand-in that would break every consumer
    /// grouping by exception type.
    /// </para>
    /// <para>
    /// So the exception is detached and re-emitted as the three attributes the OTLP exporter would
    /// have derived from it anyway - <c>exception.type</c>, <c>exception.message</c> and
    /// <c>exception.stacktrace</c> - with the two string-valued ones redacted. Nothing is lost
    /// except the ability of a downstream processor to inspect the live object, and the type name
    /// survives intact.
    /// </para>
    /// </remarks>
    private static List<KeyValuePair<string, object?>>? RedactException(LogRecord data)
    {
        if (data.Exception is not { } exception)
        {
            return null;
        }

        string? message = SensitiveDataRedactor.RedactDownloadPath(exception.Message);
        string? stackTrace = SensitiveDataRedactor.RedactDownloadPath(exception.ToString());

        bool leaked =
            !string.Equals(message, exception.Message, StringComparison.Ordinal)
            || !string.Equals(stackTrace, exception.ToString(), StringComparison.Ordinal);

        if (!leaked)
        {
            // Nothing sensitive in it. Leave the live object attached, so downstream processors and
            // the exporter behave exactly as they would without this processor in the pipeline.
            return null;
        }

        data.Exception = null;

        return
        [
            new KeyValuePair<string, object?>("exception.type", exception.GetType().FullName),
            new KeyValuePair<string, object?>("exception.message", message),
            new KeyValuePair<string, object?>("exception.stacktrace", stackTrace),
        ];
    }
}

/// <summary>
/// Applies <see cref="SensitiveDataRedactor"/> to every span on its way to an exporter.
/// </summary>
/// <remarks>
/// Traces need this at least as much as logs do. The ASP.NET Core instrumentation records
/// <c>url.path</c> and <c>url.full</c> automatically, so a download-redemption request would
/// otherwise export a live single-use credential as a span attribute without anybody having
/// written a line of logging code.
/// </remarks>
public sealed class RedactingActivityProcessor : BaseProcessor<Activity>
{
    private static readonly string[] UrlTagNames =
    [
        "url.full",
        "url.path",
        "url.query",
        "http.url",
        "http.target",
        "http.route",
    ];

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);

        foreach (string tag in UrlTagNames)
        {
            if (data.GetTagItem(tag) is string value)
            {
                string? redacted = SensitiveDataRedactor.RedactDownloadPath(value);
                if (!string.Equals(redacted, value, StringComparison.Ordinal))
                {
                    _ = data.SetTag(tag, redacted);
                }
            }
        }

        // Materialise before mutating: SetTag while enumerating TagObjects is undefined.
        List<KeyValuePair<string, object?>>? sensitive = null;
        foreach (KeyValuePair<string, object?> tag in data.TagObjects)
        {
            if (SensitiveDataRedactor.IsSensitiveName(tag.Key))
            {
                sensitive ??= [];
                sensitive.Add(tag);
            }
        }

        if (sensitive is null)
        {
            return;
        }

        foreach (KeyValuePair<string, object?> tag in sensitive)
        {
            _ = data.SetTag(tag.Key, SensitiveDataRedactor.RedactedMarker);
        }
    }
}
