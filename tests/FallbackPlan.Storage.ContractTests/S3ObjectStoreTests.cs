using System.Net;
using System.Net.Sockets;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.S3;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// What the S3-compatible provider does beyond the shared contract, because
/// the S3 API does it differently (FR-REP-002, NFR-REL-001, ADR-0091): every
/// put is create-only whatever its conditions say, since the copier writes
/// unconditionally and the API would otherwise overwrite; a transient refusal
/// is retried from content read once, so the factory is never asked again; a
/// store that refuses the credentials is a fault that names its code and
/// never the secret; a store too busy through every attempt, one with no
/// room and one whose quota a put would cross are each a fault of its own
/// (FR-QUOTA-001); a body cut short is an IOException rather than a short
/// object; a listing spans pages, resumes where it is asked to, and sees only
/// its own root; and nothing is spoken in clear to anywhere but this machine.
/// </summary>
[TestClass]
public sealed class S3ObjectStoreTests : IAsyncDisposable
{
    private readonly S3CompatibleTestServer _server = new();

    private static readonly S3StoreOptions QuickRetries = new() { RetryDelay = TimeSpan.FromMilliseconds(5) };

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    [TestMethod]
    public async Task Put_WithNoConditions_StillAsksTheStoreToCreateOnly()
    {
        var store = Store();

        await store.PutAsync(Key("blobs/data/ab/one"), Content("sealed"u8.ToArray()), PutConditions.None, CancellationToken.None);

        var put = Assert.ContainsSingle(_server.Requests.Where(request => request.Method == "PUT"));
        Assert.AreEqual("*", put.Headers["if-none-match"], "an unconditional put would let the API overwrite a sealed object");
    }

    [TestMethod]
    public async Task Put_ATransientRefusal_IsRetriedFromTheContentReadOnce()
    {
        var store = Store();
        var invocations = 0;
        _server.FailNext(2, 503, "SlowDown");

        var put = await store.PutAsync(
            Key("blobs/data/ab/retried"),
            _ =>
            {
                invocations++;
                return ValueTask.FromResult<Stream>(new MemoryStream("blob bytes"u8.ToArray()));
            },
            PutConditions.IfNotExists,
            CancellationToken.None);

        Assert.AreEqual(PutOutcome.Created, put.Outcome);
        Assert.AreEqual(1, invocations, "the content is read once and resent, never fetched again");
        Assert.HasCount(3, _server.Requests.Where(request => request.Method == "PUT"));
        SequenceAssert.AreEqual("blob bytes"u8.ToArray(), _server.ObjectIn(Bucket, "root/blobs/data/ab/retried")!);
    }

    [TestMethod]
    public async Task Put_AConnectionDroppedUnanswered_IsRetried()
    {
        var store = Store();
        _server.DropNext();

        var put = await store.PutAsync(
            Key("index/delta/0001"), Content("delta"u8.ToArray()), PutConditions.IfNotExists, CancellationToken.None);

        Assert.AreEqual(PutOutcome.Created, put.Outcome);
    }

    [TestMethod]
    public async Task Put_ContentThatCannotSeek_IsReadOnce_AndArrivesWhole()
    {
        var store = Store();
        var payload = Enumerable.Range(0, 300_000).Select(value => (byte)(value * 7)).ToArray();
        _server.FailNext(1, 500, "InternalError");

        var put = await store.PutAsync(
            Key("blobs/data/cd/streamed"),
            _ => ValueTask.FromResult<Stream>(new ForwardOnlyStream(payload)),
            PutConditions.IfNotExists,
            CancellationToken.None);

        Assert.AreEqual(PutOutcome.Created, put.Outcome);
        SequenceAssert.AreEqual(payload, _server.ObjectIn(Bucket, "root/blobs/data/cd/streamed")!);
    }

    [TestMethod]
    [DataRow("SlowDown")]
    [DataRow("SlowDownWrite")]
    public async Task Put_ThrottledUntilTheRetriesRunOut_IsABusyStore_NamingTheStoresCode(string code)
    {
        // A throttle that outlasts this request's attempts is still a store
        // asking to be asked later: the next pass may find it serving, so it
        // is a gap that closes itself rather than a refusal (FR-QUOTA-001).
        // SlowDown is the API's own word; SlowDownWrite is one an
        // S3-compatible store uses for the same answer.
        var store = Store(options: new S3StoreOptions { MaxAttempts = 3, RetryDelay = TimeSpan.FromMilliseconds(5) });
        _server.FailNext(3, 503, code);

        var fault = await Assert.ThrowsExactlyAsync<StoreBusyException>(async () => await store.PutAsync(
            Key("blobs/data/ab/never"), Content([1, 2, 3]), PutConditions.IfNotExists, CancellationToken.None));

        Assert.IsInstanceOfType<StoreUnavailableException>(fault, "a busy store is unavailable, as one that does not answer is");
        Assert.Contains(code, fault.Message, StringComparison.Ordinal);
        Assert.HasCount(3, _server.Requests.Where(request => request.Method == "PUT"));
    }

    [TestMethod]
    [DataRow("XMinioStorageFull")]
    [DataRow("InsufficientCapacity")]
    public async Task Put_ToAStoreOutOfRoom_IsAFullStore_AndIsNotRetried(string code)
    {
        // 507, Insufficient Storage: the store has no room for the object,
        // which another attempt will not make. XMinioStorageFull is one
        // S3-compatible store's word for it and InsufficientCapacity
        // another's; the status is what they share (FR-QUOTA-001).
        var store = Store();
        _server.FailNext(1, 507, code);

        var fault = await Assert.ThrowsExactlyAsync<StoreFullException>(async () => await store.PutAsync(
            Key("blobs/data/ab/never"), Content([1, 2, 3]), PutConditions.IfNotExists, CancellationToken.None));

        Assert.IsInstanceOfType<StoreUnavailableException>(fault, "room made at the store lets the next pass go on, as at a full disk");
        Assert.Contains(code, fault.Message, StringComparison.Ordinal);
        Assert.ContainsSingle(_server.Requests.Where(request => request.Method == "PUT"));
        Assert.IsNull(_server.ObjectIn(Bucket, "root/blobs/data/ab/never"), "a refused put leaves no object, partial or whole");
    }

    [TestMethod]
    [DataRow(403, "QuotaExceeded")]
    [DataRow(400, "XMinioAdminBucketQuotaExceeded")]
    public async Task Put_OverTheStoresQuota_IsAQuota_AndIsNotRetried(int status, string code)
    {
        // A quota is a limit the store's owner set, and it holds until a
        // person raises it or keeps less there: a decision, not a gap, so it
        // is told apart from a full store and from a throttle
        // (FR-QUOTA-001). Each S3-compatible store that has one names it
        // with its own code and status.
        var store = Store();
        _server.FailNext(1, status, code);

        var fault = await Assert.ThrowsExactlyAsync<StoreQuotaExceededException>(async () => await store.PutAsync(
            Key("blobs/data/ab/never"), Content([1, 2, 3]), PutConditions.IfNotExists, CancellationToken.None));

        Assert.IsNotInstanceOfType<StoreUnavailableException>(fault, "a quota does not lift by itself");
        Assert.Contains(code, fault.Message, StringComparison.Ordinal);
        Assert.ContainsSingle(_server.Requests.Where(request => request.Method == "PUT"));
        Assert.IsNull(_server.ObjectIn(Bucket, "root/blobs/data/ab/never"), "a refused put leaves no object, partial or whole");
    }

    [TestMethod]
    public async Task AnyRequest_SignedWithTheWrongSecret_IsAFaultNamingTheCode_AndNeverTheSecret()
    {
        var store = new S3ObjectStore(
            new S3Location(_server.Endpoint, Bucket, _server.Region, "root"),
            new S3Credentials(_server.AccessKeyId, "not-the-secret-anyone-configured"),
            options: QuickRetries);

        // A read, because the API's answer to one carries the error's code;
        // its answer to a HEAD carries no body to say it in.
        var fault = await Assert.ThrowsAsync<IOException>(async () =>
            await store.OpenReadAsync(Key("descriptor"), range: null, CancellationToken.None));

        Assert.Contains("SignatureDoesNotMatch", fault.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-secret", fault.ToString(), StringComparison.Ordinal);
        Assert.ContainsSingle(_server.Requests, "a refusal of the credentials is not retried: it will not change");
    }

    [TestMethod]
    public async Task OpenRead_ARangeStartingPastTheEnd_IsRangeNotSatisfiable()
    {
        var store = Store();
        await store.PutAsync(Key("blobs/data/ab/small"), Content(new byte[10]), PutConditions.IfNotExists, CancellationToken.None);

        using var read = await store.OpenReadAsync(Key("blobs/data/ab/small"), new ObjectRange(20, 5), CancellationToken.None);

        Assert.AreEqual(OpenReadOutcome.RangeNotSatisfiable, read.Outcome);
    }

    [TestMethod]
    public async Task OpenRead_ABodyCutShort_IsAnIOException_NeverAShortObject()
    {
        // A body that ends early must not read as a smaller object: the
        // reader treats an IOException as transient and a short blob as
        // damage, and only one of those is true here.
        var store = Store();
        var payload = Enumerable.Range(0, 64_000).Select(value => (byte)value).ToArray();
        await store.PutAsync(Key("blobs/data/ab/whole"), Content(payload), PutConditions.IfNotExists, CancellationToken.None);
        _server.TruncateNextBody();

        using var read = await store.OpenReadAsync(Key("blobs/data/ab/whole"), range: null, CancellationToken.None);
        Assert.AreEqual(OpenReadOutcome.Found, read.Outcome);

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            using var buffer = new MemoryStream();
            await read.Content!.CopyToAsync(buffer);
        });
    }

    [TestMethod]
    public async Task List_SpanningPages_ReturnsEveryKeyOnce_InOrdinalOrder()
    {
        var store = Store();
        _server.ListPageLimit = 2;
        string[] keys = ["index/delta/0005", "index/delta/0001", "index/delta/0003", "index/delta/0002", "index/delta/0004"];
        foreach (var key in keys)
        {
            await store.PutAsync(Key(key), Content([1]), PutConditions.IfNotExists, CancellationToken.None);
        }

        List<string> listed = [];
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("index/"), ListOptions.Default, CancellationToken.None))
        {
            listed.Add(entry.Key.Value);
        }

        SequenceAssert.AreEqual(keys.Order(StringComparer.Ordinal).ToArray(), listed.ToArray());
        Assert.IsGreaterThan(2, _server.Requests.Count(request => request.Method == "GET"), "the listing went past its first page");
    }

    [TestMethod]
    public async Task List_ResumedAfterAKey_AsksTheStoreToStartAfterIt()
    {
        // Resuming is a parameter of the request, not a walk from the first
        // page: a caller that resumes a long listing pays for the pages after
        // its position, never for the ones before it.
        var store = Store();
        foreach (var key in new[] { "blobs/data/aa/one", "blobs/data/bb/two", "blobs/data/cc/three" })
        {
            await store.PutAsync(Key(key), Content([1]), PutConditions.IfNotExists, CancellationToken.None);
        }

        List<string> listed = [];
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("blobs/"), new ListOptions { ResumeAfter = "blobs/data/aa/one" }, CancellationToken.None))
        {
            listed.Add(entry.Key.Value);
        }

        SequenceAssert.AreEqual(["blobs/data/bb/two", "blobs/data/cc/three"], listed.ToArray());
        var asked = Assert.ContainsSingle(_server.Listings);
        Assert.Contains("start-after=root/blobs/data/aa/one", Uri.UnescapeDataString(asked.Target), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task List_SeesOnlyItsOwnRoot_AndAnswersKeysWithoutIt()
    {
        var mine = Store("site-a/repo-1");
        var theirs = Store("site-a/repo-10");
        await mine.PutAsync(Key("descriptor"), Content([1]), PutConditions.IfNotExists, CancellationToken.None);
        await theirs.PutAsync(Key("descriptor"), Content([2]), PutConditions.IfNotExists, CancellationToken.None);

        List<string> listed = [];
        await foreach (var entry in mine.ListAsync(ObjectPrefix.All, ListOptions.Default, CancellationToken.None))
        {
            listed.Add(entry.Key.Value);
        }

        Assert.AreEqual("descriptor", Assert.ContainsSingle(listed), "a sibling whose name begins with this root's is not under it");
    }

    [TestMethod]
    public async Task ListChildren_NamesEachFolderUnderTheRoot_Once()
    {
        var root = Store("site-a");
        await Store("site-a/aaaa").PutAsync(Key("descriptor"), Content([1]), PutConditions.IfNotExists, CancellationToken.None);
        await Store("site-a/aaaa").PutAsync(Key("blobs/data/ab/x"), Content([1]), PutConditions.IfNotExists, CancellationToken.None);
        await Store("site-a/bbbb").PutAsync(Key("descriptor"), Content([1]), PutConditions.IfNotExists, CancellationToken.None);

        List<string> children = [];
        await foreach (var child in root.ListChildrenAsync(CancellationToken.None))
        {
            children.Add(child);
        }

        SequenceAssert.AreEqual(new[] { "aaaa", "bbbb" }, children.ToArray());
    }

    [TestMethod]
    public void Location_InClear_IsRefused_UnlessTheStoreIsThisMachine()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new S3Location(new Uri("http://objects.example.net"), Bucket, "us-east-1"));

        _ = new S3Location(new Uri("http://127.0.0.1:9000"), Bucket, "us-east-1");
        _ = new S3Location(new Uri("http://localhost:9000"), Bucket, "us-east-1");
        _ = new S3Location(new Uri("https://objects.example.net"), Bucket, "us-east-1");
    }

    [TestMethod]
    public void Location_AddressesTheBucketByPathOrByHost_AsDeclared()
    {
        var endpoint = new Uri("https://objects.example.net:8443");

        Assert.AreEqual(
            "https://objects.example.net:8443/backups/site-a/descriptor",
            new S3Location(endpoint, "backups", "us-east-1", "site-a").ObjectUri("site-a/descriptor").AbsoluteUri);
        Assert.AreEqual(
            "https://backups.objects.example.net:8443/site-a/descriptor",
            new S3Location(endpoint, "backups", "us-east-1", "site-a", S3Addressing.VirtualHost)
                .ObjectUri("site-a/descriptor").AbsoluteUri);
    }

    [TestMethod]
    public async Task AnyRequest_ToAStoreNobodyAnswers_IsUnreachable_AfterItsRetries()
    {
        var store = new S3ObjectStore(
            new S3Location(new Uri($"http://127.0.0.1:{ClosedPort()}"), Bucket, _server.Region),
            new S3Credentials(_server.AccessKeyId, _server.SecretAccessKey),
            options: QuickRetries);

        var fault = await Assert.ThrowsAsync<S3StoreUnreachableException>(async () =>
            await store.GetMetadataAsync(Key("descriptor"), CancellationToken.None));

        Assert.IsInstanceOfType<IOException>(fault, "unreachable is a fault every caller already handles");
    }

    [TestMethod]
    public void Capabilities_DeclareWhatTheApiPromises()
    {
        var declared = S3ObjectStore.DeclaredCapabilities;

        Assert.IsTrue(declared.ConditionalCreate);
        Assert.IsTrue(declared.RangedReads);
        Assert.AreEqual(ListingConsistency.Strong, declared.ListingConsistency);
        Assert.AreEqual(5L * 1024 * 1024 * 1024, declared.MaximumObjectSize, "one PUT carries at most 5 GiB");
        Assert.IsFalse(declared.MultipartUpload);
    }

    private const string Bucket = "provider-tests";

    private S3ObjectStore Store(string prefix = "root", S3StoreOptions? options = null)
    {
        _server.CreateBucket(Bucket);
        return new S3ObjectStore(
            new S3Location(_server.Endpoint, Bucket, _server.Region, prefix),
            new S3Credentials(_server.AccessKeyId, _server.SecretAccessKey),
            options: options ?? QuickRetries);
    }

    private static ObjectKey Key(string value) => ObjectKey.Parse(value);

    private static Func<CancellationToken, ValueTask<Stream>> Content(byte[] bytes) =>
        _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Content that can be read once, front to back, as a pipe would give it.</summary>
    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
