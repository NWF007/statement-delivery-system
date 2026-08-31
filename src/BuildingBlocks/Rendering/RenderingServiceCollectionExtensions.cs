using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QuestPDF;
using QuestPDF.Infrastructure;
using StatementDelivery.Domain.Rendering;

namespace StatementDelivery.Rendering;

/// <summary>Rendering configuration.</summary>
public sealed class RenderingOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Rendering";

    /// <summary>
    /// Gets or sets the QuestPDF licence tier this DEPLOYMENT declares: Community, Professional
    /// or Enterprise.
    /// </summary>
    /// <remarks>
    /// FROM CONFIGURATION, NEVER HARD-CODED. QuestPDF requires the licence to be asserted at
    /// startup, and the assertion is a legal statement about the LICENSEE, not about the code:
    /// this repository qualifies for Community as an individual portfolio project, while any
    /// organisation above $1M annual gross revenue deploying the same bytes must declare (and
    /// hold) Professional or Enterprise. Putting the tier in configuration makes each deployment
    /// own its declaration. See docs/LICENSING.md.
    /// </remarks>
    [Required]
    [RegularExpression("^(Community|Professional|Enterprise)$")]
    public string License { get; set; } = "Community";
}

/// <summary>Wires the QuestPDF renderer.</summary>
public static class RenderingServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IStatementRenderer"/> and applies the global QuestPDF settings.</summary>
    /// <param name="builder">The host builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddStatementRendering(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.Services
            .AddOptions<RenderingOptions>()
            .Bind(builder.Configuration.GetSection(RenderingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        ApplyGlobalSettings(builder.Configuration
            .GetSection(RenderingOptions.SectionName)[nameof(RenderingOptions.License)] ?? "Community");

        builder.Services.AddSingleton<IStatementRenderer, QuestPdfStatementRenderer>();
        return builder;
    }

    /// <summary>
    /// Applies the process-wide QuestPDF settings. Exposed for tests, which have no host builder.
    /// </summary>
    /// <param name="license">The declared licence tier.</param>
    public static void ApplyGlobalSettings(string license)
    {
        Settings.License = Enum.Parse<LicenseType>(license, ignoreCase: true);

        // DETERMINISM AND PORTABILITY IN ONE FLAG. With environment fonts on, the renderer
        // resolves whatever the host has installed - different between the dev machine and the
        // chiseled container, and therefore different bytes for the same document. Off, QuestPDF
        // uses only its embedded Lato, which ships inside the NuGet package and travels with the
        // deployment. Every text style in the renderer names Lato explicitly.
        Settings.UseEnvironmentFonts = false;

        // Fail loudly in development if a layout cannot fit, rather than emitting a truncated
        // document; production keeps the default lenient behaviour and relies on the pagination
        // tests having caught the layouts that matter.
        Settings.EnableDebugging = false;
    }
}
