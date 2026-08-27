using Shouldly;
using StatementDelivery.ServiceDefaults.Logging;
using Xunit;

namespace UnitTests.Observability;

/// <summary>
/// Tests for the telemetry redaction rules.
/// </summary>
/// <remarks>
/// These exist BEFORE the data they protect. Nothing in the repository issues a download token or
/// holds a key yet, and that is precisely why the rules and their tests are here: redaction that
/// arrives after the feature is redaction that already leaked, into a log store with months of
/// retention.
/// </remarks>
public sealed class SensitiveDataRedactorTests
{
    [Theory]
    [InlineData("token")]
    [InlineData("Token")]
    [InlineData("TOKEN")]
    [InlineData("dek")]
    [InlineData("kek")]
    [InlineData("password")]
    [InlineData("download.token")]
    [InlineData("statement_DEK")]
    [InlineData("db:password")]
    [InlineData("wrapped-kek")]
    [InlineData("http/password")]
    public void IsSensitiveName_MatchesAnySegmentOfTheKey(string name) =>
        SensitiveDataRedactor.IsSensitiveName(name).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("customerId")]
    [InlineData("statement.id")]
    [InlineData("url.path")]
    [InlineData("tokenizer")]
    [InlineData("passwordless")]
    public void IsSensitiveName_LeavesUnrelatedKeysAlone(string? name) =>
        SensitiveDataRedactor.IsSensitiveName(name).ShouldBeFalse();

    [Theory]
    [InlineData("/v1/d/abc123", "/v1/d/[REDACTED]")]
    [InlineData("/V1/D/abc123", "/V1/D/[REDACTED]")]
    [InlineData("https://dl.example.com/v1/d/9f8e7d6c5b4a", "https://dl.example.com/v1/d/[REDACTED]")]
    [InlineData("/v1/d/abc123?x=1", "/v1/d/[REDACTED]?x=1")]
    [InlineData("/v1/d/abc/def", "/v1/d/[REDACTED]")]
    [InlineData("GET /v1/d/secret 200", "GET /v1/d/[REDACTED] 200")]
    public void RedactDownloadPath_ReplacesEverythingAfterTheDownloadPrefix(string input, string expected) =>
        SensitiveDataRedactor.RedactDownloadPath(input).ShouldBe(expected);

    [Theory]
    [InlineData("/v1/statements")]
    [InlineData("/health/ready")]
    [InlineData("/ping")]
    [InlineData("")]
    public void RedactDownloadPath_LeavesOtherPathsUnchanged(string input) =>
        SensitiveDataRedactor.RedactDownloadPath(input).ShouldBe(input);

    [Fact]
    public void RedactDownloadPath_RedactsEveryOccurrenceInOneString()
    {
        // Exception messages and formatted log lines can carry the same URL more than once. A rule
        // that redacts only the first is a rule that leaks the second.
        const string Message = "retry /v1/d/aaa failed, retry /v1/d/bbb failed";

        SensitiveDataRedactor.RedactDownloadPath(Message)
            .ShouldBe("retry /v1/d/[REDACTED] failed, retry /v1/d/[REDACTED] failed");
    }

    [Fact]
    public void Redact_ReplacesTheValueWhenTheKeyIsSensitive() =>
        SensitiveDataRedactor.Redact("download.token", "a-real-token-value")
            .ShouldBe(SensitiveDataRedactor.RedactedMarker);

    [Fact]
    public void Redact_ReplacesTheValueRegardlessOfItsType() =>
        SensitiveDataRedactor.Redact("dek", new byte[] { 1, 2, 3 })
            .ShouldBe(SensitiveDataRedactor.RedactedMarker);

    [Fact]
    public void Redact_StillScansTheValueWhenTheKeyIsInnocuous() =>
        SensitiveDataRedactor.Redact("url.full", "https://dl.example.com/v1/d/leaky")
            .ShouldBe("https://dl.example.com/v1/d/[REDACTED]");

    [Fact]
    public void Redact_PassesThroughValuesThatMatchNothing() =>
        SensitiveDataRedactor.Redact("customerId", 42).ShouldBe(42);
}
