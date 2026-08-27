using Delivery.Api.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace SecurityTests;

/// <summary>
/// The JWT configuration guard.
/// </summary>
/// <remarks>
/// Each rule below describes a real way a deployment goes wrong QUIETLY: a development signing key
/// left in configuration, HTTPS metadata validation switched off "temporarily", or an authority
/// that was never set so tokens are validated against nothing. None of them produce a visible
/// symptom - the service starts and happily accepts tokens. Failing startup is what makes them
/// loud.
/// </remarks>
public sealed class JwtOptionsValidatorTests
{
    private static JwtOptionsValidator ValidatorFor(string environmentName)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        return new JwtOptionsValidator(environment);
    }

    private static JwtOptions Valid() => new()
    {
        Issuer = "https://issuer.example.com",
        Audience = "statement-delivery-api",
        Authority = "https://issuer.example.com",
        RequireHttpsMetadata = true,
    };

    [Fact]
    public void Production_RejectsADevelopmentSigningKey()
    {
        // THE ONE THAT MATTERS MOST. A symmetric key in configuration means anyone who can read
        // configuration can mint a token for any customer.
        JwtOptions options = Valid();
        options.DevelopmentSigningKey = "a-symmetric-key-that-should-never-ship-here";

        ValidateOptionsResult result = ValidatorFor(Environments.Production).Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotBeNull().ShouldContain("DevelopmentSigningKey");
    }

    [Fact]
    public void Production_RequiresAnAuthority()
    {
        // Without discovery there is no key set to validate against, and no way to rotate keys
        // without a redeploy.
        JwtOptions options = Valid();
        options.Authority = null;

        ValidateOptionsResult result = ValidatorFor(Environments.Production).Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotBeNull().ShouldContain("Authority");
    }

    [Fact]
    public void Production_RejectsPlaintextMetadataRetrieval()
    {
        // Fetching the signing keys over plaintext HTTP means anyone on the path chooses the keys.
        JwtOptions options = Valid();
        options.RequireHttpsMetadata = false;

        ValidateOptionsResult result = ValidatorFor(Environments.Production).Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotBeNull().ShouldContain("RequireHttpsMetadata");
    }

    [Fact]
    public void Production_AcceptsACorrectConfiguration() =>
        ValidatorFor(Environments.Production).Validate(null, Valid()).Succeeded.ShouldBeTrue();

    [Fact]
    public void Development_AcceptsASymmetricKeyOfSufficientLength()
    {
        JwtOptions options = Valid();
        options.Authority = null;
        options.RequireHttpsMetadata = false;
        options.DevelopmentSigningKey = "local-dev-signing-key-at-least-32-chars";

        ValidatorFor(Environments.Development).Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Development_RejectsAKeyTooShortForHmacSha256()
    {
        JwtOptions options = Valid();
        options.Authority = null;
        options.DevelopmentSigningKey = "too-short";

        ValidateOptionsResult result = ValidatorFor(Environments.Development).Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotBeNull().ShouldContain("32 characters");
    }

    [Fact]
    public void Development_RejectsHavingNeitherAnAuthorityNorAKey()
    {
        // Otherwise the service starts and validates every token against nothing at all.
        JwtOptions options = Valid();
        options.Authority = null;
        options.DevelopmentSigningKey = null;

        ValidatorFor(Environments.Development).Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void ClockSkew_DefaultsFarBelowTheFrameworkDefault()
    {
        // The framework default is five minutes. In a system where a stolen token is a live threat,
        // five extra minutes of validity after expiry is five minutes too many.
        new JwtOptions().ClockSkewSeconds.ShouldBeLessThanOrEqualTo(60);
    }
}
