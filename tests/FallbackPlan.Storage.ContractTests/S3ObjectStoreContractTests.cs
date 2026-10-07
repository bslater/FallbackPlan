using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.S3;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// The S3-compatible provider against the shared contract suite (FR-REP-002,
/// NFR-PORT-002, NFR-PORT-004; ADR-0091): every case the local store passes,
/// unchanged, over HTTP to an S3-compatible server on loopback that checks
/// each request's signature with a verifier of its own. Each test gets its
/// own bucket, rooted under a prefix, so the provider's rooting is exercised
/// by every case rather than one.
/// </summary>
[TestClass]
public sealed class S3ObjectStoreContractTests : ObjectStoreContractTests, IAsyncDisposable
{
    private readonly S3CompatibleTestServer _server = new();

    /// <inheritdoc />
    protected override IObjectStore CreateStore()
    {
        var bucket = "contract-" + Guid.NewGuid().ToString("n")[..12];
        _server.CreateBucket(bucket);
        return new S3ObjectStore(
            new S3Location(_server.Endpoint, bucket, _server.Region, "replicas/repo"),
            new S3Credentials(_server.AccessKeyId, _server.SecretAccessKey));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

/// <summary>
/// The same suite against an S3-compatible store the person running it names
/// (FR-REP-002, ADR-0091), so the provider can be held to a real
/// implementation as well as to the specification's: set
/// <c>FALLBACKPLAN_S3_TEST_ENDPOINT</c>, <c>_BUCKET</c>, <c>_REGION</c>,
/// <c>_ACCESS_KEY_ID</c> and <c>_SECRET_ACCESS_KEY</c>. Every test writes
/// under a prefix of its own and deletes what it wrote. Reported skipped,
/// never passed, where nothing is named.
/// </summary>
[TestClass]
[ConfiguredS3Store]
public sealed class ConfiguredS3ObjectStoreContractTests : ObjectStoreContractTests, IAsyncDisposable
{
    private readonly List<S3ObjectStore> _created = [];

    /// <inheritdoc />
    protected override IObjectStore CreateStore()
    {
        var configured = ConfiguredS3StoreAttribute.Read()
            ?? throw new InvalidOperationException("No S3-compatible store is configured for this run.");
        var store = new S3ObjectStore(
            new S3Location(
                configured.Endpoint, configured.Bucket, configured.Region,
                "fallbackplan-contract/" + Guid.NewGuid().ToString("n")),
            new S3Credentials(configured.AccessKeyId, configured.SecretAccessKey));
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
