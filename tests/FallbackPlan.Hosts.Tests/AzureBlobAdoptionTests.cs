using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Adopting an archive from an Azure Blob container after the machine that
/// wrote it is gone (FR-DR-009, ADR-0061, ADR-0093): every case of
/// <see cref="ObjectStoreAdoptionTests"/> against a container, read under a
/// shared access signature — the credential a rebuilt machine is most likely
/// to be handed, because it can be issued for the one container and expire.
/// </summary>
[TestClass]
public sealed class AzureBlobAdoptionTests() : ObjectStoreAdoptionTests(StartStore())
{
    private AzureBlobTestServer Azure => (AzureBlobTestServer)Store;

    /// <inheritdoc />
    protected override string MissingCredentialWords => "no account key or shared access signature";

    /// <inheritdoc />
    protected override string EndpointAt(Uri origin) =>
        $"{origin.GetLeftPart(UriPartial.Authority)}/{AzureBlobTestServer.DefaultAccount}";

    /// <inheritdoc />
    protected override DestinationConfiguration DeclareStore(string endpoint) => new()
    {
        Id = CloudId,
        Name = Cloud,
        Kind = DestinationKind.AzureBlob,
        Endpoint = endpoint,
        Account = AzureBlobTestServer.DefaultAccount,
        Container = Namespace,
        Prefix = Prefix,
    };

    /// <inheritdoc />
    protected override async Task StoreCredentialAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var envelope = WriteOnlyProvisioning.SealSharedAccessSignature(
            await RecipientAsync(handler), Cloud, Azure.IssueSas(Namespace));
        var stored = await handler.ExecuteAsync(
            new SetDestinationCredentialsCommand(Cloud, null, Convert.ToHexStringLower(envelope), Kind: "sas"), Timeout);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(stored, (stored as ServiceError)?.Message);
    }

    private static AzureBlobTestServer StartStore()
    {
        var store = new AzureBlobTestServer();
        store.CreateContainer(Namespace);
        return store;
    }
}
