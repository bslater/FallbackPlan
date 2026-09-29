using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A restore that meets a damaged copy reads it from a sound one
/// (FR-RST-007). The default restore — the set's own archive, with no
/// destination named — tries the staging archive or the set's read path
/// first, then the set's local-path destinations by priority, then its peers
/// over the retrieval session, each only when every earlier copy has failed.
/// What it reads is verified exactly as the first copy's would have been
/// (FR-RST-002), and with every copy failed the file still fails
/// (FR-RST-005). The answer and the receipt say which files were read around
/// damage and from where (FR-RST-004), damage found at a destination goes on
/// the ledger and raises a notice so the next sync can repair it or the
/// peer's owner can remove it (FR-VER-005, FR-VER-007), and a plan counts a
/// file as missing only when no copy holds it (FR-RST-003).
/// </summary>
/// <remarks>
/// <para>
/// A restore from a named destination reads that destination alone. The
/// recovery drill restores through the same verbs from the destination it is
/// drilling, to prove that copy restores (ADR-0054); read around, a drill of
/// a rotted destination would pass.
/// </para>
/// <para>
/// The content is random and large enough that every record reaches past the
/// first two hundred bytes of its blob, which is where the tampering starts:
/// the envelope survives, so a copy fails on its records rather than on its
/// framing, which is the damage a restore meets record by record.
/// </para>
/// </remarks>
[TestClass]
public sealed class RestoreReadAroundTests : IDisposable
{
    private const ulong PairedAt = 1_722_600_000_000;

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-read-around-peer", Guid.NewGuid().ToString("n"));

    private readonly Dictionary<string, string> _written = new(StringComparer.Ordinal);

    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string Spare => Path.Combine(_harness.WorkPath, "spare");

    [TestMethod]
    public async Task Restore_ADirectShipSetWhoseFirstDestinationRotted_RestoresFromTheOther_AndSaysSo()
    {
        // The default shape for a new local-path set: its blobs are read back
        // from the highest-priority destination holding them, which here is
        // the rotted one.
        await using var runtime = await StartAsync(directShip: true, ("vault", 10), ("spare", 1));
        await BackUpAsync(runtime);
        TamperEveryDataBlob(ReplicaRoot(Vault));

        var restored = await RestoreAsync(runtime);

        Assert.AreEqual("complete", restored.Outcome, string.Join(" | ", restored.FailedSample ?? []));
        await AssertRestoredAsync(restored);
        Assert.AreEqual(_written.Count, restored.ReadAround, "every file's content was read around the rotted copy");
        var line = restored.ReadAroundSample![0];
        Assert.Contains("destination 'spare'", line, StringComparison.Ordinal);
        Assert.Contains("destination 'vault'", line, StringComparison.Ordinal);

        var receipt = await File.ReadAllTextAsync(restored.ReceiptPath!, Timeout);
        Assert.Contains("\"read_from\"", receipt, StringComparison.Ordinal);
        Assert.Contains("\"read_around\"", receipt, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Restore_AStagingSetWhoseStagingCopyRotted_RestoresFromItsDestination_AndSaysSo()
    {
        await using var runtime = await StartAsync(directShip: false, ("vault", null));
        await BackUpAsync(runtime);
        await PassAsync(runtime, DateTimeOffset.Now);
        TamperEveryDataBlob(_harness.RepositoryPath);

        var restored = await RestoreAsync(runtime);

        Assert.AreEqual("complete", restored.Outcome, string.Join(" | ", restored.FailedSample ?? []));
        await AssertRestoredAsync(restored);
        var line = restored.ReadAroundSample![0];
        Assert.Contains("destination 'vault'", line, StringComparison.Ordinal);
        Assert.Contains("the staging archive", line, StringComparison.Ordinal);

        // Nothing repairs a staging archive in place, so a person hears of it.
        var notice = Notice(runtime, "staging");
        Assert.Contains("the staging archive", notice.Message, StringComparison.Ordinal);
        Assert.Contains("no longer match what was sealed", notice.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Restore_EveryCopyRotted_StillFails_NamingEachCopyTried()
    {
        await using var runtime = await StartAsync(directShip: true, ("vault", 10), ("spare", 1));
        await BackUpAsync(runtime);
        TamperEveryDataBlob(ReplicaRoot(Vault));
        TamperEveryDataBlob(ReplicaRoot(Spare));

        var restored = await RestoreAsync(runtime);

        Assert.AreEqual("failed", restored.Outcome);
        Assert.AreEqual(0, restored.ReadAround);
        var failure = restored.FailedSample![0];
        Assert.Contains("destination 'vault'", failure, StringComparison.Ordinal);
        Assert.Contains("destination 'spare'", failure, StringComparison.Ordinal);
        Assert.IsEmpty(Directory.GetFiles(restored.OutputDirectory, "*.txt", SearchOption.AllDirectories), "no file is written that did not verify");
    }

    [TestMethod]
    public async Task Restore_DamageItReadAround_GoesOnTheLedger_AndTheNextSyncRepairsIt()
    {
        // A restore becomes a detector too: what it found damaged is held
        // against the pair, and the sync's re-check replaces it from the copy
        // the restore proved sound.
        await using var runtime = await StartAsync(directShip: true, ("vault", 10), ("spare", 1));
        await BackUpAsync(runtime);
        var damaged = TamperEveryDataBlob(ReplicaRoot(Vault));

        var restored = await RestoreAsync(runtime);
        Assert.AreEqual("complete", restored.Outcome, string.Join(" | ", restored.FailedSample ?? []));

        var found = Row(runtime, "vault");
        Assert.AreEqual(DestinationSyncState.Failed, found.State);
        CollectionAssert.IsSubsetOf(found.DamagedKeys!.ToList(), damaged);
        Assert.IsNotEmpty(found.DamagedKeys!);
        Assert.IsNull(Row(runtime, "spare").DamagedKeys, "the copy that served is not blamed");

        var notice = Notice(runtime, "vault");
        Assert.Contains("destination 'vault'", notice.Message, StringComparison.Ordinal);
        Assert.Contains("check the device", notice.Message, StringComparison.Ordinal);

        await PassAsync(runtime, DateTimeOffset.Now.AddHours(2));

        var repaired = Row(runtime, "vault");
        Assert.IsNull(repaired.DamagedKeys, repaired.LastError);
        Assert.AreEqual(DestinationSyncState.InSync, repaired.State, repaired.LastError);
        foreach (var key in found.DamagedKeys!)
        {
            CollectionAssert.AreEqual(
                await File.ReadAllBytesAsync(PathOf(ReplicaRoot(Spare), key), Timeout),
                await File.ReadAllBytesAsync(PathOf(ReplicaRoot(Vault), key), Timeout),
                "the sync must have put the sound copy in the damaged one's place");
        }
    }

    [TestMethod]
    public async Task Restore_FromANamedDestination_ReadsThatCopyAlone()
    {
        await using var runtime = await StartAsync(directShip: true, ("vault", 10), ("spare", 1));
        await BackUpAsync(runtime);
        TamperEveryDataBlob(ReplicaRoot(Vault));

        var restored = await RestoreAsync(runtime, destination: "vault");

        Assert.AreEqual("failed", restored.Outcome, "a person who asked for that copy is told what that copy holds");
        Assert.AreEqual(0, restored.ReadAround);
        Assert.DoesNotContain("spare", string.Join(" | ", restored.FailedSample!), StringComparison.Ordinal);
        Assert.IsNull(Row(runtime, "vault").DamagedKeys, "a named restore is the stranger's view, and changes nothing");
    }

    [TestMethod]
    public async Task Drill_OfADestinationHoldingRot_StillFails_ThoughASiblingIsSound()
    {
        // The reason a named source is read alone. A guard: it held before
        // the default restore could read around anything, and must go on
        // holding now that it can.
        await using var runtime = await StartAsync(directShip: true, ("vault", 10), ("spare", 1));
        await BackUpAsync(runtime);
        TamperEveryDataBlob(ReplicaRoot(Vault));

        var outcome = await RecoveryDrillJob.RunAsync(
            runtime, runtime.Configuration.BackupSets.Single(), "vault",
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Timeout);

        Assert.IsNotNull(outcome.Failure, "a drill of a rotted destination must fail, whatever its sibling holds");
    }

    [TestMethod]
    public async Task Restore_ADirectShipSetWhoseOnlyLocalCopyRotted_IsReadFromThePeer()
    {
        // The household shape: a drive by the machine, and a friend's house.
        // The retrieval session that serves a restore from the peer serves
        // this too, dialled only once the drive's copy has failed.
        var fingerprint = StartPeer();
        await using var runtime = await StartAsync(directShip: true, [("vault", null)], fingerprint);
        await BackUpAsync(runtime);
        await PeerReplicaAsync();
        TamperEveryDataBlob(ReplicaRoot(Vault));

        var restored = await RestoreAsync(runtime);

        Assert.AreEqual("complete", restored.Outcome, string.Join(" | ", restored.FailedSample ?? []));
        await AssertRestoredAsync(restored);
        Assert.Contains("destination 'friend'", restored.ReadAroundSample![0], StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Restore_APeerCopyFoundDamaged_IsHeldAgainstThePair_AndNamesTheRemedy()
    {
        // Nothing here can write at a peer (ADR-0035 Amendment 2), so the
        // damage a restore finds there stands until the peer's owner removes
        // it — and the notice says exactly what to remove.
        var fingerprint = StartPeer();
        await using var runtime = await StartAsync(directShip: true, [("vault", null)], fingerprint);
        await BackUpAsync(runtime);
        var peerReplica = await PeerReplicaAsync();
        TamperEveryDataBlob(ReplicaRoot(Vault));
        TamperEveryDataBlob(peerReplica);

        var restored = await RestoreAsync(runtime);

        Assert.AreEqual("failed", restored.Outcome);
        var row = Row(runtime, "friend");
        Assert.IsNotNull(row.DamagedKeys, row.LastError);
        Assert.AreEqual(DestinationSyncState.Failed, row.State);
        var notice = Notice(runtime, "friend");
        Assert.Contains("Ask the owner of that machine", notice.Message, StringComparison.Ordinal);
        Assert.Contains(row.DamagedKeys[0], notice.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Plan_AStagingSetWhoseStagingNoLongerHoldsTheContent_CallsNothingMissing_AndTheRunReadsItFromTheDestination()
    {
        // What a staging trim leaves (ADR-0034 §6): the data of history,
        // held at every destination and no longer in staging. The plan used
        // to call those files missing and the run to fail them, leaving a
        // person to know that the destination was the restore path.
        await using var runtime = await StartAsync(directShip: false, ("vault", null));
        await BackUpAsync(runtime);
        await PassAsync(runtime, DateTimeOffset.Now);
        foreach (var blob in Directory.GetFiles(
            Path.Combine(_harness.RepositoryPath, "blobs", "data"), "*", SearchOption.AllDirectories))
        {
            File.Delete(blob);
        }

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, Timeout);
        Assert.IsInstanceOfType<RestorePlanResult>(
            await handler.ExecuteAsync(new PlanRestoreCommand(await SnapshotAsync(handler), null, Source: source.SourceId), Timeout),
            out var plan);
        Assert.IsEmpty(plan.MissingObjects, string.Join(", ", plan.MissingObjects));

        var restored = await RestoreAsync(runtime);
        Assert.AreEqual("complete", restored.Outcome, string.Join(" | ", restored.FailedSample ?? []));
        await AssertRestoredAsync(restored);
        Assert.AreEqual(0, restored.ReadAround, "a copy staging was meant to let go of is nothing to warn about");
        Assert.IsFalse(
            runtime.Notices.Unacknowledged.Any(notice => notice.Key.StartsWith("restore-found-damage:", StringComparison.Ordinal)),
            "nothing was damaged");
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

    private static DestinationSyncRecord Row(ServiceRuntime runtime, string destination) =>
        runtime.DestinationSync.Find(runtime.Configuration.BackupSets.Single().Id, destination)
        ?? throw new AssertFailedException($"'{destination}' has no ledger row");

    private static Notice Notice(ServiceRuntime runtime, string where) =>
        runtime.Notices.Unacknowledged.SingleOrDefault(notice =>
            notice.Key == $"restore-found-damage:{runtime.Configuration.BackupSets.Single().Id}:{where}")
        ?? throw new AssertFailedException(
            $"no restore notice stands for '{where}': "
            + string.Join(" | ", runtime.Notices.Unacknowledged.Select(notice => notice.Key)));

    private async Task<string> SnapshotAsync(ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        return Assert.ContainsSingle(listed.Snapshots).SnapshotId;
    }

    /// <summary>
    /// A guided restore of the whole snapshot through the contract, as the
    /// console runs one: open a source, restore, close it.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="destination">A destination to restore from by name, or null for the set's own archive.</param>
    private async Task<RestoreResult> RestoreAsync(ServiceRuntime runtime, string? destination = null)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var snapshot = await SnapshotAsync(handler);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", destination, Timeout);
        var output = Path.Combine(_harness.WorkPath, "restored-" + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            var result = await handler.ExecuteAsync(
                new RunRestoreCommand(snapshot, null, output, Source: source.SourceId), Timeout);
            Assert.IsInstanceOfType<RestoreResult>(result, out var restored, (result as ServiceError)?.Message);
            return restored;
        }
        finally
        {
            await handler.ExecuteAsync(new CloseRestoreSourceCommand(source.SourceId), Timeout);
        }
    }

    /// <summary>Every file written at the start is back, byte for byte.</summary>
    private async Task AssertRestoredAsync(RestoreResult restored)
    {
        foreach (var (relative, content) in _written)
        {
            var found = Assert.ContainsSingle(
                Directory.GetFiles(restored.OutputDirectory, Path.GetFileName(relative), SearchOption.AllDirectories));
            Assert.AreEqual(content, await File.ReadAllTextAsync(found, Timeout), $"'{relative}' did not come back whole");
        }
    }

    private async Task BackUpAsync(ServiceRuntime runtime)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private async Task PassAsync(ServiceRuntime runtime, DateTimeOffset now)
    {
        var pass = await Scheduler.RunPassAsync(runtime, now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
    }

    /// <summary>The one repository directory a destination path holds.</summary>
    private static string ReplicaRoot(string destinationPath) =>
        Assert.ContainsSingle(Directory.GetDirectories(destinationPath));

    private static string PathOf(string root, string key) =>
        Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Flips every byte of every data blob under <paramref name="repositoryRoot"/>
    /// past its first two hundred: the envelope survives, every record does not.
    /// </summary>
    /// <returns>The store keys of the blobs tampered with.</returns>
    private static List<string> TamperEveryDataBlob(string repositoryRoot)
    {
        var files = Directory.GetFiles(Path.Combine(repositoryRoot, "blobs", "data"), "*", SearchOption.AllDirectories);
        Assert.IsNotEmpty(files, "the copy must hold data blobs for this to test anything");

        foreach (var path in files)
        {
            var bytes = File.ReadAllBytes(path);
            for (var i = 200; i < bytes.Length; i++)
            {
                bytes[i] ^= 0xFF;
            }

            File.WriteAllBytes(path, bytes);
        }

        return [.. files.Select(path => Path.GetRelativePath(repositoryRoot, path).Replace(Path.DirectorySeparatorChar, '/'))];
    }

    private void WriteSource(string relative, int seed, int bytes)
    {
        var random = new byte[bytes];
        new Random(seed).NextBytes(random);
        var content = Convert.ToBase64String(random);
        _harness.WriteSourceFile(relative, content);
        _written[relative] = content;
    }

    private async Task<ServiceRuntime> StartAsync(
        bool directShip, params (string Name, int? Priority)[] localPaths) =>
        await StartAsync(directShip, localPaths, peerFingerprint: null);

    private async Task<ServiceRuntime> StartAsync(
        bool directShip, (string Name, int? Priority)[] localPaths, string? peerFingerprint)
    {
        WriteSource("docs/report.txt", 11, 120_000);
        WriteSource("docs/notes.txt", 12, 60_000);

        var destinations = new List<DestinationConfiguration>();
        var references = new List<SetDestinationReference>();
        var id = 1;
        foreach (var (name, priority) in localPaths)
        {
            var path = Path.Combine(_harness.WorkPath, name);
            Directory.CreateDirectory(path);
            destinations.Add(new DestinationConfiguration
            {
                Id = new string((char)('0' + id++), 32), Name = name, Kind = DestinationKind.LocalPath, Path = path,
                Priority = priority,
            });
            references.Add(new SetDestinationReference { Ref = name });
        }

        if (peerFingerprint is not null)
        {
            destinations.Add(new DestinationConfiguration
            {
                Id = new string('f', 32), Name = "friend", Kind = DestinationKind.Peer,
                Fingerprint = peerFingerprint, Endpoint = $"{_listener!.Endpoint.Address}:{_listener.Endpoint.Port}",
            });
            references.Add(new SetDestinationReference { Ref = "friend" });
        }

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = destinations,
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Destinations = references,
                    DirectShip = directShip,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

        await _harness.SetupAsync();
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                // The placement condition (ADR-0051) judges by volume, and the
                // fixture's every path shares one real volume — the
                // destinations are told apart by name.
                VolumeIdentityOverride = path =>
                    path.Contains("vault", StringComparison.Ordinal) ? 2UL
                    : path.Contains("spare", StringComparison.Ordinal) ? 3UL
                    : 1UL,
            },
            Timeout);
    }

    /// <summary>A paired destination listening on loopback, as the peer suites stand one up.</summary>
    private string StartPeer()
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
            log: null, replicationStateDirectory: _destinationState);
        _listener.Bind(new UnusedService());
        return destinationKeypair.Identity.Fingerprint;
    }

    /// <summary>
    /// The peer's replica directory, waited for: the destination finishes
    /// committing its side after the sender has already returned.
    /// </summary>
    private async Task<string> PeerReplicaAsync()
    {
        var replicas = Path.Combine(_destinationState, "replicas");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var directories = Directory.Exists(replicas) ? Directory.GetDirectories(replicas) : [];
            if (directories.Length == 1 && Directory.Exists(Path.Combine(directories[0], "blobs", "data")))
            {
                return directories[0];
            }

            Assert.IsTrue(DateTimeOffset.UtcNow < deadline, "the peer never held the set's blobs");
            await Task.Delay(100, Timeout);
        }
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
