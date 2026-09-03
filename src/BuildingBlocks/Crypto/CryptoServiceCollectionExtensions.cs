using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StatementDelivery.Crypto.Framing;
using StatementDelivery.Crypto.Keys;

namespace StatementDelivery.Crypto;

/// <summary>Which key provider a service uses.</summary>
public enum KeyProviderKind
{
    /// <summary>Cohort keys derived from a configuration secret. DEVELOPMENT AND TEST ONLY.</summary>
    Local = 0,

    /// <summary>Cohort keys held in AWS KMS. The production choice.</summary>
    Kms = 1,
}

/// <summary>Registers the crypto building block.</summary>
public static class CryptoServiceCollectionExtensions
{
    /// <summary>Configuration key naming the provider.</summary>
    public const string ProviderConfigurationKey = "Crypto:KeyProvider";

    /// <summary>
    /// Adds the framed cipher, the key hierarchy and the data key cache.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// The local key provider was selected outside Development.
    /// </exception>
    public static IHostApplicationBuilder AddCrypto(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<CipherOptions>()
            .Bind(builder.Configuration.GetSection(CipherOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddOptions<DataKeyCacheOptions>()
            .Bind(builder.Configuration.GetSection(DataKeyCacheOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Factory registration, NOT AddSingleton<IStreamingCipher, FramedAeadCipher>():
        // the cipher has two public constructors (options for DI, int frame size for tests),
        // and DI activation refuses ambiguous constructors - which made every host that calls
        // AddCrypto fail at build validation. Found by the endpoint-enumeration test,
        // which is the first thing that ever actually CONSTRUCTED these hosts on this
        // Docker-less machine.
        builder.Services.AddSingleton<IStreamingCipher>(static sp =>
            new FramedAeadCipher(sp.GetRequiredService<IOptions<CipherOptions>>()));

        KeyProviderKind kind = Enum.TryParse(
            builder.Configuration[ProviderConfigurationKey], ignoreCase: true, out KeyProviderKind parsed)
            ? parsed
            : KeyProviderKind.Local;

        if (kind == KeyProviderKind.Local)
        {
            // REFUSED OUTSIDE DEVELOPMENT, at the composition root rather than inside the provider.
            //
            // The provider itself only warns, because a component that kills a process is a component
            // that makes an incident harder to read. The decision to REFUSE belongs here, where the
            // environment is known and where the failure is a startup configuration error with an
            // actionable message - which is a much better 3am than a service that came up and
            // encrypted a night of statements under a key sitting in a config map.
            if (!builder.Environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    LocalKeyProviderWarning.RefusalMessage(builder.Environment.EnvironmentName));
            }

            builder.Services.AddOptions<LocalKeyProviderOptions>()
                .Bind(builder.Configuration.GetSection(LocalKeyProviderOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            builder.Services.AddSingleton<IKeyProvider, LocalKeyProvider>();

            builder.Services.AddSingleton<IHostedService>(provider =>
                new LocalKeyProviderAnnouncement(
                    provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(LocalKeyProvider)),
                    builder.Environment.EnvironmentName));
        }
        else
        {
            builder.Services.AddOptions<KmsKeyProviderOptions>()
                .Bind(builder.Configuration.GetSection(KmsKeyProviderOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            builder.Services.AddSingleton<IKeyProvider, AwsKmsKeyProvider>();
        }

        builder.Services.AddSingleton<ICustomerKeyService, CustomerKeyService>();

        // ONE CACHE PER PROCESS, and a singleton is what makes that true. Registered as itself as
        // well as as the port so the container disposes it on shutdown, which is what zeroises the
        // keys it holds.
        builder.Services.AddSingleton<DataKeyCache>();
        builder.Services.AddSingleton<IDataKeyBroker>(provider => provider.GetRequiredService<DataKeyCache>());

        return builder;
    }

    private sealed class LocalKeyProviderAnnouncement(ILogger logger, string environment) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            LocalKeyProviderWarning.DevelopmentProviderInUse(logger, environment);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
