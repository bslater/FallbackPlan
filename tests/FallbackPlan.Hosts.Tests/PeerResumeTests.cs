using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A transfer cut inside an object resumes where it stopped
/// (FR-REP-003, [ADR-0057](../../docs/adr/0057-resumable-object-transfer.md)).
/// </summary>
/// <remarks>
/// <para>
/// The observable is <c>PushOutcome.BytesSent</c>, because the object counts
/// cannot tell the two outcomes apart: re-sending a whole object and sending
/// only its tail both commit exactly one object. Bytes are also the thing the
/// person on the slow uplink is paying, which is what the work is for.
/// </para>
/// <para>
/// Nothing severed a peer transfer mid-object before this suite existed, so
/// "an interrupted object is re-sent whole" was specified prose and an
/// emergent property of three code sites, believed rather than held. The
/// compatibility test below now holds it, and holds it as the behaviour an
/// un-negotiated pair keeps.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class PeerResumeTests : IDisposable
{
    private readonly HostHarness _source = new();

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-peer-resume", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private const ulong PairedAt = 1_722_600_000_000;

    /// <summary>Big enough that a cut lands inside it rather than between objects.</summary>
    private const int LargeObjectBytes = 6 * 1024 * 1024;

    private CancellationToken Timeout => _timeout.Token;

    private IPEndPoint? _endpoint;
    private RemoteServiceListener? _listener;
    private Protocol.PeerIdentity? _destinationIdentity;

    public void Dispose()
    {
        _listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _timeout.Dispose();
        _source.Dispose();

        try
        {
            if (Directory.Exists(_destinationState))
            {
                Directory.Delete(_destinationState, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    [TestMethod]
    public async Task Push_SeveredMidObject_ResumesFromWhatTheDestinationStaged()
    {
        await SeedSourceAsync();
        await StartDestinationAsync();

        // The first push dies part-way through the largest object: the link
        // drops, and every byte of that object that already crossed is at the
        // destination with nowhere recorded to put it.
        var biggest = await LargestObjectAsync();
        var severing = Severing(biggest);

        await Assert.ThrowsExactlyAsync<IOException>(() => PushAsync(severing));
        Assert.IsTrue(severing.Severed, "the cut must have landed inside an object for this to test anything");

        // The same pair, a moment later, over a healthy link. The wait is the
        // one every test against this listener needs: the destination finishes
        // tearing its side of the session down after the sender has already
        // returned, and until it does it still holds the staged file open.
        severing.Heal();
        await WaitForStagedPrefixAsync();
        var second = await PushAsync(severing);

        Assert.AreEqual(
            1L, second.ResumedObjects,
            "the object the link died inside is the one that should have resumed");
        Assert.IsLessThan(
            biggest.Length,
            second.BytesSent,
            $"the whole object ({biggest.Length} bytes) crossed again rather than its tail");

        await AssertReplicaMatchesSourceAsync();
    }

    [TestMethod]
    public async Task Push_ToAPeerThatDoesNotOfferResume_SendsTheWholeObjectAgain()
    {
        // The compatibility rule, and the behaviour of every build before this
        // one: with no agreement to resume, a cut object crosses whole on the
        // next session and the destination keeps nothing between them. It has
        // to keep working, and it has to keep being what happens — a
        // destination that quietly staged bytes for a peer that never asked
        // would be holding a source's data on an assumption.
        await SeedSourceAsync();
        await StartDestinationAsync();

        var biggest = await LargestObjectAsync();
        var severing = Severing(biggest);

        await Assert.ThrowsExactlyAsync<IOException>(() => PushAsync(severing, offerResume: false));
        severing.Heal();

        // Waited for, not read at once: the destination finishes tearing its
        // side down after the sender has already returned, and the staged file
        // only goes when it does.
        await WaitForNothingStagedAsync();

        var second = await PushAsync(severing, offerResume: false);

        Assert.AreEqual(0L, second.ResumedObjects);
        Assert.IsGreaterThanOrEqualTo(
            biggest.Length, second.BytesSent, "without the feature the whole object must cross again");

        await AssertReplicaMatchesSourceAsync();
    }

    [TestMethod]
    public async Task Push_AStagedPrefixThatDoesNotMatch_StartsTheObjectAgain()
    {
        // The check that makes resuming safe. The destination cannot tell a
        // good prefix from a rotted one — it holds no repository keys and the
        // store key is a keyed rendering of an identifier, not of the bytes —
        // so the source hashes its own copy of what was claimed. A mismatch
        // costs a re-send, which is what the old behaviour cost anyway.
        await SeedSourceAsync();
        await StartDestinationAsync();

        var biggest = await LargestObjectAsync();
        var severing = Severing(biggest);

        await Assert.ThrowsExactlyAsync<IOException>(() => PushAsync(severing));
        severing.Heal();
        await WaitForStagedPrefixAsync();

        // Something eats a byte of the staged prefix between the sessions.
        var staged = Assert.ContainsSingle(Directory.GetFiles(
            Path.Combine(_destinationState, "spool", "replication"), "*.partial", SearchOption.AllDirectories));
        var damaged = await File.ReadAllBytesAsync(staged, Timeout);
        damaged[damaged.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(staged, damaged, Timeout);

        var second = await PushAsync(severing);

        Assert.AreEqual(
            0L, second.ResumedObjects, "a prefix that does not match this copy must not be resumed on top of");
        Assert.IsGreaterThanOrEqualTo(biggest.Length, second.BytesSent);

        // And the replica is right, which is the point: the wrong bytes were
        // overwritten rather than committed around.
        await AssertReplicaMatchesSourceAsync();
    }

    private SeveringReadObjectStore Severing(ObjectEntry biggest) => new(
        new LocalFileSystemObjectStore(_source.RepositoryPath),
        key => string.Equals(key, biggest.Key.Value, StringComparison.Ordinal),
        readableBytes: 2 * 1024 * 1024);

    /// <summary>
    /// Waits until the cut session has let go of what it staged — the file
    /// exists and opens exclusively.
    /// </summary>
    private async Task WaitForStagedPrefixAsync()
    {
        var spool = Path.Combine(_destinationState, "spool", "replication");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            var staged = Directory.Exists(spool)
                ? Directory.GetFiles(spool, "*.partial", SearchOption.AllDirectories)
                : [];

            if (staged.Length == 1 && CanOpenExclusively(staged[0]))
            {
                return;
            }

            Assert.IsTrue(
                DateTimeOffset.UtcNow < deadline,
                $"the cut session left {staged.Length} staged prefix(es) and did not release them");
            await Task.Delay(50, Timeout);
        }
    }

    /// <summary>Waits until the destination holds no staged prefix at all.</summary>
    private async Task WaitForNothingStagedAsync()
    {
        var spool = Path.Combine(_destinationState, "spool", "replication");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            var staged = Directory.Exists(spool)
                ? Directory.GetFiles(spool, "*.partial", SearchOption.AllDirectories)
                : [];

            if (staged.Length == 0)
            {
                return;
            }

            Assert.IsTrue(
                DateTimeOffset.UtcNow < deadline,
                "a pair that did not negotiate resumption left a staged prefix behind");
            await Task.Delay(50, Timeout);
        }
    }

    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return file.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>A repository with one object large enough to be cut in half.</summary>
    private async Task SeedSourceAsync()
    {
        await _source.CreateRepositoryAsync();
        _source.WriteSourceFile("notes.txt", "a small one");
        // Random bytes, not generated text: anything with a pattern in it packs
        // down to nothing and the blob comes out a couple of kilobytes, which
        // puts the cut between objects instead of inside one.
        var payload = new byte[LargeObjectBytes];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(_source.SourceRoot, "large.bin"), payload, Timeout);

        await _source.BackUpAsync();
    }

    private async Task<ObjectEntry> LargestObjectAsync()
    {
        var store = new LocalFileSystemObjectStore(_source.RepositoryPath);
        ObjectEntry? biggest = null;
        await foreach (var entry in store.ListAsync(ObjectPrefix.All, ListOptions.Default, Timeout))
        {
            if (biggest is null || entry.Length > biggest.Length)
            {
                biggest = entry;
            }
        }

        Assert.IsNotNull(biggest);
        Assert.IsGreaterThan(
            4L * 1024 * 1024, biggest.Length, "the fixture must hold an object worth resuming");
        return biggest;
    }

    private async Task<ReplicationInitiator.PushOutcome> PushAsync(IObjectStore source, bool offerResume = true)
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_source.PassphraseVariable)!);
        using var repository = await RepositoryLifecycle.OpenAsync(
            new LocalFileSystemObjectStore(_source.RepositoryPath), passphrase, Timeout);

        using var keypair = PeerKeypairStore.Open(_source.StateDirectory);
        var grants = PeerGrantStore.Open(_source.StateDirectory);

        await using var connection = await PeerTlsConnection.DialAsync(
            _endpoint!.Address.ToString(), _endpoint.Port, DateTimeOffset.UtcNow, Timeout);
        var session = await PeerSessionDriver.DialAsync(
            connection, keypair, grants, _destinationIdentity!, "fallbackplan-agent",
            terms: null, requiredFeatures: null, logger: null,
            offeredFeatures: offerResume
                ? null
                : [.. PeerSessionNegotiation.SupportedFeatures.Where(feature => !string.Equals(
                    feature, PeerSessionNegotiation.PartialObjectResumeFeature, StringComparison.Ordinal))],
            cancellationToken: Timeout);

        return await ReplicationInitiator.PushAndConvergeAsync(
            source, repository.RepositoryId.ToArray(), session.Stream, keeps: null, Timeout,
            resumeNegotiated: session.Supports(PeerSessionNegotiation.PartialObjectResumeFeature));
    }

    private async Task AssertReplicaMatchesSourceAsync()
    {
        var replicaPath = Assert.ContainsSingle(
            Directory.GetDirectories(Path.Combine(_destinationState, "replicas")));
        var source = new LocalFileSystemObjectStore(_source.RepositoryPath);
        var replica = new LocalFileSystemObjectStore(replicaPath);

        await foreach (var entry in source.ListAsync(ObjectPrefix.All, ListOptions.Default, Timeout))
        {
            if (entry.Key.Value.StartsWith("tombstones/", StringComparison.Ordinal)
                || entry.Key.Value.StartsWith("leases/", StringComparison.Ordinal))
            {
                continue;
            }

            var mine = await ReadAsync(source, entry.Key);
            var theirs = await ReadAsync(replica, entry.Key);
            Assert.IsNotNull(theirs, $"the replica is missing {entry.Key.Value}");
            Assert.IsTrue(
                mine.AsSpan().SequenceEqual(theirs),
                $"the replica's {entry.Key.Value} is not byte-identical — a resumed object assembled wrongly");
        }
    }

    private async Task<byte[]> ReadAsync(LocalFileSystemObjectStore store, ObjectKey key)
    {
        using var read = await store.OpenReadAsync(key, range: null, Timeout);
        if (read.Outcome != OpenReadOutcome.Found || read.Content is null)
        {
            return [];
        }

        using var buffer = new MemoryStream();
        await read.Content.CopyToAsync(buffer, Timeout);
        return buffer.ToArray();
    }

    private async Task StartDestinationAsync()
    {
        using var sourceKeypair = PeerKeypairStore.Open(_source.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);
        _destinationIdentity = destinationKeypair.Identity;

        var destinationGrants = PeerGrantStore.Open(_destinationState);
        destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));

        var sourceGrants = PeerGrantStore.Open(_source.StateDirectory);
        sourceGrants.Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        var listenerKeypair = PeerKeypairStore.Open(_destinationState);
        _listener = RemoteServiceListener.Start(
            listenerKeypair, destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _destinationState);
        _listener.Bind(new UnusedService());
        _endpoint = _listener.Endpoint;

        await Task.CompletedTask;
    }

    /// <summary>The Bind contract needs a service; the replication path never calls it.</summary>
    private sealed class UnusedService : IFallbackPlanService
    {
        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");
    }
}
