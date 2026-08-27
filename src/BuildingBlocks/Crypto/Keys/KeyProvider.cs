using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StatementDelivery.Crypto.Keys;

/// <summary>
/// Generates and unwraps data keys against a key encryption key that lives somewhere else.
/// </summary>
/// <remarks>
/// <para>
/// THE POINT OF THE PORT IS THAT THE KEK NEVER COMES BACK. Both methods return usable DATA keys and
/// neither returns the key that protected them, so no caller can accumulate the material that would
/// let it decrypt anything it was not handed. In the KMS implementation the KEK genuinely cannot
/// leave; the local implementation preserves the same shape so that code written against it does
/// not have to change when it does.
/// </para>
/// </remarks>
public interface IKeyProvider
{
    /// <summary>Generates a data key and returns it in both plaintext and wrapped form.</summary>
    /// <param name="kekId">The key encryption key to wrap under.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The plaintext key, the wrapped key, and the KEK that wrapped it.</returns>
    Task<GeneratedDataKey> GenerateDataKeyAsync(string kekId, CancellationToken ct);

    /// <summary>Unwraps a previously wrapped data key.</summary>
    /// <param name="kekId">The key encryption key it was wrapped under.</param>
    /// <param name="wrapped">The wrapped bytes as stored.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The plaintext key. The caller owns it and must dispose it.</returns>
    Task<DataKey> UnwrapAsync(string kekId, ReadOnlyMemory<byte> wrapped, CancellationToken ct);
}

/// <summary>Configuration for the development key provider.</summary>
public sealed class LocalKeyProviderOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Crypto:LocalKeys";

    /// <summary>
    /// Gets or sets the base64 master secret that every cohort KEK is derived from.
    /// </summary>
    /// <remarks>
    /// DEVELOPMENT AND TEST ONLY. Whoever holds this holds every key in the system - it is the
    /// single point of compromise that the real hierarchy exists to avoid.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string MasterSecret { get; set; } = string.Empty;
}

/// <summary>
/// Cohort KEKs derived from one configured master secret. DEVELOPMENT AND TEST ONLY.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS NOT A KEY MANAGEMENT SERVICE AND MUST NEVER BE MISTAKEN FOR ONE. Every cohort key is
/// derived by HKDF from a secret sitting in configuration, so the entire hierarchy collapses to
/// whoever can read that configuration - which is exactly the property a KMS exists to prevent.
/// </para>
/// <para>
/// It exists so that the local stack and the test suite exercise the SAME code paths as production:
/// the same wrapping, the same envelope, the same failure when the wrong cohort is used. A test
/// suite that skipped wrapping entirely would leave the most delicate part of the design unexercised
/// until the first deployment.
/// </para>
/// <para>
/// Registration logs a loud warning outside Development. It does not refuse to start, because a
/// deployment that dies with a dependency-injection error at 3am is harder to diagnose than one that
/// screams in its first log line - but see <see cref="CryptoServiceCollectionExtensions"/>, where
/// the composition root treats a non-Development environment as a configuration error.
/// </para>
/// <para>
/// The wrapped form is <c>version(1) || nonce(12) || ciphertext(32) || tag(16)</c> = 61 bytes. The
/// KEK identifier is the AAD, so a blob wrapped under one cohort fails to unwrap under another
/// rather than producing plausible-looking garbage.
/// </para>
/// </remarks>
public sealed class LocalKeyProvider : IKeyProvider
{
    private const int DataKeyLength = 32;

    private readonly byte[] _masterSecret;

    /// <summary>Initialises a new instance of the <see cref="LocalKeyProvider"/> class.</summary>
    /// <param name="options">Provider options.</param>
    public LocalKeyProvider(IOptions<LocalKeyProviderOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _masterSecret = Convert.FromBase64String(options.Value.MasterSecret);

        if (_masterSecret.Length < 32)
        {
            throw new InvalidOperationException(
                "Crypto:LocalKeys:MasterSecret must decode to at least 32 bytes.");
        }
    }

    /// <inheritdoc />
    public Task<GeneratedDataKey> GenerateDataKeyAsync(string kekId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kekId);
        ct.ThrowIfCancellationRequested();

        DataKey plaintext = DataKey.Generate(DataKeyLength);

        try
        {
            Span<byte> kek = stackalloc byte[32];
            DeriveKek(kekId, kek);

            try
            {
                byte[] wrapped = KeyWrap.Wrap(kek, plaintext.Span, kekId);
                return Task.FromResult(new GeneratedDataKey(plaintext, wrapped, kekId));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        catch
        {
            // The caller never receives the key, so nothing else will dispose it.
            plaintext.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public Task<DataKey> UnwrapAsync(string kekId, ReadOnlyMemory<byte> wrapped, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kekId);
        ct.ThrowIfCancellationRequested();

        Span<byte> kek = stackalloc byte[32];
        DeriveKek(kekId, kek);

        try
        {
            // The KEK identifier is the AAD, so a blob wrapped under cohort 7 FAILS here under
            // cohort 8 rather than yielding 32 bytes of plausible nonsense that would go on to
            // produce an unreadable statement with no obvious cause.
            return Task.FromResult(KeyWrap.Unwrap(kek, wrapped.Span, kekId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private void DeriveKek(string kekId, Span<byte> destination) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            _masterSecret,
            destination,
            salt: "statement-delivery/cohort-kek/v1"u8,
            info: Encoding.UTF8.GetBytes(kekId));
}

/// <summary>Configuration for the AWS KMS key provider.</summary>
public sealed class KmsKeyProviderOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Crypto:Kms";

    /// <summary>Gets or sets an explicit endpoint, for LocalStack. Empty uses the real service.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Gets or sets the region.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Gets or sets the access key. Local development only; see <see cref="ObjectStorageParity"/>.</summary>
    public string? AccessKey { get; set; }

    /// <summary>Gets or sets the secret key. Local development only.</summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// A note, not a setting: credentials follow the same rule as object storage.
    /// </summary>
    /// <remarks>
    /// Explicit keys are the local path. A deployed environment leaves them empty and lets the SDK
    /// resolve an instance role or workload identity, so there is no long-lived secret to leak.
    /// </remarks>
    public static string ObjectStorageParity => "See ObjectStorageOptions.AccessKey.";
}

/// <summary>
/// Cohort KEKs held in AWS KMS. The production provider.
/// </summary>
/// <remarks>
/// <para>
/// <c>GenerateDataKey</c> returns the plaintext and the ciphertext of a fresh data key in ONE call,
/// which is the whole point of the API: the KEK never leaves the service, and the caller never has
/// to hold it. <c>Decrypt</c> reverses it later.
/// </para>
/// <para>
/// THE ENCRYPTION CONTEXT IS NOT OPTIONAL. KMS authenticates it, so a ciphertext produced for one
/// cohort cannot be decrypted while claiming another - the same defence, at the key layer, that the
/// frame AAD provides at the data layer.
/// </para>
/// <para>
/// Integration tests against a real account are marked <c>[Trait("Category","RequiresAws")]</c> and
/// excluded from the default CI run, because a test suite that silently needs cloud credentials is
/// a test suite that is green on one machine and red everywhere else.
/// </para>
/// </remarks>
public sealed class AwsKmsKeyProvider : IKeyProvider, IDisposable
{
    private readonly Amazon.KeyManagementService.IAmazonKeyManagementService _kms;
    private readonly bool _ownsClient;

    /// <summary>Initialises a new instance of the <see cref="AwsKmsKeyProvider"/> class.</summary>
    /// <param name="options">KMS options.</param>
    public AwsKmsKeyProvider(IOptions<KmsKeyProviderOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        KmsKeyProviderOptions settings = options.Value;

        var config = new Amazon.KeyManagementService.AmazonKeyManagementServiceConfig
        {
            AuthenticationRegion = settings.Region,
        };

        if (string.IsNullOrWhiteSpace(settings.ServiceUrl))
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(settings.Region);
        }
        else
        {
            config.ServiceURL = settings.ServiceUrl;
        }

        _kms = string.IsNullOrWhiteSpace(settings.AccessKey) || string.IsNullOrWhiteSpace(settings.SecretKey)
            ? new Amazon.KeyManagementService.AmazonKeyManagementServiceClient(config)
            : new Amazon.KeyManagementService.AmazonKeyManagementServiceClient(
                settings.AccessKey, settings.SecretKey, config);

        _ownsClient = true;
    }

    /// <summary>Initialises a new instance of the <see cref="AwsKmsKeyProvider"/> class.</summary>
    /// <param name="kms">An externally owned client, for tests.</param>
    public AwsKmsKeyProvider(Amazon.KeyManagementService.IAmazonKeyManagementService kms)
    {
        _kms = kms;
        _ownsClient = false;
    }

    /// <inheritdoc />
    public async Task<GeneratedDataKey> GenerateDataKeyAsync(string kekId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kekId);

        Amazon.KeyManagementService.Model.GenerateDataKeyResponse response = await _kms
            .GenerateDataKeyAsync(
                new Amazon.KeyManagementService.Model.GenerateDataKeyRequest
                {
                    KeyId = kekId,
                    KeySpec = Amazon.KeyManagementService.DataKeySpec.AES_256,
                    EncryptionContext = EncryptionContextFor(kekId),
                },
                ct)
            .ConfigureAwait(false);

        byte[] plaintext = response.Plaintext.ToArray();

        try
        {
            return new GeneratedDataKey(DataKey.CopyFrom(plaintext), response.CiphertextBlob.ToArray(), kekId);
        }
        finally
        {
            // The SDK handed back a plain array. It is copied into a pinned, wipeable buffer above;
            // this wipes the SDK's copy so only the managed one survives this method.
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <inheritdoc />
    public async Task<DataKey> UnwrapAsync(string kekId, ReadOnlyMemory<byte> wrapped, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kekId);

        using var blob = new MemoryStream(wrapped.ToArray(), writable: false);

        Amazon.KeyManagementService.Model.DecryptResponse response = await _kms
            .DecryptAsync(
                new Amazon.KeyManagementService.Model.DecryptRequest
                {
                    KeyId = kekId,
                    CiphertextBlob = blob,
                    EncryptionContext = EncryptionContextFor(kekId),
                },
                ct)
            .ConfigureAwait(false);

        byte[] plaintext = response.Plaintext.ToArray();

        try
        {
            return DataKey.CopyFrom(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient)
        {
            _kms.Dispose();
        }
    }

    private static Dictionary<string, string> EncryptionContextFor(string kekId) =>
        new(StringComparer.Ordinal)
        {
            ["purpose"] = "statement-cek",
            ["kek"] = kekId,
        };
}

/// <summary>Startup warning for the development key provider.</summary>
public static partial class LocalKeyProviderWarning
{
    /// <summary>Logs the loud warning that the development provider is in use.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="environment">The environment name.</param>
    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Critical,
        Message = "LOCAL KEY PROVIDER IS ACTIVE IN ENVIRONMENT '{Environment}'. Every cohort key is "
                + "derived from a secret in configuration, so anything that can read configuration can "
                + "decrypt every statement in the system. This is a development-only component and must "
                + "never serve real customer data.")]
    public static partial void DevelopmentProviderInUse(ILogger logger, string environment);

    /// <summary>Formats the same warning for a startup exception message.</summary>
    /// <param name="environment">The environment name.</param>
    /// <returns>The message.</returns>
    public static string RefusalMessage(string environment) => string.Create(
        CultureInfo.InvariantCulture,
        $"Crypto:KeyProvider is 'Local' in environment '{environment}'. The local provider derives every cohort key from a configuration secret and must not run outside Development. Set Crypto:KeyProvider to 'Kms'.");
}
