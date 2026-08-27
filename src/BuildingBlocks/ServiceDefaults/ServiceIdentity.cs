using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace StatementDelivery.ServiceDefaults;

/// <summary>
/// The four values every signal this service emits is stamped with.
/// </summary>
/// <remarks>
/// <para>
/// These map one-to-one onto the OpenTelemetry service resource attributes. Getting them right
/// once, centrally, is what makes a trace answerable without a deployment manifest to hand:
/// which service, which build, which instance, which environment.
/// </para>
/// <para>
/// The same values back the <c>/ping</c> endpoint, so what an operator sees over HTTP and what
/// the telemetry backend sees cannot drift apart.
/// </para>
/// </remarks>
/// <param name="Name">
/// Emitted as <c>service.name</c>. The logical service, stable across replicas and deploys.
/// </param>
/// <param name="Version">
/// Emitted as <c>service.version</c>. Read from the assembly informational version, so it is
/// whatever the build stamped rather than a hand-maintained constant that goes stale.
/// </param>
/// <param name="InstanceId">
/// Emitted as <c>service.instance.id</c>. The machine or pod name. This is what separates
/// "one replica is broken" from "the service is broken", and without it those two look identical.
/// </param>
/// <param name="Environment">
/// Emitted as <c>deployment.environment</c>, from ASPNETCORE_ENVIRONMENT.
/// </param>
public sealed record ServiceIdentity(string Name, string Version, string InstanceId, string Environment)
{
    /// <summary>
    /// Builds the identity from the entry assembly and the ambient environment.
    /// </summary>
    /// <param name="serviceName">The logical service name.</param>
    /// <param name="environmentName">The environment name.</param>
    /// <returns>The service identity.</returns>
    public static ServiceIdentity Create(string serviceName, string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        return new ServiceIdentity(
            serviceName,
            ResolveVersion(),
            System.Environment.MachineName,
            environmentName);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Reading version metadata must never be able to stop a service from starting.")]
    private static string ResolveVersion()
    {
        try
        {
            Assembly? entry = Assembly.GetEntryAssembly();
            string? informational = entry?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (string.IsNullOrWhiteSpace(informational))
            {
                return entry?.GetName().Version?.ToString() ?? "0.0.0";
            }

            // The SDK appends "+<commit sha>" to the informational version. Keep the semantic
            // part for display; the full string with the sha is what the build artefact carries.
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }
        catch (Exception)
        {
            return "0.0.0";
        }
    }
}
