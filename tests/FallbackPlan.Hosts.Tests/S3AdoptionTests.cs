using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Adopting an archive from an S3-compatible bucket after the machine that
/// wrote it is gone (FR-DR-009, ADR-0061, ADR-0091 Amendment 1): every case
/// of <see cref="ObjectStoreAdoptionTests"/> against a bucket, read with the
/// access key the rebuilt service was given.
/// </summary>
[TestClass]
public sealed class S3AdoptionTests() : ObjectStoreAdoptionTests(StartStore())
{
    private S3CompatibleTestServer S3 => (S3CompatibleTestServer)Store;

    /// <inheritdoc />
    protected override string MissingCredentialWords => "no access key";

    /// <inheritdoc />
    protected override string EndpointAt(Uri origin) => origin.ToString();

    /// <inheritdoc />
    protected override DestinationConfiguration Declare(string endpoint) => new()
    {
        Id = CloudId,
        Name = Cloud,
        Kind = DestinationKind.S3,
        Endpoint = endpoint,
        Bucket = Namespace,
        Region = S3CompatibleTestServer.DefaultRegion,
        Prefix = Prefix,
    };

    /// <inheritdoc />
    protected override async Task StoreCredentialAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var envelope = WriteOnlyProvisioning.SealAccessKeySecret(
            await RecipientAsync(handler), Cloud, S3.AccessKeyId, S3.SecretAccessKey);
        var stored = await handler.ExecuteAsync(
            new SetDestinationCredentialsCommand(Cloud, S3.AccessKeyId, Convert.ToHexStringLower(envelope)), Timeout);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(stored, (stored as ServiceError)?.Message);
    }

    private static S3CompatibleTestServer StartStore()
    {
        var store = new S3CompatibleTestServer();
        store.CreateBucket(Namespace);
        return store;
    }
}
