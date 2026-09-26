using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;
using FallbackPlan.Replication;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A peer replica is proved by reading it back, where there is nothing to
/// compare it against (FR-VER-001, FR-VER-002,
/// [ADR-0058](../../docs/adr/0058-peer-write-adapter.md) §8).
/// </summary>
/// <remarks>
/// <para>
/// The wire challenge judges a peer's answer against bytes this side reads for
/// itself, so a direct-ship set whose only destination is that peer cannot use
/// it: the content lives nowhere else. ADR-0058 took the honest way out and
/// stamped nothing, which is right and is not the end of it — such a set then
/// has no proof of its content at all, for ever.
/// </para>
/// <para>
/// The proof that needs no second copy already exists for local paths: open a
/// sampled blob at the replica and authenticate a record inside it under the
/// repository's own key, which the destination has never held. Reaching it
/// through the retrieval session is what this suite is about.
/// </para>
/// <para>
/// The samples come from the peer's own replication inventory, and that is a
/// closed loop rather than letting the peer choose what is examined: a key it
/// omits to avoid being asked about is a key the same session re-ships, so
/// hiding a loss repairs it.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class PeerReadBackVerificationTests : IDisposable
{
    private readonly HostHarness _harness = new();

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-peer-readback", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private const ulong PairedAt = 1_722_600_000_000;

    private CancellationToken Timeout => _timeout.Token;

    private IPEndPoint? _endpoint;
    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;
    private Protocol.PeerIdentity? _destinationIdentity;

    [TestMethod]
    public async Task Pass_APeerHoldingTheOnlyCopy_ProvesItByReadingItBack()
    {
        await SeedAsync();

        var record = DestinationSyncStore.Open(_harness.StateDirectory).Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreEqual(
            DestinationSyncState.InSync, record.State, $"state={record.State} error={record.LastError}");
        Assert.IsNotNull(
            record.VerifiedAt,
            "a set whose only destination is a peer went unproven — the replica can be opened over the "
                + "retrieval session and its records authenticated without any second copy");
        Assert.IsGreaterThan(
            0, record.VerifiedObjects, "the stamp claims a proof drawn from no objects at all");
    }

    [TestMethod]
    public async Task Pass_ABlobRottedAtThePeer_IsAFindingAndNotASuccess()
    {
        // The proof has to bite, or it is a stamp rather than a check. The
        // corruption is length-preserving, which is the case a listing and a
        // length can never catch: only opening the blob and authenticating
        // what is inside it does.
        //
        // It is also broad rather than a single flipped byte. The proof reads
        // ONE record per blob, chosen at random — deliberately, because a
        // fixed choice is one a damaged replica survives for ever — so a
        // single flipped byte is a coin toss rather than a test. Whether this
        // then fails as a container that will not open or as a record whose
        // tag does not hold, both are findings and either is the point.
        await SeedAsync();

        var replica = await ReplicaPathAsync();
        var blobs = Directory.GetFiles(Path.Combine(replica, "blobs", "data"), "*", SearchOption.AllDirectories);
        Assert.IsNotEmpty(blobs, "the fixture must have shipped a data blob to rot");
        foreach (var blob in blobs)
        {
            var bytes = await File.ReadAllBytesAsync(blob, Timeout);
            for (var at = 32; at < bytes.Length - 256; at++)
            {
                bytes[at] ^= 0xFF;
            }

            await File.WriteAllBytesAsync(blob, bytes, Timeout);
        }

        // Driven by the `sync` verb rather than another pass. A converged pair
        // costs nothing on a scheduled pass — that is ADR-0056's whole point —
        // so nothing would look at the replica until the next capture or the
        // next verification interval. An operator asking is the same code path
        // arriving sooner.
        await SyncAsync();

        var record = DestinationSyncStore.Open(_harness.StateDirectory).Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreNotEqual(
            DestinationSyncState.InSync,
            record.State,
            $"every data blob at the peer was rotted and the pass called the destination in sync "
                + $"(verified {record.VerifiedObjects} object(s))");
    }

    [TestMethod]
    public async Task Pass_ARecordHeaderRottedAtThePeer_IsAFindingAndNotSilence()
    {
        // The narrow case, beside the broad one. Sixty-four bytes near the
        // front of a single blob is what an aging disk actually does, and it
        // is a long way from the wholesale rewrite above — the point being
        // that the proof does not need the damage to be extensive, only to be
        // somewhere it reads.
        await SeedAsync();

        var replica = await ReplicaPathAsync();
        var blobs = Directory.GetFiles(Path.Combine(replica, "blobs", "data"), "*", SearchOption.AllDirectories);
        var blob = Assert.ContainsSingle(blobs);
        var bytes = await File.ReadAllBytesAsync(blob, Timeout);
        for (var at = 32; at < 96 && at < bytes.Length; at++)
        {
            bytes[at] ^= 0xFF;
        }

        await File.WriteAllBytesAsync(blob, bytes, Timeout);
        await SyncAsync();

        var record = DestinationSyncStore.Open(_harness.StateDirectory).Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreNotEqual(
            DestinationSyncState.InSync,
            record.State,
            $"a rotted record header at the peer was not a finding (verified {record.VerifiedObjects} object(s))");
    }

    [TestMethod]
    public async Task Pass_AWriteOnlySetsSealedBlobsAtThePeer_AreProvedByDigestAndSaidSo()
    {
        // The digest tier over the wire: every set setup produces is
        // write-only, so a data blob's records are sealed to a key this side
        // does not hold and the tag proof stops at the container. The whole
        // blob is read back over the retrieval session and hashed against
        // the digest the writer signed into the index, and the ledger says
        // that is what proved it. Held here because a digest challenge in
        // which the peer hashes its own copy was considered and refused as a
        // self-report (ADR-0058 §8): the bytes crossing the wire ARE the
        // proof.
        //
        // Pinned to format 2 deliberately, now that creation defaults to
        // format 3: the digest tier is what proves a blob carrying no Merkle
        // commitment, so a repository that publishes one would be proved by
        // chunk and this case would silently stop testing its own subject.
        ServiceRuntime.ArchiveFormatVersion = FallbackPlan.Domain.FormatVersions.SealedDataPlane;
        await SeedAsync();
        await SyncAsync();

        var record = DestinationSyncStore.Open(_harness.StateDirectory).Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreEqual(DestinationSyncState.InSync, record.State, record.LastError);
        Assert.IsGreaterThan(0, record.VerifiedDigest, "the sealed data plane at the peer is proved by digest, and the ledger must say so");
        Assert.IsGreaterThanOrEqualTo(record.VerifiedSealed + record.VerifiedDigest, record.VerifiedObjects);
    }

    [TestMethod]
    public async Task Pass_OneByteRottedUnderASealedRecordAtThePeer_FailsByDigest()
    {
        // The narrowest damage there is, placed where only the digest can
        // see it: inside a sealed record, with the footer left whole. The
        // container opens, the record's tag would refuse but nobody here
        // holds the key to try it, and before the digest tier this was a
        // peer quietly holding damaged bytes for ever while reading in sync.
        await SeedAsync();

        var replica = await ReplicaPathAsync();
        var blob = Assert.ContainsSingle(
            Directory.GetFiles(Path.Combine(replica, "blobs", "data"), "*", SearchOption.AllDirectories));
        var bytes = await File.ReadAllBytesAsync(blob, Timeout);
        bytes[200] ^= 0xFF;
        await File.WriteAllBytesAsync(blob, bytes, Timeout);

        await SyncAsync();

        var record = DestinationSyncStore.Open(_harness.StateDirectory).Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreEqual(
            DestinationSyncState.Failed, record.State,
            $"one rotted byte under a sealed record at the peer must fail by digest (verified {record.VerifiedObjects}, digest {record.VerifiedDigest})");
        Assert.Contains("verification failed", record.LastError!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Pass_TheReadBack_RotatesThroughThePeersInventoryAcrossPasses()
    {
        // A random draw per pass never reaches most of a large inventory and
        // forgets what the byte budget skipped; the read-back walks the same
        // cursor the local path does. The budget is narrowed so the fixture's
        // two blobs are more than one pass's rotation takes: each pass stops
        // after one and records where, and the next resumes after it and
        // closes the circuit — so the recorded cursor alternates between a
        // key and nothing, pass after pass. A read-back that ignored the
        // cursor would record the same first key every time.
        FanOut.ReadBackBudget = VerificationSampler.PeerReservoirShare + 1;
        await SeedAsync();
        var cursors = new List<string?> { Cursor() };

        await SyncAsync();
        cursors.Add(Cursor());
        await SyncAsync();
        cursors.Add(Cursor());

        Assert.IsTrue(cursors.Any(cursor => cursor is not null), "a rotation that could not take everything must say where it stopped");
        Assert.AreNotEqual(cursors[0], cursors[1], $"the second pass must resume after the first: {string.Join(", ", cursors)}");
        Assert.AreNotEqual(cursors[1], cursors[2], $"the third pass must resume after the second: {string.Join(", ", cursors)}");
    }

    [TestMethod]
    public async Task Pass_AFormatThreeSetsSealedBlobsAtThePeer_AreProvedByChunkAndTheBlobDoesNotCross()
    {
        // The whole point of the slice, end to end over the real wire. A
        // format-3 archive publishes a Merkle root per covered blob
        // (07 §2.3), so the read-back can ask the peer for ONE leaf and its
        // authentication path instead of pulling the blob back and hashing
        // it. What is checked is the leaf's bytes against the root the
        // writer signed — the path is public arithmetic the peer could have
        // cached, and proves nothing on its own, which is exactly why the
        // bare digest answer ADR-0058 refuses is a self-report and this is
        // not.
        ServiceRuntime.ArchiveFormatVersion = FallbackPlan.Domain.FormatVersions.RelocatableRecords;
        await SeedAsync();
        await SyncAsync();

        var record = DestinationSyncStore.Open(_harness.StateDirectory).Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreEqual(DestinationSyncState.InSync, record.State, record.LastError);
        Assert.IsGreaterThan(
            0, record.VerifiedChunk,
            "a format-3 set's sealed data plane at the peer is proved by chunk, and the ledger must say so");

        // And the cheap tier really is the one that ran: nothing was proved
        // by hauling a whole blob back.
        Assert.AreEqual(
            0, record.VerifiedDigest,
            "the chunk tier runs ahead of the digest tier, so no blob should have crossed whole");
    }

    [TestMethod]
    public async Task Pass_AFormatThreeBlobRottedUnderASealedRecordAtThePeer_FailsByChunk()
    {
        // The chunk tier has to bite, or it is a cheaper way of stamping
        // nothing. One byte flipped inside a sealed record, footer intact:
        // the container still opens, the tag is unreadable here, and the
        // leaf the peer sends back no longer hashes into the signed root.
        // Which leaf is drawn is random, so the damaged blob's every leaf
        // must be able to catch it — it is small enough to be one leaf.
        ServiceRuntime.ArchiveFormatVersion = FallbackPlan.Domain.FormatVersions.RelocatableRecords;
        await SeedAsync();

        var replica = await ReplicaPathAsync();
        var blob = Assert.ContainsSingle(
            Directory.GetFiles(Path.Combine(replica, "blobs", "data"), "*", SearchOption.AllDirectories));
        var bytes = await File.ReadAllBytesAsync(blob, Timeout);
        bytes[200] ^= 0xFF;
        await File.WriteAllBytesAsync(blob, bytes, Timeout);

        await SyncAsync();

        var record = DestinationSyncStore.Open(_harness.StateDirectory).Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreEqual(
            DestinationSyncState.Failed, record.State,
            $"one rotted byte under a sealed record must fail by chunk (verified {record.VerifiedObjects}, chunk {record.VerifiedChunk})");
        Assert.Contains("verification failed", record.LastError!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheProtocolsLeafBound_IsTheFormatsLeafSize()
    {
        // Two constants arrived at independently — the format fixes the leaf
        // at one mebibyte (05 §5.2) and the protocol bounds a chunk at the
        // same number — and a proof that carried a whole leaf would be
        // refused at the wire if they ever parted.
        Assert.AreEqual(
            FallbackPlan.Repository.Crypto.BlobMerkle.LeafSize, MerkleProof.MaximumLeafBytes);
    }

    private string? Cursor()
    {
        var record = DestinationSyncStore.Open(_harness.StateDirectory).Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record?.VerifiedAt, record?.LastError);
        return record.SampleCursor;
    }

    private async Task SeedAsync()
    {
        await _harness.SetupAsync();
        await StartDestinationAsync();
        WriteConfiguration();
        _harness.WriteSourceFile("docs/report.txt", new string('r', 200_000));

        await RunPassAsync();
        _ = await ReplicaPathAsync();
    }

    /// <summary>
    /// The `sync` verb. A converged pair costs nothing on a scheduled pass —
    /// that is ADR-0056's whole point — so nothing would look at the replica
    /// until the next capture or the next verification interval. An operator
    /// asking is the same code path arriving sooner.
    /// </summary>
    private async Task SyncAsync()
    {
        var sync = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "sync", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory
            );
        Assert.AreEqual(0, sync.ExitCode, sync.Error);
    }

    private async Task RunPassAsync()
    {
        var run = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "run", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--once");
        Assert.AreEqual(0, run.ExitCode, run.Error);
    }

    private async Task<string> ReplicaPathAsync()
    {
        var replicas = Path.Combine(_destinationState, "replicas");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var directories = Directory.Exists(replicas) ? Directory.GetDirectories(replicas) : [];
            if (directories.Length == 1 && Directory.Exists(Path.Combine(directories[0], "snapshots")))
            {
                return directories[0];
            }

            Assert.IsTrue(DateTimeOffset.UtcNow < deadline, "the destination never took a replica");
            await Task.Delay(100, Timeout);
        }
    }

    private void WriteConfiguration() => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32),
                Name = "friend",
                Kind = DestinationKind.Peer,
                Fingerprint = _destinationIdentity!.Fingerprint,
                Endpoint = $"{_endpoint!.Address}:{_endpoint.Port}",
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = _harness.DocsSetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                Schedule = "every 1h",
                Destinations = [new SetDestinationReference { Ref = "friend" }],
                DirectShip = true,
            },
        ],
    }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    private async Task StartDestinationAsync()
    {
        using var sourceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);
        _destinationIdentity = destinationKeypair.Identity;

        var destinationGrants = PeerGrantStore.Open(_destinationState);
        destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));

        PeerGrantStore.Open(_harness.StateDirectory).Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        _listenerKeypair = PeerKeypairStore.Open(_destinationState);
        _listener = RemoteServiceListener.Start(
            _listenerKeypair, destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
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

    public void Dispose()
    {
        FanOut.ReadBackBudget = VerificationSampler.DefaultBudget;
        ServiceRuntime.ArchiveFormatVersion = FallbackPlan.Domain.FormatLimits.FormatVersion;
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
            // A test directory that will not delete is not a test failure.
        }
    }
}
