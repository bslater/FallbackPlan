using System.Net;
using System.Net.Sockets;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.AzureBlob;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// What the Azure Blob provider does beyond the shared contract, because the
/// Blob API does it differently (FR-REP-002, NFR-REL-001, ADR-0093): every
/// put is create-only whatever its conditions say, and carries its body's
/// digest so a body altered in transit is refused rather than stored; a
/// transient refusal is retried from content read once; a store that refuses
/// the credential, or a signature that does not allow the request, is a
/// fault that names its code and never the secret; a body cut short is an
/// IOException rather than a short object; a container that does not exist
/// is a fault, never an empty store; a delete of nothing is one request; a
/// listing spans pages from markers only the store can read and sees only
/// its own root; and nothing is spoken in clear to anywhere but this machine.
/// </summary>
[TestClass]
public sealed class AzureBlobObjectStoreTests : IAsyncDisposable
{
    private const string Container = "provider-tests";

    private static readonly AzureBlobStoreOptions QuickRetries = new() { RetryDelay = TimeSpan.FromMilliseconds(5) };

    private readonly AzureBlobTestServer _server = new();

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    [TestMethod]
    public async Task Put_WithNoConditions_StillAsksTheStoreToCreateOnly()
    {
        var store = Store();

        await store.PutAsync(Key("blobs/data/ab/one"), Content("sealed"u8.ToArray()), PutConditions.None, CancellationToken.None);

        var put = Assert.ContainsSingle(_server.Requests.Where(request => request.Method == "PUT"));
        Assert.AreEqual("*", put.Headers["if-none-match"], "an unconditional put would let the API overwrite a sealed object");
        Assert.AreEqual("BlockBlob", put.Headers["x-ms-blob-type"]);
    }

    [TestMethod]
    public async Task Put_EveryBody_CarriesItsDigest_SoOneAlteredInTransitIsRefused()
    {
        var store = Store();

        await store.PutAsync(Key("blobs/data/ab/digested"), Content("sealed blob"u8.ToArray()), PutConditions.IfNotExists, CancellationToken.None);

        var put = Assert.ContainsSingle(_server.Requests.Where(request => request.Method == "PUT"));
        Assert.AreEqual("8mR1IrDWoUvZQFIY5DmM5Q==", put.Headers["content-md5"], "the API checks the body against it before storing");
    }

    [TestMethod]
    public async Task Put_AKeyAlreadyHeld_IsAlreadyExists_FromTheStoresConflict()
    {
        var store = Store();
        await store.PutAsync(Key("index/delta/0001"), Content([1]), PutConditions.IfNotExists, CancellationToken.None);

        var again = await store.PutAsync(Key("index/delta/0001"), Content([2]), PutConditions.IfNotExists, CancellationToken.None);

        Assert.AreEqual(PutOutcome.AlreadyExists, again.Outcome);
        Assert.AreEqual(409, _server.Requests.Last(request => request.Method == "PUT").Status, "the API answers a create of a held key with a conflict");
        SequenceAssert.AreEqual(new byte[] { 1 }, _server.ObjectIn(Container, "root/index/delta/0001")!);
    }

    [TestMethod]
    public async Task Put_ATransientRefusal_IsRetriedFromTheContentReadOnce()
    {
        var store = Store();
        var invocations = 0;
        _server.FailNext(2, 503, "ServerBusy");

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
        SequenceAssert.AreEqual("blob bytes"u8.ToArray(), _server.ObjectIn(Container, "root/blobs/data/ab/retried")!);
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
        SequenceAssert.AreEqual(payload, _server.ObjectIn(Container, "root/blobs/data/cd/streamed")!);
    }

    [TestMethod]
    [DataRow(503, "ServerBusy")]
    [DataRow(500, "OperationTimedOut")]
    public async Task Put_ThrottledUntilTheRetriesRunOut_IsABusyStore_NamingTheStoresCode(int status, string code)
    {
        // The API's two answers for a partition past its targets: a store
        // asking to be asked later, which the next pass may find serving, so
        // a gap that closes itself rather than a refusal (FR-QUOTA-001).
        var store = Store(options: new AzureBlobStoreOptions { MaxAttempts = 3, RetryDelay = TimeSpan.FromMilliseconds(5) });
        _server.FailNext(3, status, code);

        var fault = await Assert.ThrowsExactlyAsync<StoreBusyException>(async () => await store.PutAsync(
            Key("blobs/data/ab/never"), Content([1, 2, 3]), PutConditions.IfNotExists, CancellationToken.None));

        Assert.IsInstanceOfType<StoreUnavailableException>(fault, "a busy store is unavailable, as one that does not answer is");
        Assert.Contains(code, fault.Message, StringComparison.Ordinal);
        Assert.HasCount(3, _server.Requests.Where(request => request.Method == "PUT"));
    }

    [TestMethod]
    public async Task AnyRequest_SignedWithTheWrongKey_IsAFaultNamingTheCode_AndNeverTheKey()
    {
        const string wrong = "d3Jvbmcta2V5LXdyb25nLWtleS13cm9uZy1rZXktd3Jvbmcta2V5LXdyb25nLWtleQ==";
        _server.CreateContainer(Container);
        var store = new AzureBlobObjectStore(
            new AzureBlobLocation(_server.Account, Container, "root", _server.Endpoint),
            AzureBlobCredentials.SharedKey(wrong),
            options: QuickRetries);

        // A HEAD, because the API names the code even there, in a header.
        var fault = await Assert.ThrowsAsync<IOException>(async () =>
            await store.GetMetadataAsync(Key("descriptor"), CancellationToken.None));

        Assert.Contains("AuthenticationFailed", fault.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(wrong, fault.ToString(), StringComparison.Ordinal);
        Assert.ContainsSingle(_server.Requests, "a refusal of the credential is not retried: it will not change");
    }

    [TestMethod]
    public async Task AnyRequest_UnderASignatureThatDoesNotAllowIt_IsAFaultNamingTheCode_AndNeverTheToken()
    {
        _server.CreateContainer(Container);
        var readOnly = _server.IssueSas(Container, permissions: "rl");
        var store = new AzureBlobObjectStore(
            new AzureBlobLocation(_server.Account, Container, "root", _server.Endpoint),
            AzureBlobCredentials.SharedAccessSignature(readOnly),
            options: QuickRetries);

        var fault = await Assert.ThrowsAsync<IOException>(async () => await store.PutAsync(
            Key("blobs/data/ab/refused"), Content([1]), PutConditions.IfNotExists, CancellationToken.None));

        Assert.Contains("AuthorizationPermissionMismatch", fault.Message, StringComparison.Ordinal);
        var signature = readOnly[(readOnly.IndexOf("sig=", StringComparison.Ordinal) + 4)..];
        Assert.DoesNotContain(signature, fault.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Uri.UnescapeDataString(signature), fault.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AnyRequest_UnderASignatureThatHasExpired_IsRefusedBeforeItIsSent()
    {
        // The token says when it stops being honoured, in clear. A request
        // the store will refuse is not worth a request.
        _server.CreateContainer(Container);
        var lapsed = _server.IssueSas(Container, expires: DateTimeOffset.UtcNow.AddMinutes(-5));
        var store = new AzureBlobObjectStore(
            new AzureBlobLocation(_server.Account, Container, "root", _server.Endpoint),
            AzureBlobCredentials.SharedAccessSignature(lapsed),
            options: QuickRetries);

        var fault = await Assert.ThrowsAsync<IOException>(async () =>
            await store.GetMetadataAsync(Key("descriptor"), CancellationToken.None));

        Assert.Contains("expired", fault.Message, StringComparison.Ordinal);
        Assert.IsEmpty(_server.Requests);
    }

    [TestMethod]
    public async Task GetMetadata_InAContainerThatDoesNotExist_IsAFault_NeverAbsence()
    {
        // An empty answer here would read as "nothing stored yet" and send a
        // sync to recreate a replica in a container nobody declared.
        var store = new AzureBlobObjectStore(
            new AzureBlobLocation(_server.Account, "no-such-container", "root", _server.Endpoint),
            AzureBlobCredentials.SharedKey(_server.AccountKey),
            options: QuickRetries);

        var fault = await Assert.ThrowsAsync<IOException>(async () =>
            await store.GetMetadataAsync(Key("descriptor"), CancellationToken.None));

        Assert.Contains("ContainerNotFound", fault.Message, StringComparison.Ordinal);
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
    public async Task OpenRead_ARangeRunningPastTheEnd_IsRangeNotSatisfiable_AsReadFromContentRange()
    {
        // The API answers a range that starts inside the blob and runs past
        // its end with a short 206. The contract calls that range
        // unsatisfiable, as the local store does, so the served range is read
        // from Content-Range rather than trusted to be the one asked for.
        var store = Store();
        await store.PutAsync(Key("blobs/data/ab/short"), Content("sealed blob"u8.ToArray()), PutConditions.IfNotExists, CancellationToken.None);

        using var read = await store.OpenReadAsync(Key("blobs/data/ab/short"), new ObjectRange(5, 100), CancellationToken.None);

        Assert.AreEqual(OpenReadOutcome.RangeNotSatisfiable, read.Outcome);
        var get = _server.Requests.Last(request => request.Method == "GET");
        Assert.AreEqual(206, get.Status, "the store served what there was");
        Assert.AreEqual("bytes=5-104", get.Headers["x-ms-range"]);
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
    public async Task Delete_OfNothing_IsNotFound_InOneRequest()
    {
        // The API says so itself, unlike S3's: no look first.
        var store = Store();

        var deleted = await store.DeleteAsync(Key("tombstones/never-written"), DeleteConditions.None, CancellationToken.None);

        Assert.AreEqual(DeleteOutcome.NotFound, deleted.Outcome);
        Assert.AreEqual("DELETE", Assert.ContainsSingle(_server.Requests).Method);
    }

    [TestMethod]
    public async Task List_SpanningPages_ReturnsEveryKeyOnce_InOrdinalOrder_FromTheStoresMarkers()
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
        var pages = _server.Requests.Where(request => request.Method == "GET").ToList();
        Assert.IsGreaterThan(2, pages.Count, "the listing went past its first page");
        Assert.IsTrue(
            pages.Skip(1).All(page => page.Target.Contains("marker=2%21", StringComparison.Ordinal)),
            "each later page resumes from the marker the store issued, as issued");
    }

    [TestMethod]
    public async Task List_ResumedAfterAKey_AsksTheStoreToStartThere_RatherThanWalkingFromTheFirstPage()
    {
        // The API resumes only from a marker it issued, which a caller that
        // persisted a key does not have; walked from the first page instead,
        // every resumed listing would pay for every page before its position.
        // Asked to start at the key itself, the store answers from there, and
        // the key, which the API includes, is the one entry passed over.
        var store = Store();
        _server.ListPageLimit = 2;
        foreach (var key in new[] { "blobs/data/aa/one", "blobs/data/bb/two", "blobs/data/cc/three", "blobs/data/dd/four" })
        {
            await store.PutAsync(Key(key), Content([1]), PutConditions.IfNotExists, CancellationToken.None);
        }

        List<string> listed = [];
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("blobs/"), new ListOptions { ResumeAfter = "blobs/data/cc/three" }, CancellationToken.None))
        {
            listed.Add(entry.Key.Value);
        }

        Assert.AreEqual("blobs/data/dd/four", Assert.ContainsSingle(listed));
        var asked = Assert.ContainsSingle(_server.Listings, "one page, from the position: none of the two before it");
        Assert.Contains("startFrom=root/blobs/data/cc/three", Uri.UnescapeDataString(asked.Target), StringComparison.Ordinal);
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
    public async Task AnyRequest_ToAStoreNobodyAnswers_IsUnreachable_AfterItsRetries()
    {
        var store = new AzureBlobObjectStore(
            new AzureBlobLocation(_server.Account, Container, "root", new Uri($"http://127.0.0.1:{ClosedPort()}/{_server.Account}")),
            AzureBlobCredentials.SharedKey(_server.AccountKey),
            options: QuickRetries);

        var fault = await Assert.ThrowsAsync<AzureBlobStoreUnreachableException>(async () =>
            await store.GetMetadataAsync(Key("descriptor"), CancellationToken.None));

        Assert.IsInstanceOfType<StoreUnreachableException>(fault, "every caller tells an outage from a refusal by one type");
        Assert.IsInstanceOfType<IOException>(fault, "unreachable is a fault every caller already handles");
    }

    [TestMethod]
    public void Capabilities_DeclareWhatTheApiPromises()
    {
        var declared = AzureBlobObjectStore.DeclaredCapabilities;

        Assert.IsTrue(declared.ConditionalCreate);
        Assert.IsTrue(declared.RangedReads);
        Assert.AreEqual(ListingConsistency.Strong, declared.ListingConsistency);
        Assert.AreEqual(5000L * 1024 * 1024, declared.MaximumObjectSize, "one Put Blob carries at most 5000 MiB");
        Assert.IsFalse(declared.MultipartUpload);
    }

    [TestMethod]
    public void Location_WithNoEndpoint_IsTheAccountsHostAtThePublicService()
    {
        var location = new AzureBlobLocation("fbptestaccount", "family-backups", "site-a");

        Assert.AreEqual(
            "https://fbptestaccount.blob.core.windows.net/family-backups/site-a/descriptor",
            location.BlobUri("site-a/descriptor").AbsoluteUri);
        Assert.AreEqual("https://fbptestaccount.blob.core.windows.net/family-backups", location.ContainerUri.AbsoluteUri);
    }

    [TestMethod]
    public void Location_WithAnEndpoint_AddressesTheAccountByItsHost_OrByThePathThatNamesIt()
    {
        Assert.AreEqual(
            "https://fbptestaccount.objects.example.net/family-backups/site-a/descriptor",
            new AzureBlobLocation("fbptestaccount", "family-backups", "site-a", new Uri("https://fbptestaccount.objects.example.net"))
                .BlobUri("site-a/descriptor").AbsoluteUri);
        Assert.AreEqual(
            "http://127.0.0.1:10000/fbptestaccount/family-backups/site-a/descriptor",
            new AzureBlobLocation("fbptestaccount", "family-backups", "site-a", new Uri("http://127.0.0.1:10000/fbptestaccount/"))
                .BlobUri("site-a/descriptor").AbsoluteUri);
        Assert.AreEqual(
            "family-backups",
            new AzureBlobLocation("fbptestaccount", "family-backups", "site-a").Under("0123abcd").Container);
        Assert.AreEqual(
            "site-a/0123abcd",
            new AzureBlobLocation("fbptestaccount", "family-backups", "site-a").Under("0123abcd").Prefix);
    }

    [TestMethod]
    public void Location_InClear_IsRefused_UnlessTheStoreIsThisMachine()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new AzureBlobLocation("fbptestaccount", Container, endpoint: new Uri("http://objects.example.net")));

        _ = new AzureBlobLocation("fbptestaccount", Container, endpoint: new Uri("http://127.0.0.1:10000/fbptestaccount"));
        _ = new AzureBlobLocation("fbptestaccount", Container, endpoint: new Uri("http://localhost:10000/fbptestaccount"));
        _ = new AzureBlobLocation("fbptestaccount", Container, endpoint: new Uri("https://objects.example.net"));
    }

    [TestMethod]
    public void Location_DefectOf_NamesEachRule_InOneSentence()
    {
        Assert.IsNull(AzureBlobLocation.DefectOf("fbptestaccount", "family-backups", "site-a/host_1", null));
        Assert.Contains("storage account", AzureBlobLocation.DefectOf("Has_Capitals", "family-backups", null, null)!, StringComparison.Ordinal);
        Assert.Contains("storage account", AzureBlobLocation.DefectOf("ab", "family-backups", null, null)!, StringComparison.Ordinal);
        Assert.Contains("container", AzureBlobLocation.DefectOf("fbptestaccount", "two--hyphens", null, null)!, StringComparison.Ordinal);
        Assert.Contains("container", AzureBlobLocation.DefectOf("fbptestaccount", "-leading", null, null)!, StringComparison.Ordinal);
        Assert.Contains("container", AzureBlobLocation.DefectOf("fbptestaccount", "trailing-", null, null)!, StringComparison.Ordinal);
        Assert.Contains("prefix", AzureBlobLocation.DefectOf("fbptestaccount", "family-backups", "site-a/.hidden", null)!, StringComparison.Ordinal);
        Assert.Contains(
            "endpoint",
            AzureBlobLocation.DefectOf("fbptestaccount", "family-backups", null, new Uri("https://objects.example.net/another-account"))!,
            StringComparison.Ordinal);
        Assert.Contains(
            "endpoint",
            AzureBlobLocation.DefectOf("fbptestaccount", "family-backups", null, new Uri("https://objects.example.net/?sv=2024-11-04"))!,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void Credentials_ASharedAccessSignature_ReadsItsExpiry_AndDropsALeadingQuestionMark()
    {
        var sas = AzureBlobCredentials.SharedAccessSignature("?sv=2024-11-04&sr=c&sp=racwdl&se=2026-12-31T00%3A00%3A00Z&sig=AbC%2Bd%3D");

        Assert.AreEqual(AzureBlobCredentialKind.SharedAccessSignature, sas.Kind);
        Assert.AreEqual(new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero), sas.Expires);
        Assert.AreEqual("sv=2024-11-04&sr=c&sp=racwdl&se=2026-12-31T00%3A00%3A00Z&sig=AbC%2Bd%3D", sas.Secret);
        Assert.IsNull(AzureBlobCredentials.SharedAccessSignature("sv=2024-11-04&si=policy&sig=AbC%3D").Expires, "a stored policy may hold the expiry instead");
        Assert.IsNull(AzureBlobCredentials.SharedKey(AzureBlobTestServer.DefaultAccountKey).Expires);
    }

    [TestMethod]
    public void Credentials_WhatIsNotASignatureOrAnAccountKey_IsRefusedWhereItArrives()
    {
        Assert.ThrowsExactly<ArgumentException>(() => AzureBlobCredentials.SharedAccessSignature("sv=2024-11-04&sr=c"), "no signature");
        Assert.ThrowsExactly<ArgumentException>(() => AzureBlobCredentials.SharedAccessSignature("sr=c&sig=AbC%3D"), "no version");
        Assert.ThrowsExactly<ArgumentException>(() => AzureBlobCredentials.SharedAccessSignature("sv=2024-11-04 &sig=AbC%3D"), "a space");
        Assert.ThrowsExactly<ArgumentException>(() => AzureBlobCredentials.SharedAccessSignature("sv=2024-11-04&se=soon&sig=AbC%3D"), "an expiry that is not a time");
        Assert.ThrowsExactly<ArgumentException>(() => AzureBlobCredentials.SharedAccessSignature("https://fbptestaccount.blob.example.net/c?sv=2024-11-04&sig=AbC%3D"), "a URL, not a token");
        Assert.ThrowsExactly<ArgumentException>(() => AzureBlobCredentials.SharedKey("not base64 at all"));
        Assert.ThrowsExactly<ArgumentException>(() => AzureBlobCredentials.SharedKey(""));
    }

    private AzureBlobObjectStore Store(string prefix = "root", AzureBlobStoreOptions? options = null)
    {
        _server.CreateContainer(Container);
        return new AzureBlobObjectStore(
            new AzureBlobLocation(_server.Account, Container, prefix, _server.Endpoint),
            AzureBlobCredentials.SharedKey(_server.AccountKey),
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
