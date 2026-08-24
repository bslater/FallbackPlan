using System.Runtime.Versioning;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Api.Tests;

/// <summary>
/// The 1.7 additions (ADR-0037) ride the same wire as everything else: each
/// new command crosses the local binding typed, discriminators and optional
/// fields intact, and each new result comes back as itself.
/// </summary>
[TestClass]
public sealed class ConfigurationContractTests : IDisposable
{
    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "fbp-api", Guid.NewGuid().ToString("n")[..12]);

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public ConfigurationContractTests() => Directory.CreateDirectory(_state);

    public void Dispose()
    {
        _timeout.Dispose();
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public void ContractVersion_TheReplicaClaimVerb_IsRecordedAtOneSeventeen()
    {
        // Deliberately exact: bumping Current without landing here is how a
        // minor stops meaning anything (the convention since 1.2).
        Assert.AreEqual("1.17", ContractVersion.Current.ToString());
    }

    [TestMethod]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("windows")]
    public async Task ConfigurationCommands_SentOverTheEndpoint_ArriveTypedWithTheirOptionalFields()
    {
        var service = new FakeService
        {
            Respond = command => command switch
            {
                UpsertBackupSetCommand => new AcknowledgedResult(),
                DeleteDestinationCommand => new ConfigurationChangeResult(["nothing was deleted"]),
                BrowseFoldersCommand => new FolderListingResult(
                    "/srv", "/", [new FolderDescriptor("data", "/srv/data", false, false)]),
                CreatePairingInviteCommand => new PairingInviteResult(
                    "code", "invite-1", 5UL, "127.0.0.1:9", null),
                ListNoticesCommand => new NoticesResult(
                    [new NoticeDescriptor("ab12cd34", "set-changed:x", "the message", 9UL, 11UL)]),
                AcknowledgeNoticeCommand => new AcknowledgedResult(),
                UnpairCommand => new ConfigurationChangeResult(["revoked"]),
                ClaimReplicasCommand => new ClaimedReplicasResult(
                    [
                        new ClaimedFromPeer(
                            "fp99",
                            [new ClaimedReplicaDescriptor(new string('b', 32), [new string('c', 32)])]),
                        new ClaimedFromPeer("fp77", [], "the peer did not answer"),
                    ]),
                ListDirectoryCommand => new DirectoryResult(
                    "docs",
                    [new DirectoryEntryDescriptor("a.txt", "file", 12, ModifiedAt: 5UL, Change: "changed")],
                    Deleted: ["gone.txt"],
                    PreviousSnapshotId: "ff00"),
                OpenRestoreSourceCommand => new RestoreSourceOpenedResult(
                    "ab12cd34ef56ab78", "docs", "vault",
                    [new SnapshotDescriptor(new string('e', 32), new string('a', 32), 42UL, 1, 3)],
                    ["one blob would not open"]),
                CloseRestoreSourceCommand => new AcknowledgedResult(),
                RunRestoreCommand => new RestoreResult(
                    3, 1, "/out", "failed",
                    Skipped: 2, Degraded: 1, Displaced: 0, WrittenBeside: 1,
                    ReceiptPath: "/state/receipts/r1.json",
                    FailedSample: ["docs/a.txt — the store does not hold this blob"]),
                PreviewSetChangesCommand => new SetChangePreviewResult(
                    "docs", "ab12", 7UL, 40,
                    New: new ChangeBucketDescriptor(2, ["a.txt", "b.txt"]),
                    Updated: new ChangeBucketDescriptor(1, ["c.txt"]),
                    MetadataOnly: new ChangeBucketDescriptor(0, []),
                    Moved: new ChangeBucketDescriptor(0, []),
                    Deleted: new ChangeBucketDescriptor(3, ["d.txt"]),
                    NoLongerIncluded: new ChangeBucketDescriptor(5, ["e.txt"]),
                    Failures: 1,
                    SampleLimit: 20),
                _ => new AcknowledgedResult(),
            },
        };

        await using var listener = LocalServiceListener.Start(service, _state);
        await using var client = await LocalServiceClient.ConnectAsync(_state, "test", _timeout.Token);

        Assert.IsInstanceOfType<AcknowledgedResult>(await client.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('a', 32), "docs", "/data", "every 4h", ["photos/**"], ["*.iso"], ["vault"],
                new RetentionPolicyDescriptor(KeepDaily: 7, MinGenerations: 2),
                new Dictionary<string, RetentionPolicyDescriptor> { ["vault"] = new(KeepWeekly: 4) },
                Roots: [new BackupRootDescriptor("/data", "data"), new BackupRootDescriptor("/pics", "Photos")])),
            _timeout.Token));

        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await client.ExecuteAsync(new DeleteDestinationCommand("vault"), _timeout.Token), out var change);
        Assert.ContainsSingle(change.Lines);

        Assert.IsInstanceOfType<FolderListingResult>(
            await client.ExecuteAsync(new BrowseFoldersCommand("/srv"), _timeout.Token), out var listing);
        Assert.AreEqual("/srv/data", Assert.ContainsSingle(listing.Folders).Path);

        Assert.IsInstanceOfType<PairingInviteResult>(
            await client.ExecuteAsync(
                new CreatePairingInviteCommand("laptop", "stores-here", 1024, 60), _timeout.Token),
            out var invite);
        Assert.AreEqual("invite-1", invite.InviteId);

        Assert.IsInstanceOfType<SetChangePreviewResult>(
            await client.ExecuteAsync(
                new PreviewSetChangesCommand("docs", ExcludeRules: ["photos"], SampleLimit: 5), _timeout.Token),
            out var preview);
        Assert.AreEqual(2, preview.New.Count);
        Assert.AreEqual("d.txt", Assert.ContainsSingle(preview.Deleted.Sample));
        Assert.AreEqual(5, preview.NoLongerIncluded.Count);
        Assert.AreEqual(1, preview.Failures);

        Assert.IsInstanceOfType<NoticesResult>(
            await client.ExecuteAsync(new ListNoticesCommand(IncludeAcknowledged: true), _timeout.Token),
            out var notices);
        var listed = Assert.ContainsSingle(notices.Notices);
        Assert.AreEqual("set-changed:x", listed.Key);
        Assert.AreEqual(11UL, listed.AcknowledgedAt);

        Assert.IsInstanceOfType<AcknowledgedResult>(
            await client.ExecuteAsync(new AcknowledgeNoticeCommand("ab12cd34"), _timeout.Token));

        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await client.ExecuteAsync(
                new UnpairCommand("fp99", Notify: false, Endpoint: "10.0.0.9:7777"), _timeout.Token));

        // A claim's answer has to survive the boundary with both of its
        // shapes intact: a peer that yielded something, and a peer that could
        // not be asked at all. Collapsing those two into one is precisely
        // what this result exists to prevent, so both are round-tripped.
        Assert.IsInstanceOfType<ClaimedReplicasResult>(
            await client.ExecuteAsync(
                new ClaimReplicasCommand(new string('f', 64), Fingerprint: "fp"), _timeout.Token),
            out var claimed);
        Assert.HasCount(2, claimed.Peers);
        Assert.AreEqual(
            new string('c', 32),
            Assert.ContainsSingle(Assert.ContainsSingle(claimed.Peers[0].Claimed).SetIds));
        Assert.AreEqual("the peer did not answer", claimed.Peers[1].Unreachable);
        Assert.IsEmpty(claimed.Peers[1].Claimed);

        Assert.IsInstanceOfType<DirectoryResult>(
            await client.ExecuteAsync(new ListDirectoryCommand("ab", "docs"), _timeout.Token), out var directory);
        var entry = Assert.ContainsSingle(directory.Entries);
        Assert.AreEqual(5UL, entry.ModifiedAt);
        Assert.AreEqual("changed", entry.Change);
        Assert.AreEqual("gone.txt", Assert.ContainsSingle(directory.Deleted!));
        Assert.AreEqual("ff00", directory.PreviousSnapshotId);

        Assert.IsInstanceOfType<SetChangePreviewResult>(
            await client.ExecuteAsync(
                new PreviewSetChangesCommand(
                    null, Roots: [new BackupRootDescriptor("/a", "A"), new BackupRootDescriptor("/b", "B")]),
                _timeout.Token));

        // The guided-restore verbs (1.11, ADR-0041): the source handle and
        // its snapshots come back typed, and the run's options — several
        // paths, the target and existing policies, in-place — arrive intact.
        Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
            await client.ExecuteAsync(new OpenRestoreSourceCommand("docs", "vault"), _timeout.Token),
            out var opened);
        Assert.AreEqual("vault", opened.Location);
        Assert.AreEqual(42UL, Assert.ContainsSingle(opened.Snapshots).CapturedAt);
        Assert.ContainsSingle(opened.Warnings);

        Assert.IsInstanceOfType<RestoreResult>(
            await client.ExecuteAsync(
                new RunRestoreCommand(
                    new string('e', 32), null, "/ignored",
                    Source: opened.SourceId,
                    Paths: ["docs/a.txt", "media"],
                    Target: "original",
                    Existing: "rename",
                    InPlace: true),
                _timeout.Token),
            out var ran);
        Assert.AreEqual(1L, ran.WrittenBeside);
        Assert.AreEqual("/state/receipts/r1.json", ran.ReceiptPath);
        Assert.AreEqual("failed", ran.Outcome);
        Assert.ContainsSingle(ran.FailedSample!);

        Assert.IsInstanceOfType<AcknowledgedResult>(
            await client.ExecuteAsync(new CloseRestoreSourceCommand(opened.SourceId), _timeout.Token));

        // What the service received is what was sent — optional fields intact.
        Assert.IsInstanceOfType<UpsertBackupSetCommand>(service.Received[0], out var upsert);
        Assert.AreEqual(7, upsert.Set.Retention?.KeepDaily);
        Assert.AreEqual(4, upsert.Set.DestinationRetention?["vault"].KeepWeekly);
        Assert.AreEqual("Photos", upsert.Set.Roots?[1].Label);
        Assert.IsInstanceOfType<PreviewSetChangesCommand>(
            service.Received.Last(command => command is PreviewSetChangesCommand), out var previewSent);
        Assert.AreEqual("B", Assert.ContainsSingle(
            previewSent.Roots!.Where(root => root.Path == "/b")).Label);
        Assert.IsInstanceOfType<CreatePairingInviteCommand>(service.Received[3], out var create);
        Assert.AreEqual(60, create.TimeToLiveMinutes);

        Assert.IsInstanceOfType<RunRestoreCommand>(
            Assert.ContainsSingle(service.Received.OfType<RunRestoreCommand>()), out var restoreSent);
        Assert.AreEqual("original", restoreSent.Target);
        Assert.AreEqual("rename", restoreSent.Existing);
        Assert.IsTrue(restoreSent.InPlace);
        Assert.AreEqual("media", restoreSent.Paths?[1]);
    }
}
