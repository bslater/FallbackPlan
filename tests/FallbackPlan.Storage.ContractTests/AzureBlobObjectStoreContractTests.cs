using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.AzureBlob;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// The Azure Blob provider against the shared contract suite, signing with
/// the account key (FR-REP-002, NFR-PORT-002, NFR-PORT-004; ADR-0093): every
/// case the local store passes, unchanged, over HTTP to a Blob server on
/// loopback that checks each request's Shared Key signature with a verifier
/// of its own. Each test gets its own container, rooted under a prefix, so
/// the provider's rooting is exercised by every case rather than one.
/// </summary>
[TestClass]
public sealed class AzureBlobObjectStoreContractTests : ObjectStoreContractTests, IAsyncDisposable
{
    private readonly AzureBlobTestServer _server = new();

    /// <inheritdoc />
    protected override IObjectStore CreateStore()
    {
        var container = "contract-" + Guid.NewGuid().ToString("n")[..12];
        _server.CreateContainer(container);
        return new AzureBlobObjectStore(
            new AzureBlobLocation(_server.Account, container, "replicas/repo", _server.Endpoint),
            AzureBlobCredentials.SharedKey(_server.AccountKey));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

/// <summary>
/// The same suite under a shared access signature for the container
/// (FR-REP-002, ADR-0093): the second credential an Azure Blob destination
/// accepts, carried in each request's query as it was issued, held by the
/// server to the container and the permissions it names.
/// </summary>
[TestClass]
public sealed class AzureBlobSasObjectStoreContractTests : ObjectStoreContractTests, IAsyncDisposable
{
    private readonly AzureBlobTestServer _server = new();

    /// <inheritdoc />
    protected override IObjectStore CreateStore()
    {
        var container = "contract-" + Guid.NewGuid().ToString("n")[..12];
        _server.CreateContainer(container);
        return new AzureBlobObjectStore(
            new AzureBlobLocation(_server.Account, container, "replicas/repo", _server.Endpoint),
            AzureBlobCredentials.SharedAccessSignature(_server.IssueSas(container)));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

/// <summary>
/// The same suite against an Azure Blob container the person running it names
/// (FR-REP-002, ADR-0093), so the provider can be held to a real
/// implementation as well as to the specification's: set
/// <c>FALLBACKPLAN_AZURE_TEST_ACCOUNT</c>, <c>_CONTAINER</c>, and
/// <c>_ACCOUNT_KEY</c> or <c>_SAS</c>, with <c>_ENDPOINT</c> for an account
/// the public service does not host. Every test writes under a prefix of its
/// own and deletes what it wrote. Reported skipped, never passed, where
/// nothing is named.
/// </summary>
[TestClass]
[ConfiguredAzureBlobStore]
public sealed class ConfiguredAzureBlobObjectStoreContractTests : ObjectStoreContractTests, IAsyncDisposable
{
    private readonly List<AzureBlobObjectStore> _created = [];

    /// <inheritdoc />
    protected override IObjectStore CreateStore()
    {
        var configured = ConfiguredAzureBlobStoreAttribute.Read()
            ?? throw new InvalidOperationException("No Azure Blob container is configured for this run.");
        var store = new AzureBlobObjectStore(
            new AzureBlobLocation(
                configured.Account, configured.Container,
                "fallbackplan-contract/" + Guid.NewGuid().ToString("n"), configured.Endpoint),
            configured.AccountKey is { } key
                ? AzureBlobCredentials.SharedKey(key)
                : AzureBlobCredentials.SharedAccessSignature(configured.Sas!));
        _created.Add(store);
        return store;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var store in _created)
        {
            await foreach (var entry in store.ListAsync(ObjectPrefix.All, ListOptions.Default, CancellationToken.None))
            {
                await store.DeleteAsync(entry.Key, DeleteConditions.None, CancellationToken.None);
            }
        }
    }
}
