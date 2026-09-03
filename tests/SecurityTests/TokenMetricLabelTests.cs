using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Download.Gateway.Downloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Shouldly;
using StatementDelivery.Domain.Auditing;
using Xunit;

namespace SecurityTests;

/// <summary>
/// Proof that the plaintext token can never become a metric label.
/// </summary>
/// <remarks>
/// <para>
/// The fifth of the five places the plaintext must never reach, and the one that is easiest to miss
/// because it looks harmless. A metric label is exported to the telemetry backend, indexed, kept for
/// as long as the retention policy says, and readable by everyone with a dashboard - which is a
/// larger audience than the database has and a longer life than the token's ten minutes.
/// </para>
/// <para>
/// Two tests, because either alone is weak. The first proves the instrument itself refuses an
/// unknown value; the second proves the call sites only ever pass classified reasons, so the
/// refusal is a backstop rather than the only thing standing in the way.
/// </para>
/// </remarks>
public sealed partial class TokenMetricLabelTests
{
    private const string Plaintext = "Zx7Qn2LrT8vWpK4mHs1BdJgYcE6aNfUiO0RxV3tPqLw";

    [Fact]
    public void TokenPlaintext_CanNeverBecomeAMetricLabel()
    {
        var captured = new List<KeyValuePair<string, object?>>();

        using ServiceProvider provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        using var metrics = new DownloadMetrics(provider.GetRequiredService<IMeterFactory>());

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, DownloadMetrics.MeterName, StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                captured.Add(tag);
            }
        });

        listener.Start();

        // Every reason the redemption path can produce...
        foreach (string reason in KnownReasons)
        {
            metrics.Denied(reason);
        }

        // ...and then the accident this exists to stop: the token itself, and a URL containing it.
        metrics.Denied(Plaintext);
        metrics.Denied("/v1/d/" + Plaintext);

        metrics.Incomplete();
        metrics.Completed(4096);

        listener.Dispose();

        captured.ShouldNotBeEmpty("the listener must have observed measurements for this to prove anything");

        foreach (KeyValuePair<string, object?> tag in captured)
        {
            string value = tag.Value?.ToString() ?? string.Empty;

            value.ShouldNotContain(Plaintext, Case.Sensitive, $"tag '{tag.Key}' carried the plaintext token");

            // Stronger than "does not contain the token": the label space is CLOSED. Anything not on
            // the known list - which a token never is - collapses to one constant.
            tag.Key.ShouldBe("reason");
            value.ShouldBeOneOf([.. KnownReasons, DownloadMetrics.UnclassifiedReason]);
        }

        captured.Count(static tag => string.Equals(
                tag.Value?.ToString(),
                DownloadMetrics.UnclassifiedReason,
                StringComparison.Ordinal))
            .ShouldBe(2, "both unrecognised values must have been replaced, not passed through");

        // The transfer counters carry no labels at all, so there is nothing there to leak.
        captured.Count.ShouldBe(KnownReasons.Length + 2);
    }

    [Fact]
    public void DeniedMetric_IsOnlyEverCalledWithAClassifiedReason()
    {
        // The instrument's closed set is a backstop. This is the primary control: every call site
        // passes either a DenialReason constant or the variable holding the diagnosis result. A
        // future edit that interpolates anything else fails here, at review time, rather than
        // silently landing in the telemetry backend.
        string source = RepositoryFiles.Read("src/Services/Download.Gateway/Downloads/DownloadEndpoints.cs");

        MatchCollection calls = DeniedCall().Matches(source);

        calls.Count.ShouldBeGreaterThan(0, "the denial metric must be emitted somewhere");

        foreach (Match call in calls)
        {
            string argument = call.Groups["arg"].Value.Trim();

            bool acceptable =
                argument.StartsWith("DenialReason.", StringComparison.Ordinal)
                || string.Equals(argument, "reason", StringComparison.Ordinal);

            acceptable.ShouldBeTrue(
                $"metrics.Denied({argument}) passes something other than a classified reason; "
                + "a metric label is one of the five places the plaintext must never reach");
        }

        // And `reason` itself is only ever the diagnosis result or a DenialReason constant. Asserted
        // as a WHITELIST rather than by banning suspicious substrings: a ban has to anticipate every
        // way the token could arrive, and a whitelist only has to name the two ways it may not.
        MatchCollection assignments = ReasonAssignment().Matches(source);
        assignments.Count.ShouldBeGreaterThan(0, "the denial reason must be computed somewhere");

        foreach (Match assignment in assignments)
        {
            string value = assignment.Groups["value"].Value;

            value.ShouldNotContain(
                "\"",
                Case.Sensitive,
                "a denial reason must be a DenialReason constant, never an inline string");

            (value.Contains("DiagnoseFailureAsync", StringComparison.Ordinal)
                || value.Contains("DenialReason.", StringComparison.Ordinal))
                .ShouldBeTrue($"`string reason = {value.Trim()}` is neither a diagnosis nor a constant");
        }
    }

    private static readonly string[] KnownReasons =
    [
        DenialReason.Consumed,
        DenialReason.Revoked,
        DenialReason.Expired,
        DenialReason.UnknownToken,
        DenialReason.MalformedToken,
        DenialReason.NotFound,
        DenialReason.NotOwner,
        DenialReason.SubjectMismatch,
        DenialReason.NoSubjectClaim,

        // Added when envelope decryption became a way a download can fail. A new denial reason that
        // is not on this list still reaches the metric - it just collapses to UNCLASSIFIED, so the
        // label space stays closed and no token can leak. But the reason then tells an operator
        // nothing, and DECRYPTION_FAILED is the one reason on this list that should page somebody.
        DenialReason.DecryptionFailed,
    ];

    [GeneratedRegex(@"\bmetrics\.Denied\((?<arg>[^)]*)\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DeniedCall();

    [GeneratedRegex(@"string reason\s*=\s*(?<value>[^;]*);", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ReasonAssignment();
}
