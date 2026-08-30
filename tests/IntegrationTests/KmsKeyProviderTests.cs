using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Shouldly;
using StatementDelivery.Crypto.Keys;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// Detects whether a KMS endpoint has been configured for this run.
/// </summary>
/// <remarks>
/// The same shape as <see cref="DockerAvailability"/>, and for the same reason: a suite that
/// silently needs cloud credentials is green on the machine that has them and red everywhere else,
/// which trains people to ignore red.
/// </remarks>
public static class AwsKmsAvailability
{
    /// <summary>Environment variable naming the KMS endpoint. Point it at LocalStack or leave it unset.</summary>
    public const string EndpointVariable = "STATEMENT_DELIVERY_KMS_ENDPOINT";

    /// <summary>Environment variable naming the cohort key to exercise.</summary>
    public const string KeyIdVariable = "STATEMENT_DELIVERY_KMS_KEY_ID";

    /// <summary>The reason shown when these tests are skipped.</summary>
    public const string SkipReason =
        "Requires a KMS endpoint. Set STATEMENT_DELIVERY_KMS_ENDPOINT (a LocalStack URL or empty for real AWS) "
        + "and STATEMENT_DELIVERY_KMS_KEY_ID to run these.";

    /// <summary>Gets a value indicating whether a key id was supplied.</summary>
    /// <remarks>
    /// The KEY ID is what gates, not the endpoint: an empty endpoint is meaningful (it means real
    /// AWS, resolved through the ambient credential chain), whereas without a key id there is
    /// nothing to call.
    /// </remarks>
    public static bool IsConfigured { get; } =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeyIdVariable));

    /// <summary>Gets the configured endpoint, or null for real AWS.</summary>
    public static string? Endpoint => Environment.GetEnvironmentVariable(EndpointVariable);

    /// <summary>Gets the configured key id.</summary>
    public static string KeyId => Environment.GetEnvironmentVariable(KeyIdVariable) ?? string.Empty;
}

/// <summary>
/// <see cref="AwsKmsKeyProvider"/> against a real key management service.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ EXCLUDED FROM THE DEFAULT CI RUN by <c>[Trait("Category","RequiresAws")]</c>. The workflow
/// appends <c>--filter-not-trait "Category=RequiresAws"</c> to every <c>dotnet test</c> invocation.
/// </para>
/// <para>
/// WHY THESE EXIST AT ALL, given they almost never run: <see cref="LocalKeyProvider"/> is what the
/// whole test suite exercises, and it shares nothing with the production provider except an
/// interface. Every property that matters about KMS - that the encryption context is authenticated,
/// that a ciphertext blob is opaque, that the plaintext arrives in a form we can wipe - is a
/// property of the SERVICE, and no local double can tell you whether we got it right. A provider
/// with no test against the real thing is a provider whose first real invocation is in production.
/// </para>
/// <para>
/// Point them at LocalStack for a free run:
/// </para>
/// <code>
/// docker run -d -p 4566:4566 localstack/localstack
/// aws --endpoint-url http://localhost:4566 kms create-key
/// aws --endpoint-url http://localhost:4566 kms create-alias \
///     --alias-name alias/statement-cek-0000 --target-key-id &lt;key-id&gt;
/// STATEMENT_DELIVERY_KMS_ENDPOINT=http://localhost:4566 \
/// STATEMENT_DELIVERY_KMS_KEY_ID=alias/statement-cek-0000 \
/// dotnet test --project tests/IntegrationTests/IntegrationTests.csproj -- --filter-trait "Category=RequiresAws"
/// </code>
/// </remarks>
[Trait("Category", "RequiresAws")]
public sealed class KmsKeyProviderTests
{
    [Fact(SkipUnless = nameof(AwsKmsAvailability.IsConfigured), SkipType = typeof(AwsKmsAvailability), Skip = AwsKmsAvailability.SkipReason)]
    public async Task GenerateDataKey_ThenUnwrap_RoundTrips()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using AwsKmsKeyProvider provider = CreateProvider();

        using GeneratedDataKey generated = await provider
            .GenerateDataKeyAsync(AwsKmsAvailability.KeyId, cancellationToken).ConfigureAwait(true);

        byte[] plaintext = generated.Plaintext.Span.ToArray();

        plaintext.Length.ShouldBe(32, "AES-256 only; there is no 128-bit option to negotiate down to");
        generated.KekId.ShouldBe(AwsKmsAvailability.KeyId);

        // THE CIPHERTEXT BLOB MUST NOT CONTAIN THE KEY. Obvious, and worth asserting: it is the one
        // failure that would make every other test in this file pass while the system stored
        // plaintext keys in a column called wrapped_dek.
        generated.Wrapped.Length.ShouldBeGreaterThan(plaintext.Length);
        IndexOf(generated.Wrapped, plaintext).ShouldBe(-1, "the wrapped blob must not contain the plaintext key");

        using DataKey unwrapped = await provider
            .UnwrapAsync(AwsKmsAvailability.KeyId, generated.Wrapped, cancellationToken).ConfigureAwait(true);

        unwrapped.Span.ToArray().ShouldBe(plaintext);
    }

    [Fact(SkipUnless = nameof(AwsKmsAvailability.IsConfigured), SkipType = typeof(AwsKmsAvailability), Skip = AwsKmsAvailability.SkipReason)]
    public async Task Unwrap_UnderADifferentKekId_Fails()
    {
        // ⚠ WHAT THIS DOES AND DOES NOT PROVE, stated because the obvious reading is wrong.
        //
        // AwsKmsKeyProvider derives BOTH the DecryptRequest KeyId AND the encryption context from
        // the same kekId argument, so passing a different one changes both at once. This therefore
        // proves that a blob is not portable between cohort identifiers - which is the property the
        // system actually relies on - but it does NOT isolate the encryption context as the
        // mechanism, and it would still pass if the context were dropped entirely.
        //
        // Isolating the context would need a provider that took the two separately, which would mean
        // widening the port for a test. Not worth it: the context is belt to the KeyId's braces, and
        // the assertion below is on the outcome that matters.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using AwsKmsKeyProvider provider = CreateProvider();

        using GeneratedDataKey generated = await provider
            .GenerateDataKeyAsync(AwsKmsAvailability.KeyId, cancellationToken).ConfigureAwait(true);

        _ = await Should.ThrowAsync<Exception>(() =>
            provider.UnwrapAsync(AwsKmsAvailability.KeyId + "-wrong", generated.Wrapped, cancellationToken))
            .ConfigureAwait(true);
    }

    [Fact(SkipUnless = nameof(AwsKmsAvailability.IsConfigured), SkipType = typeof(AwsKmsAvailability), Skip = AwsKmsAvailability.SkipReason)]
    public async Task GenerateDataKey_TwiceProducesDifferentKeys()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using AwsKmsKeyProvider provider = CreateProvider();

        using GeneratedDataKey first = await provider
            .GenerateDataKeyAsync(AwsKmsAvailability.KeyId, cancellationToken).ConfigureAwait(true);
        using GeneratedDataKey second = await provider
            .GenerateDataKeyAsync(AwsKmsAvailability.KeyId, cancellationToken).ConfigureAwait(true);

        first.Plaintext.Span.ToArray().ShouldNotBe(second.Plaintext.Span.ToArray());
        first.Wrapped.ShouldNotBe(second.Wrapped);
    }

    [Fact(SkipUnless = nameof(AwsKmsAvailability.IsConfigured), SkipType = typeof(AwsKmsAvailability), Skip = AwsKmsAvailability.SkipReason)]
    public async Task WrappedBlob_SurvivesTheDatabaseEnvelopeFloor()
    {
        // V013 requires octet_length(wrapped_cek) >= 40 and V013 requires the same of wrapped_dek.
        // A KMS blob is far longer than that, but the constraint is written against OUR envelope
        // format - so it is worth proving the production provider's output also clears the floor
        // rather than discovering at deployment that the schema rejects every real key.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using AwsKmsKeyProvider provider = CreateProvider();

        using GeneratedDataKey generated = await provider
            .GenerateDataKeyAsync(AwsKmsAvailability.KeyId, cancellationToken).ConfigureAwait(true);

        generated.Wrapped.Length.ShouldBeGreaterThan(40);
    }

    private static AwsKmsKeyProvider CreateProvider() => new(Options.Create(new KmsKeyProviderOptions
    {
        ServiceUrl = AwsKmsAvailability.Endpoint,
        Region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-east-1",
        AccessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"),
        SecretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"),
    }));

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }
}
