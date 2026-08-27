using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace StatementDelivery.ServiceDefaults.Logging;

/// <summary>
/// A structured JSON console formatter that applies <see cref="SensitiveDataRedactor"/> to every
/// field it writes.
/// </summary>
/// <remarks>
/// <para>
/// THIS REPLACES <c>AddJsonConsole</c>, AND THE REASON IS A REAL GAP RATHER THAN A PREFERENCE.
/// </para>
/// <para>
/// <see cref="RedactingLogProcessor"/> runs inside the OpenTelemetry pipeline and therefore only
/// sees records on their way to an OTLP exporter. The console provider is a separate sink: a log
/// line containing a download URL would reach container stdout - and from there a log shipper, an
/// index, and a retention period measured in months - completely untouched by it.
/// </para>
/// <para>
/// Classification-based redaction (<c>Microsoft.Extensions.Compliance</c>) does apply to every
/// provider, but only to parameters a developer remembered to tag. That is precisely the "the call
/// site is where it gets forgotten" failure this design rejects everywhere else.
/// </para>
/// <para>
/// So the console gets its own formatter, and it redacts unconditionally: the rendered message, the
/// exception text, every state value and every scope value. Over-redaction here costs an operator
/// one confusing log line. A gap costs a customer their statement.
/// </para>
/// </remarks>
public sealed class RedactingConsoleFormatter : ConsoleFormatter, IDisposable
{
    /// <summary>The formatter name, as referenced by <c>ConsoleLoggerOptions.FormatterName</c>.</summary>
    public const string FormatterName = "redacting-json";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,

        // Relaxed escaping so a '+' in a timestamp offset or a '/' in a category stays readable.
        // The encoder still escapes everything that would break the JSON or inject a control
        // character into a log stream.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IDisposable? _reload;
    private ConsoleFormatterOptions _options;

    /// <summary>Initialises a new instance of the <see cref="RedactingConsoleFormatter"/> class.</summary>
    /// <param name="options">Formatter options, monitored for change.</param>
    public RedactingConsoleFormatter(IOptionsMonitor<ConsoleFormatterOptions> options)
        : base(FormatterName)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.CurrentValue;
        _reload = options.OnChange(updated => _options = updated);
    }

    /// <inheritdoc />
    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        ArgumentNullException.ThrowIfNull(textWriter);

        string message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception) ?? string.Empty;

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();

            writer.WriteString("Timestamp", Timestamp());
            writer.WriteNumber("EventId", logEntry.EventId.Id);
            writer.WriteString("LogLevel", LevelName(logEntry.LogLevel));
            writer.WriteString("Category", logEntry.Category);

            // Redacted, always. This is the field a URL is most likely to appear in, because
            // interpolating a request path into a message is the most natural thing to write.
            writer.WriteString("Message", SensitiveDataRedactor.RedactDownloadPath(message));

            if (logEntry.Exception is not null)
            {
                // ToString(), not just Message: the stack trace is what makes an exception useful.
                // Redacted too, because the path that produced it is often in the message.
                writer.WriteString(
                    "Exception",
                    SensitiveDataRedactor.RedactDownloadPath(logEntry.Exception.ToString()));
            }

            WriteState(writer, logEntry.State);
            WriteScopes(writer, scopeProvider);

            writer.WriteEndObject();
        }

        textWriter.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <inheritdoc />
    public void Dispose() => _reload?.Dispose();

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Information",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Critical",
        _ => "None",
    };

    private static void WriteState<TState>(Utf8JsonWriter writer, TState state)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
        {
            return;
        }

        writer.WriteStartObject("State");
        foreach (KeyValuePair<string, object?> pair in values)
        {
            WriteRedacted(writer, pair.Key, pair.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteScopes(Utf8JsonWriter writer, IExternalScopeProvider? scopeProvider)
    {
        if (scopeProvider is null)
        {
            return;
        }

        writer.WriteStartArray("Scopes");
        scopeProvider.ForEachScope(
            static (scope, state) =>
            {
                if (scope is IReadOnlyList<KeyValuePair<string, object?>> values)
                {
                    state.WriteStartObject();
                    foreach (KeyValuePair<string, object?> pair in values)
                    {
                        WriteRedacted(state, pair.Key, pair.Value);
                    }

                    state.WriteEndObject();
                    return;
                }

                state.WriteStringValue(
                    SensitiveDataRedactor.RedactDownloadPath(scope?.ToString()) ?? string.Empty);
            },
            writer);
        writer.WriteEndArray();
    }

    private static void WriteRedacted(Utf8JsonWriter writer, string key, object? value)
    {
        // OriginalFormat holds the message TEMPLATE, so it carries placeholders rather than values -
        // but a template with a literal path baked into it would still leak, so it goes through
        // exactly the same redaction as every other state value.
        object? redacted = SensitiveDataRedactor.Redact(key, value);

        switch (redacted)
        {
            case null:
                writer.WriteNull(key);
                break;
            case string text:
                writer.WriteString(key, text);
                break;
            case bool flag:
                writer.WriteBoolean(key, flag);
                break;
            case int number:
                writer.WriteNumber(key, number);
                break;
            case long number:
                writer.WriteNumber(key, number);
                break;
            case double number:
                writer.WriteNumber(key, number);
                break;
            case decimal number:
                writer.WriteNumber(key, number);
                break;
            default:
                // Everything else is rendered as text and redacted again. A byte array or a wrapper
                // type must not reach the output simply because this switch did not anticipate it.
                writer.WriteString(
                    key,
                    SensitiveDataRedactor.RedactDownloadPath(
                        Convert.ToString(redacted, CultureInfo.InvariantCulture)));
                break;
        }
    }

    private string Timestamp()
    {
        DateTimeOffset now = _options.UseUtcTimestamp ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
        return now.ToString(
            _options.TimestampFormat ?? "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture);
    }
}
