using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.AzureBlob;
using FallbackPlan.Storage.S3;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// A network store says what it was asked, request by request, to whoever is
/// keeping count (FR-DEST-019, ADR-0111): every request that leaves, by its
/// operation, with the bytes it carried and the bytes the store said it
/// answered with. A store bills each attempt, so a request retried is counted
/// once an attempt, and a listing once a page.
/// </summary>
/// <remarks>
/// Counted where the request is sent, not where a caller asks: a listing is
/// one call and as many requests as it has pages, and a retried put one call
/// and as many requests as it took.
/// </remarks>
[TestClass]
public sealed class StoreRequestObserverTests : IAsyncDisposable
{
    private const string Bucket = "observed";

    private readonly S3CompatibleTestServer _s3 = new();
    private readonly AzureBlobTestServer _azure = new();
    private readonly Observed _observed = new();

    public async ValueTask DisposeAsync()
    {
        await _s3.DisposeAsync();
        await _azure.DisposeAsync();
    }

    [TestMethod]
    [DataRow("s3")]
    [DataRow("azure-blob")]
    public async Task EveryRequestSent_IsObserved_WithItsOperationAndItsBytes(string kind)
    {
        var store = Store(kind);
        var key = ObjectKey.Parse("blobs/data/ab/one");
        var content = new byte[1_000];

        await store.PutAsync(key, _ => ValueTask.FromResult<Stream>(new MemoryStream(content)), PutConditions.IfNotExists, CancellationToken.None);
        var read = await store.OpenReadAsync(key, range: null, CancellationToken.None);
        await using (read.Content!)
        {
            await read.Content!.CopyToAsync(Stream.Null);
        }

        await store.GetMetadataAsync(key, CancellationToken.None);
        await foreach (var _ in store.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
        }

        await store.DeleteAsync(key, DeleteConditions.None, CancellationToken.None);

        // The S3 API answers a delete of nothing as done, so its delete asks
        // first whether there is anything, and that head is billed too.
        SequenceAssert.AreEqual(
            kind == "s3" ? ["put", "get", "head", "list", "head", "delete"] : ["put", "get", "head", "list", "delete"],
            _observed.Operations);
        Assert.AreEqual(1_000, _observed.Calls.Single(call => call.Operation == "put").Sent);
        Assert.AreEqual(1_000, _observed.Calls.Single(call => call.Operation == "get").Received);
        Assert.AreEqual(0, _observed.Calls.Single(call => call.Operation == "get").Sent);
        Assert.IsTrue(
            _observed.Calls.Where(call => call.Operation == "head").All(call => call.Received == 0),
            "a head's answer describes the object without carrying it");
    }

    [TestMethod]
    [DataRow("s3")]
    [DataRow("azure-blob")]
    public async Task AnAttemptNothingAnswered_IsObservedToo(string kind)
    {
        // The store may have taken a request whose answer never came back.
        var store = Store(kind);
        Server(kind).DropNext();

        await store.PutAsync(
            ObjectKey.Parse("blobs/data/ab/dropped"), _ => ValueTask.FromResult<Stream>(new MemoryStream(new byte[10])),
            PutConditions.IfNotExists, CancellationToken.None);

        var puts = _observed.Calls.Where(call => call.Operation == "put").ToList();
        Assert.HasCount(2, puts);
        Assert.AreEqual((10L, 0L), (puts[0].Sent, puts[0].Received), "sent whole, and nothing answered");
    }

    [TestMethod]
    [DataRow("s3")]
    [DataRow("azure-blob")]
    public async Task ARetriedRequest_IsObservedOnceAnAttempt(string kind)
    {
        var store = Store(kind);
        Server(kind).FailNext(2, 503, kind == "s3" ? "SlowDown" : "ServerBusy");

        await store.PutAsync(
            ObjectKey.Parse("blobs/data/ab/retried"), _ => ValueTask.FromResult<Stream>(new MemoryStream(new byte[10])),
            PutConditions.IfNotExists, CancellationToken.None);

        Assert.HasCount(3, _observed.Calls.Where(call => call.Operation == "put"), "the store bills each attempt");
    }

    [TestMethod]
    [DataRow("s3")]
    [DataRow("azure-blob")]
    public async Task AListing_IsObservedOnceAPage(string kind)
    {
        var store = Store(kind);
        Server(kind).ListPageLimit = 2;
        foreach (var name in new[] { "one", "two", "three", "four", "five" })
        {
            await store.PutAsync(
                ObjectKey.Parse($"index/delta/{name}"), _ => ValueTask.FromResult<Stream>(new MemoryStream([1])),
                PutConditions.IfNotExists, CancellationToken.None);
        }

        await foreach (var _ in store.ListAsync(ObjectPrefix.Parse("index/"), ListOptions.Default, CancellationToken.None))
        {
        }

        Assert.HasCount(3, _observed.Calls.Where(call => call.Operation == "list"));
    }

    [TestMethod]
    [DataRow("s3")]
    [DataRow("azure-blob")]
    public async Task AStoreNobodyCountsFor_SendsAsBefore(string kind)
    {
        // The observer is a reader of the requests, never a party to them.
        var store = Store(kind, observed: false);

        var put = await store.PutAsync(
            ObjectKey.Parse("blobs/data/ab/plain"), _ => ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3])),
            PutConditions.IfNotExists, CancellationToken.None);

        Assert.AreEqual(PutOutcome.Created, put.Outcome);
        Assert.IsEmpty(_observed.Calls);
    }

    private ObjectStoreTestServer Server(string kind) => kind == "s3" ? _s3 : _azure;

    private IObjectStore Store(string kind, bool observed = true)
    {
        var observer = observed ? _observed : null;
        if (kind == "s3")
        {
            _s3.CreateBucket(Bucket);
            return new S3ObjectStore(
                new S3Location(_s3.Endpoint, Bucket, _s3.Region, "root"),
                new S3Credentials(_s3.AccessKeyId, _s3.SecretAccessKey),
                options: new S3StoreOptions { RetryDelay = TimeSpan.FromMilliseconds(5), Observer = observer });
        }

        _azure.CreateContainer(Bucket);
        return new AzureBlobObjectStore(
            new AzureBlobLocation(_azure.Account, Bucket, "root", _azure.Endpoint),
            AzureBlobCredentials.SharedKey(_azure.AccountKey),
            options: new AzureBlobStoreOptions { RetryDelay = TimeSpan.FromMilliseconds(5), Observer = observer });
    }

    private sealed class Observed : IStoreRequestObserver
    {
        private readonly List<(string Operation, long Sent, long Received)> _calls = [];

        public IReadOnlyList<(string Operation, long Sent, long Received)> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public IReadOnlyList<string> Operations => [.. Calls.Select(call => call.Operation)];

        public void Requested(string operation, long bytesSent, long bytesReceived)
        {
            lock (_calls)
            {
                _calls.Add((operation, bytesSent, bytesReceived));
            }
        }
    }
}
