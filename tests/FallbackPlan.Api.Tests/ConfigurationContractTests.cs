using System.Runtime.Versioning;
using System.Text.Json;
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
    public void ContractVersion_ADiagnosticBundleCommand_IsRecordedAtOneFiftyTwo()
    {
        // Deliberately exact: bumping Current without landing here is how a
        // minor stops meaning anything (the convention since 1.2).
        Assert.AreEqual("1.52", ContractVersion.Current.ToString());
    }

    [TestMethod]
    public void ExportDiagnosticsCommand_CrossesUnderItsWireNames_AndAsksForNoPathsUnlessTold()
    {
        // NFR-PRIV-003: paths are a per-bundle opt-in, so the field's absence
        // must read as "no" on a service that never saw it named (ADR-0081).
        var asked = JsonSerializer.Serialize<ServiceCommand>(
            new ExportDiagnosticsCommand(IncludePaths: true), FrameCodec.SerializerOptions);

        Assert.Contains("\"command\":\"export_diagnostics\"", asked, StringComparison.Ordinal);
        Assert.Contains("\"include_paths\":true", asked, StringComparison.Ordinal);
        Assert.IsInstanceOfType<ExportDiagnosticsCommand>(
            JsonSerializer.Deserialize<ServiceCommand>(
                """{"command":"export_diagnostics"}""", FrameCodec.SerializerOptions),
            out var bare);
        Assert.IsFalse(bare.IncludePaths);
    }

    [TestMethod]
    public void DiagnosticBundleResult_CrossesUnderItsWireNames_WithItsBytesIntact()
    {
        byte[] content = [0x50, 0x4b, 0x03, 0x04, 0x00, 0xff];
        var result = JsonSerializer.Serialize<ServiceResult>(
            new DiagnosticBundleResult(
                "fallbackplan-diagnostics-20261004-120000Z.zip", content, IncludesPaths: false,
                ["README.txt", "log.txt"], LogRecords: 12, LogRecordsLeftOut: 3),
            FrameCodec.SerializerOptions);

        Assert.Contains("\"result\":\"diagnostic_bundle\"", result, StringComparison.Ordinal);
        Assert.Contains("\"file_name\":\"fallbackplan-diagnostics-20261004-120000Z.zip\"", result, StringComparison.Ordinal);
        Assert.Contains($"\"content\":\"{Convert.ToBase64String(content)}\"", result, StringComparison.Ordinal);
        Assert.Contains("\"includes_paths\":false", result, StringComparison.Ordinal);
        Assert.Contains("\"log_records_left_out\":3", result, StringComparison.Ordinal);

        Assert.IsInstanceOfType<DiagnosticBundleResult>(
            JsonSerializer.Deserialize<ServiceResult>(result, FrameCodec.SerializerOptions), out var read);
        CollectionAssert.AreEqual(content, read.Content);
        CollectionAssert.AreEqual(new[] { "README.txt", "log.txt" }, read.Entries.ToArray());
        Assert.AreEqual(12, read.LogRecords);
    }

    [TestMethod]
    public void DiagnosticBundleResult_TheLargestBundle_FitsAFrameOnceEncoded()
    {
        // The service trims the log until the bundle is under this; the
        // ceiling is chosen so that its base64 and the envelope around it
        // still clear the frame cap with room to spare.
        Assert.IsLessThan(FrameCodec.MaximumFrameBytes, (DiagnosticBundleResult.MaximumContentBytes / 3 * 4) + 64 * 1024);
    }

    [TestMethod]
    public void DeleteSnapshotsCommand_CrossesUnderItsWireNames_AndNeverCarriesWhoAsked()
    {
        // FR-GC-013: who asked is the connection's to say, from the session it
        // presented, and never the client's. The field exists only in process.
        var setId = new string('b', 32);
        var snapshot = new string('5', 64);
        var command = JsonSerializer.Serialize<ServiceCommand>(
            new DeleteSnapshotsCommand(setId, [snapshot], Apply: true, ReclaimGrant: "c0ffee") { Actor = "mallory" },
            FrameCodec.SerializerOptions);

        Assert.Contains("\"command\":\"delete_snapshots\"", command, StringComparison.Ordinal);
        Assert.Contains($"\"set_id\":\"{setId}\"", command, StringComparison.Ordinal);
        Assert.Contains($"\"snapshot_ids\":[\"{snapshot}\"]", command, StringComparison.Ordinal);
        Assert.Contains("\"reclaim_grant\":\"c0ffee\"", command, StringComparison.Ordinal);
        Assert.DoesNotContain("mallory", command, StringComparison.Ordinal);

        Assert.IsInstanceOfType<DeleteSnapshotsCommand>(
            JsonSerializer.Deserialize<ServiceCommand>(
                command.Replace("\"apply\":true", "\"apply\":true,\"actor\":\"mallory\"", StringComparison.Ordinal),
                FrameCodec.SerializerOptions),
            out var read);
        Assert.IsNull(read.Actor);
        Assert.IsTrue(read.Apply);
        Assert.AreEqual(snapshot, Assert.ContainsSingle(read.SnapshotIds));
    }

    [TestMethod]
    public void DeleteSnapshotsResult_EachSnapshotsOutcome_CrossesUnderItsWireNames()
    {
        var setId = new string('b', 32);
        var snapshot = new string('5', 64);
        var result = JsonSerializer.Serialize<ServiceResult>(
            new DeleteSnapshotsResult(
                setId, Applied: true, [new SnapshotDeletionOutcome(snapshot, "pending", ["usb"])], ["a line"]),
            FrameCodec.SerializerOptions);

        Assert.Contains("\"state\":\"pending\"", result, StringComparison.Ordinal);
        Assert.Contains("\"awaiting\":[\"usb\"]", result, StringComparison.Ordinal);
        Assert.IsInstanceOfType<DeleteSnapshotsResult>(
            JsonSerializer.Deserialize<ServiceResult>(result, FrameCodec.SerializerOptions), out var read);
        Assert.IsTrue(read.Applied);
        Assert.AreEqual("usb", Assert.ContainsSingle(Assert.ContainsSingle(read.Snapshots).Awaiting));
    }

    [TestMethod]
    public void SnapshotDescriptor_ADeletionStillPending_CrossesUnderItsWireName()
    {
        // FR-GC-013: a requested snapshot stays listed until every copy has
        // let it go, and says which copies it is waiting on.
        var listed = JsonSerializer.Serialize<ServiceResult>(
            new SnapshotsResult([new SnapshotDescriptor(new string('5', 64), new string('b', 32), 1, 1, 3)
            {
                DeletionPending = ["usb"],
            }]),
            FrameCodec.SerializerOptions);

        Assert.Contains("\"deletion_pending\":[\"usb\"]", listed, StringComparison.Ordinal);
        Assert.IsInstanceOfType<SnapshotsResult>(
            JsonSerializer.Deserialize<ServiceResult>(listed, FrameCodec.SerializerOptions), out var read);
        Assert.AreEqual("usb", Assert.ContainsSingle(Assert.ContainsSingle(read.Snapshots).DeletionPending!));
    }

    [TestMethod]
    public void RetentionCommand_ItsGrantsPerSet_CrossUnderTheirWireName()
    {
        // FR-GC-008: a set adopted from a destination keeps the salt it was
        // born under, so one grant cannot authorise every set; the map is
        // keyed by set id.
        var setId = new string('b', 32);
        var command = JsonSerializer.Serialize<ServiceCommand>(
            new RetentionCommand(true, ReclaimGrants: new Dictionary<string, string> { [setId] = "c0ffee" }),
            FrameCodec.SerializerOptions);
        Assert.Contains($"\"reclaim_grants\":{{\"{setId}\":\"c0ffee\"}}", command, StringComparison.Ordinal);

        Assert.IsInstanceOfType<RetentionCommand>(
            JsonSerializer.Deserialize<ServiceCommand>(command, FrameCodec.SerializerOptions), out var read);
        Assert.AreEqual("c0ffee", read.ReclaimGrants![setId]);

        // A pre-1.50 client sends none: one grant, or none, for every set.
        Assert.IsInstanceOfType<RetentionCommand>(
            JsonSerializer.Deserialize<ServiceCommand>(
                "{\"command\":\"retention\",\"apply\":true,\"reclaim_grant\":\"c0ffee\"}",
                FrameCodec.SerializerOptions),
            out var older);
        Assert.IsNull(older.ReclaimGrants);
        Assert.AreEqual("c0ffee", older.ReclaimGrant);
    }

    [TestMethod]
    public void ValidateSetDraftCommand_TheSetItDrafts_CrossesUnderItsWireName()
    {
        // FR-DEST-017's draft answer: the set a draft edits or would create,
        // so placement is judged as the save will judge it.
        var setId = new string('b', 32);
        var command = JsonSerializer.Serialize<ServiceCommand>(
            new ValidateSetDraftCommand(null, [], [], ["/data"], ["vault"], setId),
            FrameCodec.SerializerOptions);
        Assert.Contains($"\"set_id\":\"{setId}\"", command, StringComparison.Ordinal);

        Assert.IsInstanceOfType<ValidateSetDraftCommand>(
            JsonSerializer.Deserialize<ServiceCommand>(command, FrameCodec.SerializerOptions), out var read);
        Assert.AreEqual(setId, read.SetId);

        // A pre-1.49 client sends none: a draft that names no set.
        Assert.IsInstanceOfType<ValidateSetDraftCommand>(
            JsonSerializer.Deserialize<ServiceCommand>(
                "{\"command\":\"validate_set_draft\",\"schedule\":null,\"include_rules\":[],\"exclude_rules\":[]}",
                FrameCodec.SerializerOptions),
            out var older);
        Assert.IsNull(older.SetId);
    }

    [TestMethod]
    public void RestoreResult_TheFilesReadAroundDamage_CrossUnderTheirWireNames()
    {
        // FR-RST-007's answer: how many files were read from another copy
        // because a copy they were first read from was damaged or would not
        // read, and a sample of which, from where. The field names are the
        // contract; a rename here is a protocol break that compiles.
        var answer = JsonSerializer.Serialize<ServiceResult>(
            new RestoreResult(
                2, 0, "/out", "complete",
                ReadAround: 1,
                ReadAroundSample: ["docs/a.txt — read from destination 'spare', around destination 'vault'"]),
            FrameCodec.SerializerOptions);
        Assert.Contains("\"read_around\":1", answer, StringComparison.Ordinal);
        Assert.Contains("\"read_around_sample\":[", answer, StringComparison.Ordinal);

        Assert.IsInstanceOfType<RestoreResult>(
            JsonSerializer.Deserialize<ServiceResult>(answer, FrameCodec.SerializerOptions), out var parsed);
        Assert.AreEqual(1L, parsed.ReadAround);
        Assert.Contains("destination 'spare'", Assert.ContainsSingle(parsed.ReadAroundSample!), StringComparison.Ordinal);

        // A pre-1.45 service sends neither, which reads as nothing read around.
        Assert.IsInstanceOfType<RestoreResult>(
            JsonSerializer.Deserialize<ServiceResult>(
                "{\"result\":\"restore\",\"restored\":2,\"failed\":0,\"output_directory\":\"/out\",\"outcome\":\"complete\"}",
                FrameCodec.SerializerOptions),
            out var older);
        Assert.AreEqual(0L, older.ReadAround);
        Assert.IsNull(older.ReadAroundSample);
    }

    [TestMethod]
    public void ServiceSettings_RoundTripUnderTheirWireNames()
    {
        // Two verbs and their answer. The discriminators and the field names
        // are the contract; a rename here is a protocol break that compiles.
        var get = JsonSerializer.Serialize<ServiceCommand>(
            new GetServiceSettingsCommand(), FrameCodec.SerializerOptions);
        Assert.Contains("\"command\":\"get_service_settings\"", get, StringComparison.Ordinal);
        Assert.IsInstanceOfType<GetServiceSettingsCommand>(
            JsonSerializer.Deserialize<ServiceCommand>(get, FrameCodec.SerializerOptions));

        var update = JsonSerializer.Serialize<ServiceCommand>(
            new UpdateServiceSettingsCommand("22:00-06:00", "40 MiB/s", 3), FrameCodec.SerializerOptions);
        Assert.Contains("\"command\":\"update_service_settings\"", update, StringComparison.Ordinal);
        Assert.Contains("\"background_window\":\"22:00-06:00\"", update, StringComparison.Ordinal);
        Assert.Contains("\"background_read_limit\":\"40 MiB/s\"", update, StringComparison.Ordinal);
        Assert.Contains("\"max_concurrent_backups\":3", update, StringComparison.Ordinal);
        Assert.IsInstanceOfType<UpdateServiceSettingsCommand>(
            JsonSerializer.Deserialize<ServiceCommand>(update, FrameCodec.SerializerOptions), out var parsed);
        Assert.AreEqual(new UpdateServiceSettingsCommand("22:00-06:00", "40 MiB/s", 3), parsed);

        var answer = JsonSerializer.Serialize<ServiceResult>(
            new ServiceSettingsResult("22:00-06:00", null, 3, EffectiveMaxConcurrentBackups: 2),
            FrameCodec.SerializerOptions);
        Assert.Contains("\"result\":\"service_settings\"", answer, StringComparison.Ordinal);
        Assert.Contains("\"background_window\":\"22:00-06:00\"", answer, StringComparison.Ordinal);
        Assert.Contains("\"background_read_limit\":null", answer, StringComparison.Ordinal);
        Assert.Contains("\"max_concurrent_backups\":3", answer, StringComparison.Ordinal);
        Assert.Contains("\"effective_max_concurrent_backups\":2", answer, StringComparison.Ordinal);
    }

    [TestMethod]
    public void UpgradeSetFormat_RoundTripsUnderItsWireName()
    {
        // The discriminator is the contract; a rename here is a protocol
        // break that compiles.
        var json = JsonSerializer.Serialize<ServiceCommand>(
            new UpgradeSetFormatCommand("docs"), FrameCodec.SerializerOptions);

        Assert.Contains("\"upgrade_set_format\"", json, StringComparison.Ordinal);
        Assert.IsInstanceOfType<UpgradeSetFormatCommand>(
            JsonSerializer.Deserialize<ServiceCommand>(json, FrameCodec.SerializerOptions), out var parsed);
        Assert.AreEqual("docs", parsed.SetName);
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
                ListDirectoryCommand => new DirectoryResult(
                    "docs",
                    [new DirectoryEntryDescriptor("a.txt", "file", 12, ModifiedAt: 5UL, Change: "changed")],
                    Deleted: ["gone.txt"],
                    PreviousSnapshotId: "ff00"),
                OpenRestoreSourceCommand => new RestoreSourceOpenedResult(
                    "ab12cd34ef56ab78", "docs", "vault",
                    [
                        new SnapshotDescriptor(
                            new string('e', 32), new string('a', 32), 42UL, 1, 3,
                            ConsistencyMethod: 2),
                        new SnapshotDescriptor(new string('d', 32), new string('a', 32), 41UL, 1, 3),
                    ],
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
        Assert.HasCount(2, opened.Snapshots);
        Assert.AreEqual(42UL, opened.Snapshots[0].CapturedAt);
        Assert.ContainsSingle(opened.Warnings);

        // Both shapes of the consistency method cross the boundary: a service
        // that knows how the capture was taken, and one too old to say. Null
        // is not 1 — "captured live" and "would not say" are different answers
        // to someone deciding whether to trust a restored database
        // (specification 06 §6), and a codec that defaulted the absent one to
        // live would be inventing a promise.
        Assert.AreEqual((byte)2, opened.Snapshots[0].ConsistencyMethod);
        Assert.IsNull(opened.Snapshots[1].ConsistencyMethod);

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
