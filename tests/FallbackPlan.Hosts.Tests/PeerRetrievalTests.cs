using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Restore from a peer's replica over the wire (peer-protocol 07,
/// ADR-0041, FR-SNP-008's sibling drill): two live services, pairing by
/// invite, a backup fanning out and proving possession — then site A's
/// staging archive is DESTROYED and the data comes back through the command
/// contract alone: the owner inventory names the replica, the repository
/// opens over the retrieval session, and the restore fetches only what the
/// plan needs. No shared disk, no recovery kit — the running service and the
/// pairing are enough.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PeerRetrievalTests : IDisposable
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
    public async Task PeerRetrieval_AfterTotalStagingLoss_RestoresOverTheWire()
    {
        // ---- Site A: the data. Site B: a live service with its listener.
        // The photo is 1.5 MiB of incompressible bytes: past the 1 MiB
        // production segment size AND past one retrieval chunk, so the
        // restore both walks a multi-segment manifest over the wire and
        // pages `retrieve_read` more than once for a single record — the
        // chunk loop no smaller payload ever exercised.
        await _siteOne.CreateRepositoryAsync();
        var notes = "the notes that must come back over the wire";
        var photo = new byte[1_536 * 1024];
        new Random(43).NextBytes(photo);
        _siteOne.WriteSourceFile("notes.txt", notes);
        Directory.CreateDirectory(Path.Combine(_siteOne.SourceRoot, "photos"));
        File.WriteAllBytes(Path.Combine(_siteOne.SourceRoot, "photos", "beach.jpg"), photo);

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

        Assert.IsInstanceOfType<PairingInviteResult>(
            await handlerTwo.ExecuteAsync(
                new CreatePairingInviteCommand("site-a", "stores-here", QuotaBytes: null, TimeToLiveMinutes: 60),
                _timeout.Token),
            out var invite);

        string snapshotId;
        await using (var runtimeOne = await StartAsync(_siteOne))
        {
            var handlerOne = new ServiceCommandHandler(runtimeOne, RemoteBindingState.Off);
            Assert.IsInstanceOfType<PairingCompletedResult>(
                await handlerOne.ExecuteAsync(
                    new PairWithInviteCommand(invite.Code, "127.0.0.1", listener.Endpoint.Port, "site-b"),
                    _timeout.Token),
                out var paired);

            Assert.IsInstanceOfType<AcknowledgedResult>(await handlerOne.ExecuteAsync(
                new UpsertDestinationCommand(new DestinationDescriptor(
                    Id: null, Name: "site-b", Kind: "peer", Path: null,
                    Fingerprint: paired.Fingerprint, Endpoint: $"127.0.0.1:{listener.Endpoint.Port}")),
                _timeout.Token));
            Assert.IsInstanceOfType<ConfigurationChangeResult>(await handlerOne.ExecuteAsync(
                new UpsertBackupSetCommand(new BackupSetDescriptor(
                    _siteOne.DocsSetId, "docs", _siteOne.SourceRoot, Schedule: null, [], [], ["site-b"])),
                _timeout.Token));

            // The save queued the first capture itself (ADR-0047).
            await WaitForAsync(() => runtimeOne.Jobs.Jobs.Any(job =>
                job.BackupSetId == _siteOne.DocsSetId && job.State == JobState.Complete));
            await WaitForAsync(() =>
                runtimeOne.DestinationSync.Find(_siteOne.DocsSetId, "site-b") is
                { State: DestinationSyncState.InSync, VerifiedAt: not null });

            Assert.IsInstanceOfType<SnapshotsResult>(
                await handlerOne.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var listed);
            snapshotId = Assert.ContainsSingle(listed.Snapshots).SnapshotId;
        }

        // ---- The drill: site A's archives are gone. What remains is the
        // running pairing — and the contract.
        Directory.Delete(_siteOne.ArchivesRoot, recursive: true);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        await using var recovered = await StartAsync(_siteOne);
        var handler = new ServiceCommandHandler(recovered, RemoteBindingState.Off);

        var opened = await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs", "site-b"), _timeout.Token);
        if (opened is ServiceError refusal)
        {
            Assert.Fail($"peer source refused: {refusal.Reason}: {refusal.Message}");
        }

        Assert.IsInstanceOfType<RestoreSourceOpenedResult>(opened, out var source);
        Assert.AreEqual("site-b", source.Location);
        Assert.AreEqual(snapshotId, Assert.ContainsSingle(source.Snapshots).SnapshotId);

        // The listing browses the peer-held snapshot through the same verb
        // the wizard's file picker uses.
        Assert.IsInstanceOfType<DirectoryResult>(
            await handler.ExecuteAsync(
                new ListDirectoryCommand(snapshotId, null, Source: source.SourceId), _timeout.Token),
            out var top);
        CollectionAssert.AreEquivalent(
            new[] { "notes.txt", "photos" },
            top.Entries.Select(entry => entry.Name).ToArray());

        var output = Path.Combine(_siteOne.WorkPath, "over-the-wire");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(snapshotId, null, output, Source: source.SourceId, InPlace: true),
                _timeout.Token),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome);
        Assert.AreEqual(notes, File.ReadAllText(Path.Combine(output, "notes.txt")));
        Assert.IsTrue(
            photo.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(output, "photos", "beach.jpg"))),
            "the segmented photo did not round-trip over the wire");

        Assert.IsInstanceOfType<AcknowledgedResult>(
            await handler.ExecuteAsync(new CloseRestoreSourceCommand(source.SourceId), _timeout.Token));
    }

    /// <summary>Commands a backup and waits for completion — the AlternateSiteTests idiom.</summary>
    private async Task RunBackupAndWaitAsync(ServiceRuntime runtime, ServiceCommandHandler handler)
    {
        var progress = runtime.Progress.WatchAsync(_timeout.Token);
        var watching = Task.Run(
            async () =>
            {
                await foreach (var observation in progress)
                {
                    if (observation.Progress.State is JobState.Complete or JobState.CompletedWithFailures)
                    {
                        return;
                    }
                }
            },
            _timeout.Token);

        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand("docs", Full: false), _timeout.Token));
        await watching;
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        while (!condition())
        {
            _timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, _timeout.Token);
        }
    }

    private async Task<ServiceRuntime> StartAsync(HostHarness site)
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(site.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = site.ArchivesRoot,
                StateDirectory = site.StateDirectory,
            },
            passphrase,
            _timeout.Token);
    }
}
