using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Delivery.Api.Configuration;

/// <summary>
/// Bearer token validation configuration for the authenticated customer-facing API.
/// </summary>
public sealed class JwtOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Jwt";

    /// <summary>Gets or sets the expected token issuer.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the expected audience.
    /// </summary>
    /// <remarks>
    /// Validated, not merely present. Skipping audience validation is what lets a token minted for
    /// a different application in the same identity provider be replayed against this one.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OpenID Connect authority used to discover signing keys.
    /// </summary>
    /// <remarks>
    /// Required outside Development. Discovery means keys rotate without a redeploy; a pinned
    /// static key means a rotation is an outage.
    /// </remarks>
    public string? Authority { get; set; }

    /// <summary>
    /// Gets or sets a symmetric signing key for local development only.
    /// </summary>
    /// <remarks>
    /// Supplied from the environment (<c>Jwt__DevelopmentSigningKey</c>), never committed.
    /// <see cref="JwtOptionsValidator"/> refuses to let a service start outside Development with
    /// this set, so a developer key cannot reach a deployed environment by being left in a config
    /// file - the failure mode that turns "convenient local setup" into "anyone can mint a token".
    /// </remarks>
    public string? DevelopmentSigningKey { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether metadata retrieval requires HTTPS. Only ever false
    /// in Development, and <see cref="JwtOptionsValidator"/> enforces that.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Gets or sets the permitted clock skew when validating token lifetimes, in seconds.</summary>
    /// <remarks>
    /// The framework default is five minutes, which is generous for a system where a stolen token
    /// is a real threat. Thirty seconds covers ordinary NTP drift and nothing else.
    /// </remarks>
    [Range(0, 300)]
    public int ClockSkewSeconds { get; set; } = 30;
}

/// <summary>
/// Cross-field validation for <see cref="JwtOptions"/> that data annotations cannot express.
/// </summary>
/// <remarks>
/// These rules exist because every one of them describes a real way a deployment goes wrong
/// quietly: a development signing key left in configuration, HTTPS metadata validation disabled
/// "temporarily", or an authority that was never set so tokens are validated against nothing.
/// Failing at startup makes all three loud.
/// </remarks>
public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    private readonly bool _isDevelopment;

    /// <summary>Initialises a new instance of the <see cref="JwtOptionsValidator"/> class.</summary>
    /// <param name="environment">The host environment.</param>
    public JwtOptionsValidator(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _isDevelopment = environment.IsDevelopment();
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (_isDevelopment)
        {
            if (string.IsNullOrWhiteSpace(options.Authority) && string.IsNullOrWhiteSpace(options.DevelopmentSigningKey))
            {
                failures.Add(
                    "Jwt:Authority or Jwt:DevelopmentSigningKey must be set. Without one of them there is no key to validate tokens against.");
            }

            if (!string.IsNullOrWhiteSpace(options.DevelopmentSigningKey) && options.DevelopmentSigningKey.Length < 32)
            {
                failures.Add("Jwt:DevelopmentSigningKey must be at least 32 characters to satisfy HMAC-SHA256 key length requirements.");
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(options.DevelopmentSigningKey))
            {
                failures.Add(
                    "Jwt:DevelopmentSigningKey is set outside Development. A symmetric key in configuration means anyone who can read configuration can mint a valid token.");
            }

            if (string.IsNullOrWhiteSpace(options.Authority))
            {
                failures.Add("Jwt:Authority is required outside Development so that signing keys are discovered and can rotate without a redeploy.");
            }

            if (!options.RequireHttpsMetadata)
            {
                failures.Add("Jwt:RequireHttpsMetadata must be true outside Development; plaintext metadata retrieval is trivially spoofable.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
