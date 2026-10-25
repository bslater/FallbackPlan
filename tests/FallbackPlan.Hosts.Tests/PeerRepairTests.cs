using System.Net;
using System.Security.Cryptography;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// What a peer will take under a repair (specification peer-protocol 08,
/// ADR-0100, FR-VER-008), asked over the wire by the replica's owner: a
/// replacement for a blob only where its own copy fails the locator check
/// it can make without a key, only of the length its copy is, and only bytes
/// that pass the same check and state the locator its copy states. Anything
/// else leaves its copy as it was, and the verdict says which rule refused.
/// </summary>
/// <remarks>
/// The peer holds no key and takes no instruction on trust, so these are the
/// rules that make a repair safe to accept from a source that might be
/// buggy or taken over: they let it put back only the bytes that were
/// written, and only where they are no longer there.
/// </remarks>
[TestClass]
public sealed class PeerRepairTests : IDisposable
{
    private const ulong PairedAt = 1_722_600_000_000;

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-peer-repair", Guid.NewGuid().ToString("n"));

    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;

    private CancellationToken Timeout => _timeout.Token;

    [TestMethod]
    public async Task ACopyThePeersOwnCheckPasses_IsAnsweredIntact_AndNothingIsAsked()
    {
        await using var runtime = await StartAsync();
        var (replica, repositoryId) = await ReplicatedAsync(runtime);
        var key = FirstDataBlob(replica);
        var held = await File.ReadAllBytesAsync(PathOf(replica, key), Timeout);
        await using var repair = await DialAsync(runtime, repositoryId);

        Assert.AreEqual(RepairVerdictCode.Intact, await repair.OfferAsync(key, held.Length, Timeout));

        CollectionAssert.AreEqual(held, await File.ReadAllBytesAsync(PathOf(replica, key), Timeout));
    }

    [TestMethod]
    public async Task ACopyThatFailsThePeersCheck_IsReplacedByTheBytesThatWereWritten()
    {
        await using var runtime = await StartAsync();
        var (replica, repositoryId) = await ReplicatedAsync(runtime);
        var key = FirstDataBlob(replica);
        var sealedBytes = await File.ReadAllBytesAsync(PathOf(_harness.RepositoryPath, key), Timeout);
        Rot(replica, key);
        await using var repair = await DialAsync(runtime, repositoryId);

        Assert.AreEqual(RepairVerdictCode.Send, await repair.OfferAsync(key, sealedBytes.Length, Timeout));
        Assert.AreEqual(RepairVerdictCode.Replaced, await SendAsync(repair, key, sealedBytes));

        CollectionAssert.AreEqual(sealedBytes, await File.ReadAllBytesAsync(PathOf(replica, key), Timeout));
    }

    [TestMethod]
    public async Task OneSession_RepairsEachBlobItIsOffered_InTurn()
    {
        await using var runtime = await StartAsync();
        await BackUpAsync(runtime);
        _harness.WriteSourceFile("docs/second.txt", "a second capture, so the replica spans more blobs");
        var (replica, repositoryId) = await ReplicatedAsync(runtime);
        var keys = DataBlobs(replica).Where(key => File.Exists(PathOf(_harness.RepositoryPath, key))).ToList();
        Assert.IsGreaterThanOrEqualTo(2, keys.Count, "the control: two blobs to repair");
        foreach (var key in keys)
        {
            Rot(replica, key);
        }

        await using var repair = await DialAsync(runtime, repositoryId);
        foreach (var key in keys)
        {
            var sealedBytes = await File.ReadAllBytesAsync(PathOf(_harness.RepositoryPath, key), Timeout);
            Assert.AreEqual(RepairVerdictCode.Send, await repair.OfferAsync(key, sealedBytes.Length, Timeout));
            Assert.AreEqual(RepairVerdictCode.Replaced, await SendAsync(repair, key, sealedBytes));
            CollectionAssert.AreEqual(sealedBytes, await File.ReadAllBytesAsync(PathOf(replica, key), Timeout));
        }
    }

    [TestMethod]
    public async Task AReplacementThatStatesAnotherLocator_IsRefused_AndTheDamagedCopyStays()
    {
        // Consistent with itself and not the blob that was sealed: the one
        // thing that tells the peer so is the locator its own damaged copy
        // still states.
        await using var runtime = await StartAsync();
        var (replica, repositoryId) = await ReplicatedAsync(runtime);
        var key = FirstDataBlob(replica);
        var sealedBytes = await File.ReadAllBytesAsync(PathOf(_harness.RepositoryPath, key), Timeout);
        var damaged = Rot(replica, key);
        await using var repair = await DialAsync(runtime, repositoryId);

        Assert.AreEqual(RepairVerdictCode.Send, await repair.OfferAsync(key, sealedBytes.Length, Timeout));
        Assert.AreEqual(RepairVerdictCode.Unmatched, await SendAsync(repair, key, Reseal(sealedBytes)));

        CollectionAssert.AreEqual(damaged, await File.ReadAllBytesAsync(PathOf(replica, key), Timeout));
    }

    [TestMethod]
    public async Task AReplacementThatFailsTheCheck_IsRefused_AndTheDamagedCopyStays()
    {
        await using var runtime = await StartAsync();
        var (replica, repositoryId) = await ReplicatedAsync(runtime);
        var key = FirstDataBlob(replica);
        var sealedBytes = await File.ReadAllBytesAsync(PathOf(_harness.RepositoryPath, key), Timeout);
        var damaged = Rot(replica, key);
        var alsoDamaged = (byte[])sealedBytes.Clone();
        alsoDamaged[alsoDamaged.Length / 3] ^= 0x01;
        await using var repair = await DialAsync(runtime, repositoryId);

        Assert.AreEqual(RepairVerdictCode.Send, await repair.OfferAsync(key, sealedBytes.Length, Timeout));
        Assert.AreEqual(RepairVerdictCode.Unmatched, await SendAsync(repair, key, alsoDamaged));

        CollectionAssert.AreEqual(damaged, await File.ReadAllBytesAsync(PathOf(replica, key), Timeout));
    }

    [TestMethod]
    public async Task ACopyOfAnotherLength_IsAnsweredLengthDiffers_AndNothingIsAsked()
    {
        // A copy that has changed length has lost the place its locator was,
        // so nothing on it names the blob a replacement would have to be.
        await using var runtime = await StartAsync();
        var (replica, repositoryId) = await ReplicatedAsync(runtime);
        var key = FirstDataBlob(replica);
        var sealedBytes = await File.ReadAllBytesAsync(PathOf(_harness.RepositoryPath, key), Timeout);
        var truncated = sealedBytes[..(sealedBytes.Length / 2)];
        await File.WriteAllBytesAsync(PathOf(replica, key), truncated, Timeout);
        await using var repair = await DialAsync(runtime, repositoryId);

        Assert.AreEqual(RepairVerdictCode.LengthDiffers, await repair.OfferAsync(key, sealedBytes.Length, Timeout));

        CollectionAssert.AreEqual(truncated, await File.ReadAllBytesAsync(PathOf(replica, key), Timeout));
    }

    [TestMethod]
    public async Task AKeyThePeerDoesNotHold_IsAnsweredNotHeld_AndNothingIsCreated()
    {
        // A repair replaces; it never creates. Creating is the push's, under
        // the quota the push enforces (05).
        await using var runtime = await StartAsync();
        var (replica, repositoryId) = await ReplicatedAsync(runtime);
        var key = FirstDataBlob(replica);
        var sealedBytes = await File.ReadAllBytesAsync(PathOf(_harness.RepositoryPath, key), Timeout);
        File.Delete(PathOf(replica, key));
        await using var repair = await DialAsync(runtime, repositoryId);

        Assert.AreEqual(RepairVerdictCode.NotHeld, await repair.OfferAsync(key, sealedBytes.Length, Timeout));

        Assert.IsFalse(File.Exists(PathOf(replica, key)));
    }

    [TestMethod]
    public async Task ARepairNamingAnythingButABlob_IsRefusedAsMalformed()
    {
        // The locator check is a blob's. Everything else a replica holds has
        // nothing a keyless holder could check a replacement against.
        await using var runtime = await StartAsync();
        var (replica, repositoryId) = await ReplicatedAsync(runtime);
        var snapshot = Directory
            .GetFiles(Path.Combine(replica, "snapshots"), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(replica, path).Replace(Path.DirectorySeparatorChar, '/'))
            .First();
        await using var repair = await DialAsync(runtime, repositoryId);

        var refusal = await Assert.ThrowsExactlyAsync<PeerProtocolException>(
            async () => await repair.OfferAsync(snapshot, 64, Timeout));
        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }

    [TestMethod]
    public async Task ARepairOfARepositoryThePeerDoesNotHoldForThisOwner_IsRefused()
    {
        await using var runtime = await StartAsync();
        var (replica, _) = await ReplicatedAsync(runtime);
        var key = FirstDataBlob(replica);
        await using var repair = await DialAsync(runtime, RandomNumberGenerator.GetBytes(16));

        var refusal = await Assert.ThrowsExactlyAsync<PeerProtocolException>(
            async () => await repair.OfferAsync(key, 64, Timeout));
        Assert.AreEqual(PeerRefusalReason.TermsRefused, refusal.Reason);
    }

    [TestMethod]
    public async Task APeerWhoseBuildTakesNoRepair_IsNotAskedForOne()
    {
        // Gated on the feature (02 §6), so an older build is never sent a
        // message it would refuse as unknown: the session is refused at the
        // hello instead, before anything is offered.
        await using var runtime = await StartAsync(
            offeredFeatures: [.. PeerSessionNegotiation.SupportedFeatures.Where(
                feature => feature != PeerSessionNegotiation.ReplicaRepairFeature)]);
        var (_, repositoryId) = await ReplicatedAsync(runtime);

        var refusal = await Assert.ThrowsExactlyAsync<PeerProtocolException>(
            async () => await DialAsync(runtime, repositoryId));
        Assert.AreEqual(PeerRefusalReason.FeatureUnsupported, refusal.Reason);
    }

    public void Dispose()
    {
        _listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _listenerKeypair?.Dispose();
        _timeout.Dispose();
        _harness.Dispose();

        try
        {
            if (Directory.Exists(_destinationState))
            {
                Directory.Delete(_destinationState, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: a test directory that will not delete is not a test failure.
        }
    }

    private Task<PeerRepairClient> DialAsync(ServiceRuntime runtime, ReadOnlyMemory<byte> repositoryId) =>
        PeerRepairClient.DialAsync(
            runtime, runtime.Configuration.FindDestination("friend")!, repositoryId, limiter: null, Timeout);

    private async Task<RepairVerdictCode> SendAsync(PeerRepairClient repair, string key, byte[] bytes)
    {
        using var content = new MemoryStream(bytes, writable: false);
        return await repair.SendAsync(key, content, bytes.Length, Timeout);
    }

    /// <summary>One byte changed and the digest prefix recomputed: well formed, and not the blob sealed.</summary>
    private static byte[] Reseal(byte[] blob)
    {
        var forged = (byte[])blob.Clone();
        forged[forged.Length / 2] ^= 0x01;
        var digest = SHA256.HashData(forged.AsSpan(0, forged.Length - 16));
        digest.AsSpan(0, 8).CopyTo(forged.AsSpan(forged.Length - 8));
        return forged;
    }

    /// <summary>Backs the set up and pushes it, and answers the peer's replica and the repository it holds.</summary>
    private async Task<(string Replica, byte[] RepositoryId)> ReplicatedAsync(ServiceRuntime runtime)
    {
        var set = runtime.Configuration.BackupSets.Single();
        await BackUpAsync(runtime);
        var sync = FanOut.Enqueue(runtime, set, "friend", DateTimeOffset.Now, userInitiated: true);
        Assert.IsNotNull(sync);
        await sync.WaitAsync(Timeout);
        var row = runtime.DestinationSync.Find(set.Id, "friend");
        Assert.AreEqual(DestinationSyncState.InSync, row?.State, row?.LastError);

        var replicas = Path.Combine(_destinationState, "replicas");
        var replica = Assert.ContainsSingle(Directory.GetDirectories(replicas));
        return (replica, Convert.FromHexString(Path.GetFileName(replica)));
    }

    private async Task BackUpAsync(ServiceRuntime runtime)
    {
        var outcome = await Scheduler.Enqueue(
            runtime, runtime.Configuration.BackupSets.Single(), DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private static List<string> DataBlobs(string replica) =>
        [.. Directory.GetFiles(Path.Combine(replica, "blobs", "data"), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(replica, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

    private string FirstDataBlob(string replica) =>
        DataBlobs(replica).First(key => File.Exists(PathOf(_harness.RepositoryPath, key)));

    private static string PathOf(string root, string key) =>
        Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Flips one byte in the middle of the peer's copy, and answers what it now holds.</summary>
    private static byte[] Rot(string replica, string key)
    {
        var path = PathOf(replica, key);
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        return bytes;
    }

    private async Task<ServiceRuntime> StartAsync(IReadOnlyList<string>? offeredFeatures = null)
    {
        _harness.WriteSourceFile("docs/report.txt", "the words worth keeping");
        _harness.WriteSourceFile("docs/big.bin", new string('b', 300_000));
        var fingerprint = StartPeer(offeredFeatures);

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('f', 32), Name = "friend", Kind = DestinationKind.Peer,
                    Fingerprint = fingerprint, Endpoint = $"{_listener!.Endpoint.Address}:{_listener.Endpoint.Port}",
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Destinations = [new SetDestinationReference { Ref = "friend" }],
                    DirectShip = false,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

        await _harness.SetupAsync();
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            Timeout);
    }

    /// <summary>A paired destination listening on loopback, as the peer suites stand one up.</summary>
    private string StartPeer(IReadOnlyList<string>? offeredFeatures)
    {
        using var sourceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);

        var destinationGrants = PeerGrantStore.Open(_destinationState);
        destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));
        PeerGrantStore.Open(_harness.StateDirectory).Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        _listenerKeypair = PeerKeypairStore.Open(_destinationState);
        _listener = RemoteServiceListener.Start(
            _listenerKeypair, destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _destinationState, offeredFeatures: offeredFeatures);
        _listener.Bind(new UnusedService());
        return destinationKeypair.Identity.Fingerprint;
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
