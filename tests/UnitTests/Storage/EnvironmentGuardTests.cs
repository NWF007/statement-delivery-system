using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using StatementDelivery.Crypto;
using StatementDelivery.ServiceDefaults.Storage;
using Xunit;

namespace UnitTests.Storage;

/// <summary>
/// The two settings that are safe in Development and dangerous everywhere else.
/// </summary>
/// <remarks>
/// <para>
/// Both are irreversible in one direction. A GOVERNANCE object lock cannot be tightened after the
/// fact - the objects are written, and their retention says what it says. A statement encrypted
/// under a key derived from a configuration secret cannot be un-encrypted under a real one without
/// re-writing every object.
/// </para>
/// <para>
/// So neither is left to a deployment checklist. Startup refuses, and these tests are what stop the
/// refusal being quietly removed - a guard with no test is a guard somebody deletes to get a
/// deployment out.
/// </para>
/// </remarks>
public sealed class EnvironmentGuardTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void ObjectLock_RefusesGovernance_OutsideDevelopment(string environment)
    {
        // The failure this prevents is silent and permanent in the worst direction: the service
        // starts green and writes seven years of BYPASSABLE retention onto records ADR-0022 calls
        // regulatory. Nothing downstream notices, because a GOVERNANCE lock looks exactly like a
        // COMPLIANCE one right up until the day somebody deletes an object.
        HostApplicationBuilder builder = CreateBuilder(environment, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ObjectStorage:BucketName"] = "statements",
            ["ObjectStorage:Lock:Mode"] = "GOVERNANCE",
        });

        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => builder.AddEncryptedContentStore());

        error.Message.ShouldContain("COMPLIANCE");
        error.Message.ShouldContain(environment);
    }

    [Fact]
    public void ObjectLock_AllowsGovernance_InDevelopment()
    {
        // The asymmetry is the whole design, so it is asserted in both directions. A guard that
        // refused everywhere would be "safe" and would stop a developer deleting the ten thousand
        // test objects they are about to create, which is how the guard gets removed instead of
        // scoped.
        HostApplicationBuilder builder = CreateBuilder(Environments.Development, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ObjectStorage:BucketName"] = "statements",
            ["ObjectStorage:Lock:Mode"] = "GOVERNANCE",
        });

        Should.NotThrow(() => builder.AddEncryptedContentStore());
    }

    [Fact]
    public void ObjectLock_AllowsCompliance_Everywhere()
    {
        HostApplicationBuilder builder = CreateBuilder("Production", new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ObjectStorage:BucketName"] = "statements",
            ["ObjectStorage:Lock:Mode"] = "COMPLIANCE",
        });

        Should.NotThrow(() => builder.AddEncryptedContentStore());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void LocalKeyProvider_IsRefused_OutsideDevelopment(string environment)
    {
        // The sibling guard, asserted here beside the other one because they are the same rule about
        // two different settings - and because the pattern existing in two places and not a third is
        // exactly how the third gets forgotten.
        HostApplicationBuilder builder = CreateBuilder(environment, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Crypto:KeyProvider"] = "Local",
            ["Crypto:LocalKeys:MasterSecret"] = Convert.ToBase64String(new byte[32]),
        });

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => builder.AddCrypto());

        error.Message.ShouldContain(environment);
        error.Message.ShouldContain("Kms");
    }

    [Fact]
    public void LocalKeyProvider_IsAllowed_InDevelopment()
    {
        HostApplicationBuilder builder = CreateBuilder(Environments.Development, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Crypto:KeyProvider"] = "Local",
            ["Crypto:LocalKeys:MasterSecret"] = Convert.ToBase64String(new byte[32]),
        });

        Should.NotThrow(() => builder.AddCrypto());
    }

    [Fact]
    public void ObjectLock_RejectsAnAbsentMode_WhenTheHostIsBuilt()
    {
        // THE OTHER HALF OF THE GUARD, and it lives in a different mechanism. The environment check
        // above runs at registration; the [Required] and [RegularExpression] attributes run at
        // ValidateOnStart, which only fires when the host is actually built. A test that never
        // builds one asserts the first and silently assumes the second.
        //
        // Development is used deliberately: there the environment guard does NOT fire, so anything
        // that rejects an absent Mode here must be the options validation and nothing else.
        HostApplicationBuilder builder = CreateBuilder(Environments.Development, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ObjectStorage:BucketName"] = "statements",
        });

        _ = builder.AddEncryptedContentStore();

        // The store's collaborators, stubbed. Without them the container's own validation fails
        // first with "unable to resolve IAmazonS3" and the test would pass for entirely the wrong
        // reason - a green assertion proving only that the host could not be built at all.
        builder.Services.AddSingleton<Amazon.S3.IAmazonS3>(
            new Amazon.S3.AmazonS3Client("unused", "unused", new Amazon.S3.AmazonS3Config
            {
                ServiceURL = "http://localhost:1",
                ForcePathStyle = true,
            }));

        builder.Services.AddSingleton<StatementDelivery.Crypto.Keys.IDataKeyBroker, UnusedKeyBroker>();

        _ = Should.Throw<OptionsValidationException>(() =>
        {
            using IHost host = builder.Build();
            host.Start();
        });
    }

    /// <summary>A broker that is never called: this test never gets far enough to need a key.</summary>
    private sealed class UnusedKeyBroker : StatementDelivery.Crypto.Keys.IDataKeyBroker
    {
        public Task<StatementDelivery.Crypto.Keys.DataKeyLease> AcquireAsync(
            StatementDelivery.Domain.Identifiers.CustomerId customer, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<StatementDelivery.Crypto.Keys.DataKey> UnwrapDekAsync(
            StatementDelivery.Domain.Identifiers.CustomerId customer, ReadOnlyMemory<byte> wrappedDek, CancellationToken ct) =>
            throw new NotSupportedException();

        void StatementDelivery.Crypto.Keys.IDataKeyBroker.Evict(
            StatementDelivery.Domain.Identifiers.CustomerId customer)
        {
        }
    }

    [Fact]
    public void ObjectLockMode_HasNoDefault()
    {
        // Neither default is safe: COMPLIANCE puts the irreversible setting one forgotten config file
        // away from a laptop, GOVERNANCE puts the unenforceable one one forgotten config file away
        // from production. So there is none, and this asserts the absence rather than trusting the
        // attribute to stay on the property.
        new ObjectLockOptions().Mode.ShouldBeEmpty();
    }

    private static HostApplicationBuilder CreateBuilder(string environment, Dictionary<string, string?> settings)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment,
        });

        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }
}
