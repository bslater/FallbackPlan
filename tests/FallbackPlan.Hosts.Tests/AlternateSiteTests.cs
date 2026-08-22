using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The alternative-site story, whole, as an operator lives it: two live
/// services; pairing by spoken invite over the command surface (ADR-0030
/// Amendment 4); the destination and the set configured over the contract
/// (ADR-0037); a commanded backup fanning out to the other site on its own;
/// possession proven by the wire challenge, never assumed; and the claim that
/// justifies all of it — the source machine's archive can be destroyed and the
/// data still comes back, byte-identical and point-in-time, from the other
/// site plus the recovery kit alone.
/// </summary>
/// <remarks>
/// One long test rather than nine small ones, deliberately: "an alternative
/// site works" is a chain, and a chain is proven by pulling on the whole of
/// it. Each link still asserts by name, so a break says which link.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class AlternateSiteTests : IDisposable
{
    private readonly HostHarness _siteOne = new();
    private readonly HostHarness _siteTwo = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    public void Dispose()
    {
        _timeout.Dispose();
        _siteOne.Dispose();
        _siteTwo.Dispose();
    }

    [TestMethod]
    public async Task AlternateSite_PairedByInviteAndBackedUpTo_RestoresTheSourceAfterItsLoss()
    {
        // ---- Site A: the household machine with the data.
        await _siteOne.CreateRepositoryAsync();
        var originalNotes = "the original notes";
        _siteOne.WriteSourceFile("notes.txt", originalNotes);
        _siteOne.WriteSourceFile("nested/data.bin", new string('é', 4_096) + "binary-ish payload");
        _siteOne.WriteSourceFile("photos/beach.jpg", new string('p', 64_000));
        var kit = await _siteOne.ExportKitAsync();

        // ---- Site B: the other household — a full live service, its listener
        // serving invites, commands and replication on one socket.
        await _siteTwo.CreateRepositoryAsync();
        await using var runtimeTwo = await StartAsync(_siteTwo);
        using var keypairTwo = PeerKeypairStore.Open(_siteTwo.StateDirectory);
        var grantsTwo = PeerGrantStore.Open(_siteTwo.StateDirectory);
        await using var listener = RemoteServiceListener.Start(
            keypairTwo, grantsTwo, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            replicationStateDirectory: _siteTwo.StateDirectory);
        var handlerTwo = new ServiceCommandHandler(
            runtimeTwo, RemoteBindingState.On(listener.Endpoint.ToString()));
        listener.Bind(handlerTwo);

        // ---- The invite: site B's operator approves by issuing; site A's by
        // entering. Nothing else coordinates the two sites.
        Assert.IsInstanceOfType<PairingInviteResult>(
            await handlerTwo.ExecuteAsync(
                new CreatePairingInviteCommand("site-a laptop", "stores-here", QuotaBytes: null, TimeToLiveMinutes: 60),
                _timeout.Token),
            out var invite);

        await using var runtimeOne = await StartAsync(_siteOne);
        var handlerOne = new ServiceCommandHandler(runtimeOne, RemoteBindingState.Off);

        Assert.IsInstanceOfType<PairingCompletedResult>(
            await handlerOne.ExecuteAsync(
                new PairWithInviteCommand(invite.Code, "127.0.0.1", listener.Endpoint.Port, "site-b"),
                _timeout.Token),
            out var paired);
        Assert.AreEqual(keypairTwo.Identity.Fingerprint, paired.Fingerprint);

        // ---- Configuration, entirely over the contract (ADR-0037): the peer
        // destination from the pairing result, then the set that uses it.
        Assert.IsInstanceOfType<AcknowledgedResult>(await handlerOne.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                Id: null, Name: "site-b", Kind: "peer", Path: null,
                Fingerprint: paired.Fingerprint, Endpoint: $"127.0.0.1:{listener.Endpoint.Port}")),
            _timeout.Token));

        Assert.IsInstanceOfType<AcknowledgedResult>(await handlerOne.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _siteOne.DocsSetId, "docs", _siteOne.SourceRoot, Schedule: null, [], [], ["site-b"])),
            _timeout.Token));

        // ---- Backup №1. No sync command follows, deliberately: a completed
        // backup fans out to its destinations on its own, and the alternative
        // site claim includes that nobody has to remember to push.
        await RunBackupAndWaitAsync(runtimeOne, handlerOne);
        await WaitForAsync(() =>
            runtimeOne.DestinationSync.Find(_siteOne.DocsSetId, "site-b") is
            {
                State: DestinationSyncState.InSync,
                // The verification stamps land in a write after the in-sync
                // one, so the wait watches for the proven state, not merely
                // the synced one.
                VerifiedAt: not null,
            });

        // ---- Possession is proven, not assumed: the sync's keyed range
        // challenge (peer-protocol 04) stamped the ledger, and the status a
        // person reads derives from exactly that.
        var ledger = runtimeOne.DestinationSync.Find(_siteOne.DocsSetId, "site-b")!;
        Assert.IsNotNull(ledger.VerifiedAt, "the wire challenge must have run with the sync");
        Assert.AreEqual(ledger.SyncedSequence, ledger.VerifiedSequence, "verified for what was last sent");
        Assert.IsGreaterThanOrEqualTo(1, ledger.VerifiedObjects);

        Assert.IsInstanceOfType<StatusResult>(
            await handlerOne.ExecuteAsync(new GetStatusCommand(), _timeout.Token), out var status);
        var row = Assert.ContainsSingle(Assert.ContainsSingle(status.Sets).Destinations);
        Assert.AreEqual("site-b", row.Name);
        Assert.AreEqual("in-sync", row.State);
        Assert.AreEqual("proven", row.Verification);

        // ---- The replica on site B is the archive, byte for byte.
        var replicaPath = Directory.GetDirectories(Path.Combine(_siteTwo.StateDirectory, "replicas")).Single();
        await AssertReplicaMatchesAsync(replicaPath);

        // ---- The incremental leg: the delta of a changed file and a new one
        // catches up the same unattended way.
        var editedNotes = "the notes, edited after the first backup";
        _siteOne.WriteSourceFile("notes.txt", editedNotes);
        _siteOne.WriteSourceFile("added-later.txt", "a file the first snapshot never saw");
        var syncedBefore = ledger.SyncedSequence;

        await RunBackupAndWaitAsync(runtimeOne, handlerOne);
        await WaitForAsync(() =>
            runtimeOne.DestinationSync.Find(_siteOne.DocsSetId, "site-b") is
            { State: DestinationSyncState.InSync, VerifiedAt: not null } refreshed
            && refreshed.SyncedSequence > syncedBefore
            && refreshed.VerifiedSequence == refreshed.SyncedSequence);
        await AssertReplicaMatchesAsync(replicaPath);

        // ---- The drill that justifies the feature: site A is gone. Its
        // runtime stops and its archive is deleted; what remains is the kit
        // and the other site.
        await runtimeOne.DisposeAsync();
        Directory.Delete(_siteOne.ArchivesRoot, recursive: true);

        var listing = await HostHarness.RunAsync(
            RecoveryHost.RunAsync,
            "snapshots", "--repo", replicaPath, "--kit", kit, "--passphrase-env", _siteOne.PassphraseVariable);
        Assert.AreEqual(0, listing.ExitCode, listing.Error);
        var snapshots = listing.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
            .ToList();
        Assert.HasCount(2, snapshots, "both points in time crossed to the alternative site");

        // Newest first or oldest first — decide from the content itself:
        // restore both and let notes.txt say which snapshot each one is.
        var restoredA = await RestoreFromReplicaAsync(replicaPath, kit, snapshots[0], "recovered-a");
        var restoredB = await RestoreFromReplicaAsync(replicaPath, kit, snapshots[1], "recovered-b");
        var (latest, earliest) = File.Exists(FindFile(restoredA, "added-later.txt"))
            ? (restoredA, restoredB)
            : (restoredB, restoredA);

        // The current state of the source, byte-identical from site B alone.
        await AssertFileRestoredAsync(latest, "notes.txt", System.Text.Encoding.UTF8.GetBytes(editedNotes));
        await AssertFileRestoredAsync(
            latest, "data.bin",
            System.Text.Encoding.UTF8.GetBytes(new string('é', 4_096) + "binary-ish payload"));
        await AssertFileRestoredAsync(latest, "beach.jpg", System.Text.Encoding.UTF8.GetBytes(new string('p', 64_000)));
        await AssertFileRestoredAsync(latest, "added-later.txt",
            System.Text.Encoding.UTF8.GetBytes("a file the first snapshot never saw"));

        // And point-in-time: the first snapshot still says what the file said
        // then, and does not hold what did not exist yet.
        await AssertFileRestoredAsync(earliest, "notes.txt", System.Text.Encoding.UTF8.GetBytes(originalNotes));
        Assert.IsNull(FindFile(earliest, "added-later.txt"),
            "the earlier point in time must not contain the later file");

        // ---- Bookkeeping honesty on site B: the invite that started all of
        // this is spent, attributed to site A.
        using var keypairOne = PeerKeypairStore.Open(_siteOne.StateDirectory);
        Assert.IsInstanceOfType<PairingInvitesResult>(
            await handlerTwo.ExecuteAsync(new ListPairingInvitesCommand(), _timeout.Token), out var invites);
        Assert.AreEqual(keypairOne.Identity.Fingerprint, Assert.ContainsSingle(invites.Invites).ConsumedBy);
    }

    [TestMethod]
    public async Task AlternateSite_OfflineWhenTheBackupRan_CatchesUpWhenItReturns()
    {
        await _siteOne.CreateRepositoryAsync();
        _siteOne.WriteSourceFile("notes.txt", "before the outage");

        await _siteTwo.CreateRepositoryAsync();
        await using var runtimeTwo = await StartAsync(_siteTwo);
        using var keypairTwo = PeerKeypairStore.Open(_siteTwo.StateDirectory);
        var grantsTwo = PeerGrantStore.Open(_siteTwo.StateDirectory);

        var listener = RemoteServiceListener.Start(
            keypairTwo, grantsTwo, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            replicationStateDirectory: _siteTwo.StateDirectory);
        var handlerTwo = new ServiceCommandHandler(
            runtimeTwo, RemoteBindingState.On(listener.Endpoint.ToString()));
        listener.Bind(handlerTwo);

        Assert.IsInstanceOfType<PairingInviteResult>(
            await handlerTwo.ExecuteAsync(
                new CreatePairingInviteCommand("site-a laptop", "stores-here", null, null), _timeout.Token),
            out var invite);

        await using var runtimeOne = await StartAsync(_siteOne);
        var handlerOne = new ServiceCommandHandler(runtimeOne, RemoteBindingState.Off);

        Assert.IsInstanceOfType<PairingCompletedResult>(
            await handlerOne.ExecuteAsync(
                new PairWithInviteCommand(invite.Code, "127.0.0.1", listener.Endpoint.Port, "site-b"),
                _timeout.Token),
            out var paired);

        Assert.IsInstanceOfType<AcknowledgedResult>(await handlerOne.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                null, "site-b", "peer", null, paired.Fingerprint, $"127.0.0.1:{listener.Endpoint.Port}")),
            _timeout.Token));
        Assert.IsInstanceOfType<AcknowledgedResult>(await handlerOne.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _siteOne.DocsSetId, "docs", _siteOne.SourceRoot, null, [], [], ["site-b"])),
            _timeout.Token));

        await RunBackupAndWaitAsync(runtimeOne, handlerOne);
        await WaitForAsync(() =>
            runtimeOne.DestinationSync.Find(_siteOne.DocsSetId, "site-b")?.State == DestinationSyncState.InSync);

        // ---- Site B goes away. The next backup completes locally — an
        // unreachable destination never blocks a capture — and the ledger
        // stops claiming in-sync, with the failure named.
        var boundPort = listener.Endpoint.Port;
        await listener.DisposeAsync();

        _siteOne.WriteSourceFile("during-outage.txt", "written while site B was down");
        await RunBackupAndWaitAsync(runtimeOne, handlerOne);
        await WaitForAsync(() =>
            runtimeOne.DestinationSync.Find(_siteOne.DocsSetId, "site-b") is { } row
            && row.State != DestinationSyncState.InSync);
        var offline = runtimeOne.DestinationSync.Find(_siteOne.DocsSetId, "site-b")!;
        Assert.IsNotNull(offline.LastError, "an outage is a named failure, not a silent gap");

        // ---- Site B returns on the same address; an on-demand sync converges
        // the backlog — nothing is re-commanded, nothing re-backed-up.
        var revived = RemoteServiceListener.Start(
            keypairTwo, grantsTwo, new IPEndPoint(IPAddress.Loopback, boundPort), "fallbackplan-agent/test",
            replicationStateDirectory: _siteTwo.StateDirectory);
        await using (revived)
        {
            revived.Bind(handlerTwo);

            Assert.IsInstanceOfType<SyncResult>(
                await handlerOne.ExecuteAsync(new SyncCommand("docs", "site-b"), _timeout.Token), out var synced);
            Assert.IsTrue(
                synced.Lines.Any(line => line.Contains("in sync", StringComparison.Ordinal)),
                string.Join(" | ", synced.Lines));

            Assert.AreEqual(
                DestinationSyncState.InSync,
                runtimeOne.DestinationSync.Find(_siteOne.DocsSetId, "site-b")!.State);

            // The catch-up carried the outage-era delta: the replica equals
            // the archive again, including the file written while B was down.
            var replicaPath = Directory.GetDirectories(Path.Combine(_siteTwo.StateDirectory, "replicas")).Single();
            await AssertReplicaMatchesAsync(replicaPath);
        }
    }

    /// <summary>Commands a backup of "docs" and waits for the job to complete.</summary>
    private async Task RunBackupAndWaitAsync(ServiceRuntime runtime, ServiceCommandHandler handler)
    {
        var seen = new List<JobState>();

        // Subscribed here, on this thread, before the backup is commanded —
        // the ServiceTests idiom, for the same reason it holds there.
        var progress = runtime.Progress.WatchAsync(_timeout.Token);
        var watching = Task.Run(
            async () =>
            {
                await foreach (var observation in progress)
                {
                    lock (seen)
                    {
                        seen.Add(observation.Progress.State);
                    }

                    if (observation.Progress.State == JobState.Complete)
                    {
                        return;
                    }
                }
            },
            _timeout.Token);

        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand("docs", Full: false), _timeout.Token));

        await watching;
        lock (seen)
        {
            Assert.Contains(JobState.Complete, seen);
        }
    }

    /// <summary>The replica holds exactly the archive's objects, byte for byte.</summary>
    private async Task AssertReplicaMatchesAsync(string replicaPath)
    {
        var source = await ReadAllAsync(new LocalFileSystemObjectStore(_siteOne.RepositoryPath));
        var replica = await ReadAllAsync(new LocalFileSystemObjectStore(replicaPath));

        // Staging-only planes (tombstones, leases) never cross; nothing else
        // may be missing or differ.
        var expected = source.Where(pair =>
            !pair.Key.StartsWith("tombstones/", StringComparison.Ordinal)
            && !pair.Key.StartsWith("leases/", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(expected.Count, replica.Count,
            $"replica holds {replica.Count} object(s); the archive holds {expected.Count}");
        foreach (var (key, bytes) in expected)
        {
            Assert.IsTrue(replica.TryGetValue(key, out var copied), $"replica lacks {key}");
            Assert.IsTrue(bytes.AsSpan().SequenceEqual(copied), $"replica differs at {key}");
        }
    }

    private async Task<Dictionary<string, byte[]>> ReadAllAsync(LocalFileSystemObjectStore store)
    {
        Dictionary<string, byte[]> objects = [];
        await foreach (var entry in store.ListAsync(ObjectPrefix.All, ListOptions.Default, _timeout.Token))
        {
            using var read = await store.OpenReadAsync(entry.Key, range: null, _timeout.Token);
            using var buffer = new MemoryStream();
            await read.Content!.CopyToAsync(buffer, _timeout.Token);
            objects[entry.Key.Value] = buffer.ToArray();
        }

        return objects;
    }

    private async Task<string> RestoreFromReplicaAsync(string replicaPath, string kit, string snapshot, string name)
    {
        var output = Path.Combine(_siteOne.WorkPath, name);
        var restore = await HostHarness.RunAsync(
            RecoveryHost.RunAsync,
            "restore", "--repo", replicaPath, "--kit", kit, "--passphrase-env", _siteOne.PassphraseVariable,
            "--snapshot", snapshot, "--output", output);
        Assert.AreEqual(0, restore.ExitCode, restore.Error);
        return output;
    }

    /// <summary>
    /// Locates a restored file by name — where the executor places a run under
    /// the output root is the containment feature's own business — and holds
    /// it byte-identical to what was captured.
    /// </summary>
    private static async Task AssertFileRestoredAsync(string restoredRoot, string fileName, byte[] expected)
    {
        var path = FindFile(restoredRoot, fileName);
        Assert.IsNotNull(path, $"{fileName} did not restore under {restoredRoot}");
        var actual = await File.ReadAllBytesAsync(path);
        Assert.IsTrue(expected.AsSpan().SequenceEqual(actual), $"{fileName} did not restore byte-identical");
    }

    private static string? FindFile(string root, string fileName)
    {
        var found = Directory.GetFiles(root, fileName, SearchOption.AllDirectories);
        return found.Length == 1 ? found[0] : null;
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        while (!condition())
        {
            _timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, _timeout.Token);
        }
    }

    private async Task<ServiceRuntime> StartAsync(HostHarness harness)
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = harness.ArchivesRoot,
                StateDirectory = harness.StateDirectory,
            },
            passphrase,
            _timeout.Token);
    }
}
