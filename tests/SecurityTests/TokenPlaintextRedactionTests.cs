using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Shouldly;
using StatementDelivery.ServiceDefaults;
using StatementDelivery.ServiceDefaults.Logging;
using Xunit;

namespace SecurityTests;

/// <summary>
/// Captures everything an exporter would receive, so a test can assert on the raw strings.
/// </summary>
/// <typeparam name="T">The telemetry type.</typeparam>
internal sealed class CapturingExporter<T> : BaseExporter<T>
    where T : class
{
    public List<T> Exported { get; } = [];

    public override ExportResult Export(in Batch<T> batch)
    {
        foreach (T item in batch)
        {
            Exported.Add(item);
        }

        return ExportResult.Success;
    }
}

/// <summary>
/// Proof that the plaintext download token never reaches a log, a span or the console.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS THE MOST COMMONLY MISSED CONTROL IN A SYSTEM OF THIS SHAPE. A perfect token scheme is
/// worthless if the token lands in a log aggregator with ninety-day retention and broad read
/// access - at that point the credential's ten-minute lifetime protects nothing, because the
/// aggregator kept it long after the token expired and the audit trail says nobody read it.
/// </para>
/// <para>
/// These tests drive the REAL redaction components - the same processors the OpenTelemetry pipeline
/// installs and the same console formatter the host uses - and assert on the raw exported strings.
/// A test that asserted the components were merely registered would pass against a processor that
/// does nothing.
/// </para>
/// </remarks>
public sealed class TokenPlaintextRedactionTests
{
    /// <summary>
    /// A realistic token: 43 base64url characters, exactly what the issue endpoint returns.
    /// </summary>
    /// <remarks>
    /// Fixed rather than random so a failure names the exact string that leaked, and distinctive
    /// enough that a substring match cannot succeed by accident.
    /// </remarks>
    private const string Plaintext = "Zx7Qn2LrT8vWpK4mHs1BdJgYcE6aNfUiO0RxV3tPqLw";

    private const string Url = "https://downloads.example.com/v1/d/" + Plaintext;

    private const string Path = "/v1/d/" + Plaintext;

    [Fact]
    public void TokenPlaintext_NeverAppearsInAnyLogOrTraceOutput()
    {
        var logs = new CapturingExporter<LogRecord>();
        var spans = new CapturingExporter<Activity>();

        // The same processor the host installs, followed by an exporter that keeps everything. If
        // the processor stopped redacting, the plaintext would be sitting in these lists.
        using TracerProvider tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(nameof(TokenPlaintextRedactionTests))
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new RedactingActivityProcessor())
            .AddProcessor(new SimpleActivityExportProcessor(spans))
            .Build();

        using ILoggerFactory factory = LoggerFactory.Create(builder =>
            builder.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
                options.AddProcessor(new RedactingLogProcessor());
                options.AddProcessor(new SimpleLogRecordExportProcessor(logs));
            }));

        ILogger logger = factory.CreateLogger("Download.Gateway.Downloads");
        using var source = new ActivitySource(nameof(TokenPlaintextRedactionTests));

        // ---- the success path -----------------------------------------------------------------
        using (Activity? activity = source.StartActivity("GET /v1/d/{token}"))
        {
            // Exactly the attributes ASP.NET Core instrumentation records automatically. Nobody has
            // to write a line of logging code for these to carry a live credential.
            activity?.SetTag("url.full", Url);
            activity?.SetTag("url.path", "/v1/d/" + Plaintext);
            activity?.SetTag("http.target", "/v1/d/" + Plaintext);
            activity?.SetTag("http.route", "/v1/d/{token}");
            activity?.SetTag("download.token", Plaintext);

            using (logger.BeginScope(new Dictionary<string, object?> { ["request.path"] = "/v1/d/" + Plaintext }))
            {
#pragma warning disable CA1848, CA1873 // The careless-caller path is exactly what is under test.
                logger.LogInformation("Redeemed {Url} for customer", Url);
                logger.LogInformation("Streaming statement from {Path}", Path);
#pragma warning restore CA1848, CA1873
            }
        }

        // ---- the failure path -----------------------------------------------------------------
        using (Activity? activity = source.StartActivity("GET /v1/d/{token} (denied)"))
        {
            activity?.SetTag("url.full", Url);
            activity?.SetStatus(ActivityStatusCode.Error, "not found");

#pragma warning disable CA1848, CA1873
            logger.LogWarning(
                new InvalidOperationException("failed while reading " + Url),
                "Denied {Url}",
                Url);
#pragma warning restore CA1848, CA1873
        }

        tracer.ForceFlush();
        factory.Dispose();

        logs.Exported.ShouldNotBeEmpty("the log pipeline must have captured something for this to prove anything");
        spans.Exported.ShouldNotBeEmpty("the trace pipeline must have captured something for this to prove anything");

        foreach (LogRecord record in logs.Exported)
        {
            Render(record).ShouldNotContain(Plaintext);
        }

        foreach (Activity span in spans.Exported)
        {
            Render(span).ShouldNotContain(Plaintext);
        }

        // And the redaction actually fired, rather than the strings never arriving at all.
        spans.Exported
            .SelectMany(static span => span.TagObjects)
            .Select(static tag => Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty)
            .ShouldContain(static value => value.Contains(SensitiveDataRedactor.RedactedMarker, StringComparison.Ordinal));
    }

    [Fact]
    public void TokenPlaintext_NeverAppearsInConsoleOutput()
    {
        // The console is a SEPARATE provider. RedactingLogProcessor never sees it, so without
        // RedactingConsoleFormatter a token in a log message would reach container stdout - and
        // from there a shipper, an index, and months of retention - completely unredacted.
        var options = new ConsoleFormatterOptions { IncludeScopes = true, UseUtcTimestamp = true };
        using var formatter = new RedactingConsoleFormatter(new StaticOptionsMonitor(options));
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        var state = new List<KeyValuePair<string, object?>>
        {
            new("Url", Url),
            new("token", Plaintext),
            new("dek", new byte[] { 1, 2, 3 }),
            new("customerId", 42),
            new("{OriginalFormat}", "Redeemed {Url}"),
        };

        var entry = new LogEntry<List<KeyValuePair<string, object?>>>(
            LogLevel.Information,
            "Download.Gateway.Downloads",
            new EventId(1, "Redeemed"),
            state,
            new InvalidOperationException("failed while reading " + Url),
            static (_, _) => "Redeemed " + Url);

        formatter.Write(entry, new SingleScopeProvider("/v1/d/" + Plaintext), output);

        string written = output.ToString();

        written.ShouldNotBeNullOrWhiteSpace();
        written.ShouldNotContain(Plaintext);
        written.ShouldContain(SensitiveDataRedactor.RedactedMarker);

        // Non-sensitive fields survive, or the formatter is redacting by destroying the log.
        written.ShouldContain("customerId");
        written.ShouldContain("42");
        written.ShouldContain("Download.Gateway.Downloads");
    }

    [Fact]
    public void ProblemDetailsInstance_IsRedacted()
    {
        // `instance` defaults to the request path, and on the redemption route the request path IS
        // the credential. Every 404, 409, 410 and 429 the gateway returns would otherwise echo the
        // token back inside an RFC 9457 document - which is then logged by whatever client receives
        // it, with none of this repository's redaction anywhere near it.
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        _ = builder.AddProblemDetailsHandling();

        using IHost host = builder.Build();

        Microsoft.AspNetCore.Http.ProblemDetailsOptions options =
            host.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.ProblemDetailsOptions>>().Value;

        options.CustomizeProblemDetails.ShouldNotBeNull(
            "problem details must be customised, or there is nothing redacting `instance`");

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = Path;

        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails { Status = 404 };
        options.CustomizeProblemDetails!(new Microsoft.AspNetCore.Http.ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
        });

        problem.Instance.ShouldNotBeNull();
        problem.Instance!.ShouldNotContain(Plaintext);
        problem.Instance.ShouldBe("/v1/d/" + SensitiveDataRedactor.RedactedMarker);
    }

    [Fact]
    public void SensitiveNames_CoverTheTypesThatNowExist()
    {
        // Rules written in Prompt 1 against data that did not yet exist, re-verified against the
        // real names Prompt 3 produces. A rule that matches nothing looks exactly like one that
        // works, and this is the difference.
        foreach (string name in new[]
        {
            "token", "Token", "download.token", "download_token", "url-token",
            "secret", "TokenSecret", "token.secret", "plaintext", "dek", "kek", "password",
        })
        {
            SensitiveDataRedactor.IsSensitiveName(name)
                .ShouldBeTrue($"'{name}' must be treated as sensitive");
        }

        // And ordinary names are not swallowed - blunt is fine, indiscriminate is not.
        foreach (string name in new[] { "customerId", "statement.id", "http.route", "tokenizer" })
        {
            SensitiveDataRedactor.IsSensitiveName(name)
                .ShouldBeFalse($"'{name}' must not be redacted");
        }
    }

    [Fact]
    public void RedactDownloadPath_CoversEveryFormTheUrlArrivesIn()
    {
        foreach (string value in new[]
        {
            Url,
            "/v1/d/" + Plaintext,
            "GET /v1/d/" + Plaintext + " HTTP/1.1",
            "System.IO.IOException: failed reading /v1/d/" + Plaintext,
            "/V1/D/" + Plaintext,
            "https://x/v1/d/" + Plaintext + "?utm=1",
        })
        {
            SensitiveDataRedactor.RedactDownloadPath(value)!
                .ShouldNotContain(Plaintext, Case.Sensitive, $"'{value}' leaked the token");
        }
    }

    private static string Render(LogRecord record)
    {
        var parts = new List<string?> { record.Body, record.FormattedMessage, record.Exception?.ToString() };

        if (record.Attributes is not null)
        {
            parts.AddRange(record.Attributes.Select(static attribute =>
                attribute.Key + "=" + Convert.ToString(attribute.Value, CultureInfo.InvariantCulture)));
        }

        return string.Join('\n', parts.Where(static part => part is not null));
    }

    private static string Render(Activity span)
    {
        var parts = new List<string> { span.DisplayName, span.StatusDescription ?? string.Empty };

        parts.AddRange(span.TagObjects.Select(static tag =>
            tag.Key + "=" + Convert.ToString(tag.Value, CultureInfo.InvariantCulture)));

        parts.AddRange(span.Events.SelectMany(static evt =>
            evt.Tags.Select(static tag => tag.Key + "=" + Convert.ToString(tag.Value, CultureInfo.InvariantCulture))));

        return string.Join('\n', parts);
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<ConsoleFormatterOptions>
    {
        public StaticOptionsMonitor(ConsoleFormatterOptions value) => CurrentValue = value;

        public ConsoleFormatterOptions CurrentValue { get; }

        public ConsoleFormatterOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ConsoleFormatterOptions, string?> listener) => null;
    }

    private sealed class SingleScopeProvider : IExternalScopeProvider
    {
        private readonly string _path;

        public SingleScopeProvider(string path) => _path = path;

        public void ForEachScope<TState>(Action<object?, TState> callback, TState state) =>
            callback(new List<KeyValuePair<string, object?>> { new("request.path", _path) }, state);

        public IDisposable Push(object? state) => NullScope.Instance;

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
