using System.ComponentModel.DataAnnotations;
using System.Diagnostics.Metrics;
using StatementDelivery.Domain.Auditing;

namespace Download.Gateway.Downloads;

/// <summary>Configuration for the redemption path.</summary>
public sealed class DownloadOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Download";

    /// <summary>
    /// Gets or sets the minimum wall-clock time any denial takes, in milliseconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every failure path is padded to at least this long, so that "unknown token" (one index probe)
    /// and "already consumed" (a probe plus a diagnosis query) do not take visibly different times.
    /// </para>
    /// <para>
    /// AN HONEST ASSESSMENT: this REDUCES timing leakage, it does not eliminate it. Padding to a
    /// floor still leaks through the tail, and a determined attacker with enough samples can
    /// recover signal from jitter. It is defence in depth, not the primary control.
    /// </para>
    /// <para>
    /// THE PRIMARY PROTECTION IS THAT ENUMERATION IS POINTLESS. Guessing a 256-bit token is not
    /// made feasible by knowing which failure occurred; the search space is 2^256 either way.
    /// Removing this padding would not create a practical attack. It is here because uniform-looking
    /// failures cost almost nothing and remove a whole class of question from a security review.
    /// </para>
    /// </remarks>
    [Range(0, 5000)]
    public int DenialFloorMilliseconds { get; set; } = 50;

    /// <summary>
    /// Gets or sets the streaming copy buffer size, in bytes.
    /// </summary>
    /// <remarks>
    /// Bounded and rented from the array pool, so memory use is a function of CONCURRENCY, not of
    /// statement size. 80 KB sits just under the 85,000-byte large object heap threshold: a bigger
    /// buffer would be allocated on the LOH, which is collected far less often and compacted almost
    /// never.
    /// </remarks>
    [Range(4096, 1_048_576)]
    public int StreamBufferBytes { get; set; } = 80 * 1024;

    /// <summary>Gets or sets the public base URL used to build download links.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:8082";
}

/// <summary>
/// Instruments for the delivery path.
/// </summary>
public sealed class DownloadMetrics : IDisposable
{
    /// <summary>The meter name. ServiceDefaults adds this to the OpenTelemetry pipeline.</summary>
    public const string MeterName = "StatementDelivery.Download";

    private readonly Meter _meter;
    private readonly Counter<long> _denied;
    private readonly Counter<long> _incomplete;
    private readonly Counter<long> _completed;
    private readonly Counter<long> _bytes;
    private readonly Counter<long> _decryptionFailures;
    private readonly Counter<long> _contentMissing;

    /// <summary>Initialises a new instance of the <see cref="DownloadMetrics"/> class.</summary>
    /// <param name="meterFactory">Meter factory from dependency injection.</param>
    public DownloadMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(MeterName);

        // Labelled by REASON, never by token. A metric label is exported to the telemetry backend
        // and would be exactly the "one more place the plaintext lives" this design forbids - but
        // the reason is safe and is what makes an alert on an UNKNOWN_TOKEN spike possible. That
        // spike is what a guessing attack looks like from the outside.
        _denied = _meter.CreateCounter<long>(
            "download_denied_total",
            unit: "{request}",
            description: "Redemption attempts refused, by internal denial reason. A rising UNKNOWN_TOKEN rate is enumeration.");

        // EMITTED NOW SO THE RANGE-REQUEST DECISION CAN BE REVISITED WITH DATA. Range requests are
        // unsupported because they conflict with single use; if this counter ever shows a material
        // rate, that trade-off deserves re-examination. See ADR-0016.
        _incomplete = _meter.CreateCounter<long>(
            "download_incomplete_total",
            unit: "{request}",
            description: "Transfers that ended before all bytes were sent. Evidence for whether resumable downloads are needed.");

        _completed = _meter.CreateCounter<long>(
            "download_completed_total",
            unit: "{request}",
            description: "Statements delivered in full.");

        _bytes = _meter.CreateCounter<long>(
            "download_bytes_total",
            unit: "By",
            description: "Bytes streamed to clients.");

        // ALERT ON ANY NON-ZERO VALUE. Not a rate, not a threshold, not a percentage of requests -
        // any value at all. Every other counter here measures something that happens in normal
        // operation and is interesting only when its rate moves. This one measures a stored object
        // that failed to authenticate, which means corruption, substitution or tampering. One
        // occurrence is an incident; waiting for a second is waiting to see whether it spreads.
        _decryptionFailures = _meter.CreateCounter<long>(
            "statement_decryption_failure_total",
            unit: "{failure}",
            description: "Objects that failed to decrypt. NOT a user error - corruption or tampering. Alert on any non-zero value.");

        // ALSO ALERT ON ANY NON-ZERO VALUE, for the same reason as the counter above: it cannot
        // happen during correct operation. A statement is marked AVAILABLE in the same statement
        // that records where its bytes live, so a row that is AVAILABLE with no readable object
        // means the database and the object store have diverged.
        //
        // THIS COUNTER PREDATES THE JOB THAT FORMALISES IT. The reconciliation sweep that came
        // later treats this divergence as its CHECK 1; a counter that only starts existing
        // alongside the job that reports it can never answer the first question anyone will
        // ask: "how long has this been happening?"
        _contentMissing = _meter.CreateCounter<long>(
            "statement_content_missing_total",
            unit: "{failure}",
            description: "AVAILABLE statements whose content could not be read. Database and object store have diverged. Alert on any non-zero value.");
    }

    /// <summary>
    /// The complete set of values that may ever become a <c>reason</c> label.
    /// </summary>
    /// <remarks>
    /// A CLOSED SET, CHECKED AT THE INSTRUMENT RATHER THAN AT THE CALL SITE. <see cref="Denied"/>
    /// takes a string, and a string parameter one call away from the redemption path is exactly the
    /// shape of accident that puts a live token into a metric label - where it is exported to the
    /// telemetry backend, indexed, and retained for months.
    /// </remarks>
    private static readonly HashSet<string> KnownReasons = new(StringComparer.Ordinal)
    {
        DenialReason.Consumed,
        DenialReason.Revoked,
        DenialReason.Expired,
        DenialReason.UnknownToken,
        DenialReason.MalformedToken,
        DenialReason.NotFound,
        DenialReason.NotOwner,
        DenialReason.SubjectMismatch,
        DenialReason.NoSubjectClaim,
        DenialReason.DecryptionFailed,
        DenialReason.StorageUnavailable,
        DenialReason.ContentUnavailable,
    };

    /// <summary>The label used when a caller passes something not on the known list.</summary>
    /// <remarks>
    /// Cardinality is the ordinary reason to bound a label; here it is the secondary one. The
    /// primary reason is that an unrecognised value might be the token, and a metric label is one of
    /// the five places this design says the plaintext must never reach.
    /// </remarks>
    public const string UnclassifiedReason = "UNCLASSIFIED";

    /// <summary>Records a refused redemption.</summary>
    /// <remarks>
    /// The reason is REPLACED, not rejected, when it is not recognised. Throwing here would turn a
    /// telemetry mistake into a failed download, and the metric is not worth an outage - but neither
    /// is it worth exporting an arbitrary caller-supplied string.
    /// </remarks>
    /// <param name="reason">The internal denial reason. Anything unrecognised becomes UNCLASSIFIED.</param>
    public void Denied(string reason) =>
        _denied.Add(
            1,
            new KeyValuePair<string, object?>(
                "reason",
                KnownReasons.Contains(reason) ? reason : UnclassifiedReason));

    /// <summary>
    /// Records an object that failed to decrypt, and denies it as well.
    /// </summary>
    /// <remarks>
    /// Both counters move: the denial counter so the reason breakdown stays complete, and the
    /// dedicated counter so the alert can be written against one unambiguous series rather than
    /// against a label filter somebody has to get right.
    /// </remarks>
    public void DecryptionFailed()
    {
        _decryptionFailures.Add(1);
        Denied(DenialReason.DecryptionFailed);
    }

    /// <summary>
    /// Records an AVAILABLE statement whose content could not be read, and denies it as well.
    /// </summary>
    /// <remarks>
    /// Two counters move, as with <see cref="DecryptionFailed"/>: the denial counter keeps the
    /// reason breakdown complete, and the dedicated counter gives the alert one unambiguous series.
    /// </remarks>
    /// <param name="reason">
    /// <see cref="DenialReason.StorageUnavailable"/> when the row carries no location, or
    /// <see cref="DenialReason.ContentUnavailable"/> when it carries one and the object is absent.
    /// </param>
    public void ContentMissing(string reason)
    {
        _contentMissing.Add(
            1,
            new KeyValuePair<string, object?>(
                "reason",
                KnownReasons.Contains(reason) ? reason : UnclassifiedReason));

        Denied(reason);
    }

    /// <summary>Records a transfer that did not finish.</summary>
    public void Incomplete() => _incomplete.Add(1);

    /// <summary>Records a completed transfer.</summary>
    /// <param name="bytes">Bytes sent.</param>
    public void Completed(long bytes)
    {
        _completed.Add(1);
        _bytes.Add(bytes);
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
