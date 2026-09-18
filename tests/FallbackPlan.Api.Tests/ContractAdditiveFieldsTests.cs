using System.Text.Json;
using FallbackPlan.Api.Transport;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Api.Tests;

/// <summary>
/// The additive fields of contracts 1.17, 1.19 and 1.20 (ADR-0047,
/// ADR-0048), proven on the bytes: the priorities on the two configuration
/// descriptors, the full-backup facts on the status matrix, and the counted
/// plan plus the session-carrying watch on the progress surface. Additive
/// means two promises at once — a new service's fields survive the trip to
/// a new client, and an OLD service's frames, which never mention them,
/// read as the stated defaults rather than failing to parse. Establishes
/// the wire half of FR-SVC-013, FR-DEST-014's status surface, and
/// FR-SVC-006's plan.
/// </summary>
/// <remarks>
/// The wire names are asserted literally. They are derived from C# property
/// names by the snake-case policy, so a rename compiles cleanly, upsets no
/// analyzer, and silently breaks every client that spelled the old name —
/// the same trap <c>DiagnosticsRelayTests</c> documents for the console's
/// camelCase relay.
/// </remarks>
[TestClass]
public sealed class ContractAdditiveFieldsTests : IDisposable
{
    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "fbp-contract-tests", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public ContractAdditiveFieldsTests() => Directory.CreateDirectory(_state);

    public void Dispose()
    {
        _timeout.Dispose();
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static readonly StatusResult FullStatus = new(
        MachineName: "hub",
        Sets:
        [
            new BackupSetStatusDescriptor(
                "docs", new BackupSetStatus(ProtectionState.Degraded, Verification: null, Warnings: []), NextRun: null,
                [
                    new DestinationStatusDescriptor(
                        "vault-b", "local-path", "behind", LastSuccessAt: null, Detail: "awaiting full backup",
                        "same-machine", "unproven", BaselineCompletedAt: null, NeedsFull: true),
                    new DestinationStatusDescriptor(
                        "vault-a", "local-path", "in-sync", LastSuccessAt: 9_000, Detail: null,
                        "same-machine", "proven", BaselineCompletedAt: 5_000, NeedsFull: false),
                ]),
        ],
        ObservedAt: 10_000,
        Notices: []);

    [TestMethod]
    public async Task TheStatusMatrix_CarriesTheFullBackupFacts_AcrossALocalConnection()
    {
        var service = new FakeService { Respond = _ => FullStatus };
        await using var listener = LocalServiceListener.Start(service, _state);
        await using var client = await LocalServiceClient.ConnectAsync(_state, "test", _timeout.Token);

        var result = await client.ExecuteAsync(new GetStatusCommand(), _timeout.Token);

        Assert.IsInstanceOfType<StatusResult>(result, out var status);
        var rows = Assert.ContainsSingle(status.Sets).Destinations;
        Assert.HasCount(2, rows);
        Assert.IsTrue(rows[0].NeedsFull);
        Assert.IsNull(rows[0].BaselineCompletedAt);
        Assert.IsFalse(rows[1].NeedsFull);
        Assert.AreEqual(5_000UL, rows[1].BaselineCompletedAt);
    }

    [TestMethod]
    public async Task ThePriorities_CarryAcrossALocalConnection()
    {
        var service = new FakeService
        {
            Respond = command => command switch
            {
                ListBackupSetsCommand => new BackupSetsResult(
                [
                    new BackupSetDescriptor(
                        new string('a', 32), "docs", "/src", "every 4h", [], [], ["vault"], Priority: 7),
                ]),
                _ => new DestinationsResult(
                [
                    new DestinationDescriptor(
                        new string('d', 32), "vault", "local-path", "/mnt/vault", null, null, Priority: -2),
                ]),
            },
        };
        await using var listener = LocalServiceListener.Start(service, _state);
        await using var client = await LocalServiceClient.ConnectAsync(_state, "test", _timeout.Token);

        var sets = await client.ExecuteAsync(new ListBackupSetsCommand(), _timeout.Token);
        Assert.IsInstanceOfType<BackupSetsResult>(sets, out var setsResult);
        Assert.AreEqual(7, Assert.ContainsSingle(setsResult.Sets).Priority);

        var destinations = await client.ExecuteAsync(new ListDestinationsCommand(), _timeout.Token);
        Assert.IsInstanceOfType<DestinationsResult>(destinations, out var destinationsResult);
        Assert.AreEqual(-2, Assert.ContainsSingle(destinationsResult.Destinations).Priority);
    }

    [TestMethod]
    public void TheWireNames_AreThePublishedOnes()
    {
        // The names a pre-existing client spelled, pinned on the serialized
        // bytes so a C# rename cannot drift them.
        var json = JsonSerializer.Serialize<ServiceResult>(FullStatus, FrameCodec.SerializerOptions);

        Assert.Contains("\"baseline_completed_at\":5000", json, StringComparison.Ordinal);
        Assert.Contains("\"needs_full\":true", json, StringComparison.Ordinal);

        var sets = JsonSerializer.Serialize<ServiceResult>(
            new BackupSetsResult(
                [new BackupSetDescriptor(new string('a', 32), "docs", "/src", null, [], [], [], Priority: 7)]),
            FrameCodec.SerializerOptions);
        Assert.Contains("\"priority\":7", sets, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheReplicaAttributionWireNames_AreThePublishedOnes()
    {
        // Contract 1.31 (ADR-0053 §3): the operator's view of the replicas
        // stored here, pinned on the bytes. `claimable` is the whole
        // decision — whether the passphrase can move a replica or only the
        // operator can — and the key itself never crosses.
        var listed = JsonSerializer.Serialize<ServiceResult>(
            new ReplicaAttributionsResult(
                [new ReplicaAttributionDescriptor(new string('c', 32), "ABCDEFGHIJKLMNOPQRSTUVWXYZ", "laptop", true)]),
            FrameCodec.SerializerOptions);
        Assert.Contains("\"result\":\"replica_attributions\"", listed, StringComparison.Ordinal);
        Assert.Contains("\"repository_id\":\"cccc", listed, StringComparison.Ordinal);
        Assert.Contains("\"owner_fingerprint\":\"ABCDEFGHIJ", listed, StringComparison.Ordinal);
        Assert.Contains("\"owner_label\":\"laptop\"", listed, StringComparison.Ordinal);
        Assert.Contains("\"claimable\":true", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("claim_public_key", listed, StringComparison.Ordinal);

        var command = JsonSerializer.Serialize<ServiceCommand>(
            new ReattributeReplicaCommand(new string('c', 32), "ABCDEF"), FrameCodec.SerializerOptions);
        Assert.Contains("\"command\":\"reattribute_replica\"", command, StringComparison.Ordinal);
        Assert.Contains("\"fingerprint\":\"ABCDEF\"", command, StringComparison.Ordinal);
        Assert.Contains(
            "\"command\":\"list_replica_attributions\"",
            JsonSerializer.Serialize<ServiceCommand>(new ListReplicaAttributionsCommand(), FrameCodec.SerializerOptions),
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheReceiptsWireNames_AreThePublishedOnes()
    {
        // Contract 1.33 (ADR-0063, ADR-0064): the receipts filed here, both
        // kinds, as facts only — the status is the service's verdict on the
        // bytes on disk now, and neither a path nor a signed byte crosses.
        var listed = JsonSerializer.Serialize<ServiceResult>(
            new ReceiptsResult(
            [
                new ReceiptDescriptor(
                    "replication", "commander", 5, "verified", true, null, "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
                    "docs", "friend", new string('c', 32), 4, "0123456789abcdef",
                    DeletedCount: null, NotHeld: null, CommittedCount: 2, HeldObjects: 7, HeldBytes: 1234),
                new ReceiptDescriptor(
                    "deletion", "destination", 3, "signature-invalid", false, "edited", "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
                    null, null, new string('c', 32), 2, "0123456789abcdef",
                    DeletedCount: 1, NotHeld: 2, CommittedCount: null, HeldObjects: null, HeldBytes: null),
            ]),
            FrameCodec.SerializerOptions);
        Assert.Contains("\"result\":\"receipts_listed\"", listed, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"replication\"", listed, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"commander\"", listed, StringComparison.Ordinal);
        Assert.Contains("\"filed_at\":5", listed, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"signature-invalid\"", listed, StringComparison.Ordinal);
        Assert.Contains("\"verified\":false", listed, StringComparison.Ordinal);
        Assert.Contains("\"problem\":\"edited\"", listed, StringComparison.Ordinal);
        Assert.Contains("\"signer_fingerprint\":\"ABCDEFGHIJ", listed, StringComparison.Ordinal);
        Assert.Contains("\"destination\":\"friend\"", listed, StringComparison.Ordinal);
        Assert.Contains("\"repository_id\":\"cccc", listed, StringComparison.Ordinal);
        Assert.Contains("\"issued_at\":4", listed, StringComparison.Ordinal);
        Assert.Contains("\"session_prefix\":\"0123456789abcdef\"", listed, StringComparison.Ordinal);
        Assert.Contains("\"committed_count\":2", listed, StringComparison.Ordinal);
        Assert.Contains("\"held_objects\":7", listed, StringComparison.Ordinal);
        Assert.Contains("\"held_bytes\":1234", listed, StringComparison.Ordinal);
        Assert.Contains("\"deleted_count\":1", listed, StringComparison.Ordinal);
        Assert.Contains("\"not_held\":2", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("path", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("signed", listed, StringComparison.Ordinal);

        var command = JsonSerializer.Serialize<ServiceCommand>(
            new ListReceiptsCommand("deletion", "docs", new string('c', 32), 50), FrameCodec.SerializerOptions);
        Assert.Contains("\"command\":\"list_receipts\"", command, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"deletion\"", command, StringComparison.Ordinal);
        Assert.Contains("\"set\":\"docs\"", command, StringComparison.Ordinal);
        Assert.Contains("\"repository\":\"cccc", command, StringComparison.Ordinal);
        Assert.Contains("\"limit\":50", command, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheAdoptionWireNames_AreThePublishedOnes()
    {
        // Contract 1.30 (ADR-0061): the discovery row and the adoption
        // answer, pinned on the bytes so a C# rename cannot drift them.
        var discovered = JsonSerializer.Serialize<ServiceResult>(
            new ArchivesDiscoveredResult(
                "vault",
                [
                    new DiscoveredArchiveDescriptor(
                        new string('c', 32), 2, 1234, "fallbackplan-agent/0.1", new string('0', 32), 65536, 3, 4,
                        new string('1', 64), 1, 7, OwnedBySet: null, SameInstallation: false),
                ],
                []),
            FrameCodec.SerializerOptions);
        Assert.Contains("\"repository_id\":\"cccc", discovered, StringComparison.Ordinal);
        Assert.Contains("\"kdf_salt\":", discovered, StringComparison.Ordinal);
        Assert.Contains("\"kdf_memory_kib\":65536", discovered, StringComparison.Ordinal);
        Assert.Contains("\"sealing_public_key\":", discovered, StringComparison.Ordinal);
        Assert.Contains("\"snapshot_objects\":1", discovered, StringComparison.Ordinal);
        Assert.Contains("\"highest_publication_sequence\":7", discovered, StringComparison.Ordinal);
        Assert.Contains("\"owned_by_set\":null", discovered, StringComparison.Ordinal);
        Assert.Contains("\"same_installation\":false", discovered, StringComparison.Ordinal);

        var adopted = JsonSerializer.Serialize<ServiceResult>(
            new ArchiveAdoptedResult(
                new string('a', 32), "docs", new string('c', 32), [new BackupRootDescriptor("/src")], [],
                "every 1h", [], ["**/*.tmp"], 1, new string('5', 32), 9000,
                WriterIdentityResumed: true, AlreadyAdopted: false, Lines: []),
            FrameCodec.SerializerOptions);
        Assert.Contains("\"set_id\":\"aaaa", adopted, StringComparison.Ordinal);
        Assert.Contains("\"missing_roots\":[]", adopted, StringComparison.Ordinal);
        Assert.Contains("\"writer_identity_resumed\":true", adopted, StringComparison.Ordinal);
        Assert.Contains("\"already_adopted\":false", adopted, StringComparison.Ordinal);
        Assert.Contains("\"newest_snapshot_at\":9000", adopted, StringComparison.Ordinal);

        var sets = JsonSerializer.Serialize<ServiceResult>(
            new BackupSetsResult(
                [
                    new BackupSetDescriptor(
                        new string('a', 32), "docs", "/src", null, [], [], [],
                        KdfSalt: new string('0', 32), KdfMemoryKib: 65536, KdfIterations: 3, KdfParallelism: 4,
                        SealingPublicKey: new string('1', 64)),
                ]),
            FrameCodec.SerializerOptions);
        Assert.Contains("\"kdf_salt\":\"0000", sets, StringComparison.Ordinal);
        Assert.Contains("\"kdf_parallelism\":4", sets, StringComparison.Ordinal);
        Assert.Contains("\"sealing_public_key\":\"1111", sets, StringComparison.Ordinal);
    }

    [TestMethod]
    public void AFrameFromBeforeTheFields_ReadsAsTheStatedDefaults()
    {
        // A 1.16-era status row: no priority, no baseline, no needs_full.
        // Additive means this parses — to null and false, the values that
        // made the fields safe to add (ContractVersion 1.17/1.19 notes).
        var row = JsonSerializer.Deserialize<DestinationStatusDescriptor>(
            """
            { "name": "vault", "kind": "local-path", "state": "in-sync",
              "last_success_at": 9000, "detail": null,
              "failure_domain": "same-machine", "verification": "proven" }
            """,
            FrameCodec.SerializerOptions)!;
        Assert.IsNull(row.BaselineCompletedAt);
        Assert.IsFalse(row.NeedsFull);

        var set = JsonSerializer.Deserialize<BackupSetDescriptor>(
            """
            { "id": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "name": "docs", "root": "/src",
              "schedule": null, "include_rules": [], "exclude_rules": [], "destinations": [] }
            """,
            FrameCodec.SerializerOptions)!;
        Assert.IsNull(set.Priority);

        var destination = JsonSerializer.Deserialize<DestinationDescriptor>(
            """
            { "id": null, "name": "vault", "kind": "local-path", "path": "/mnt/vault",
              "fingerprint": null, "endpoint": null }
            """,
            FrameCodec.SerializerOptions)!;
        Assert.IsNull(destination.Priority);
    }

    [TestMethod]
    public void TheCountedPlan_WireNamesAndPre120Defaults()
    {
        // Contract 1.20's progress fields, on the bytes. The old-frame JSON
        // is the modern one with the additions stripped, so the fixture
        // cannot drift from the real serialization.
        var modern = JsonSerializer.Serialize(
            new FallbackPlan.Domain.Jobs.JobProgress(
                "job-1", FallbackPlan.Domain.Jobs.JobState.Packing, 10, 4, 1, 0, 4096, 2048,
                TotalFiles: 500, TotalBytes: 1_000_000),
            FrameCodec.SerializerOptions);

        Assert.Contains("\"total_files\":500", modern, StringComparison.Ordinal);
        Assert.Contains("\"total_bytes\":1000000", modern, StringComparison.Ordinal);

        var old = modern
            .Replace(",\"total_files\":500", "", StringComparison.Ordinal)
            .Replace(",\"total_bytes\":1000000", "", StringComparison.Ordinal);
        Assert.AreNotEqual(modern, old, "the strip must have removed the fields, or the old frame proves nothing");

        var parsed = JsonSerializer.Deserialize<FallbackPlan.Domain.Jobs.JobProgress>(
            old, FrameCodec.SerializerOptions)!;
        Assert.IsNull(parsed.TotalFiles);
        Assert.IsNull(parsed.TotalBytes);
        Assert.AreEqual(10, parsed.FilesSeen);
    }

    [TestMethod]
    public void TheJobRowsRunStats_WireNamesAndPre122Defaults()
    {
        // Contract 1.22: the run's terminal numbers ride the job row. The
        // old frame is the modern one with the additions stripped, so the
        // fixture cannot drift from the real serialization.
        var modern = JsonSerializer.Serialize(
            new JobDescriptor(
                "job-1", new string('a', 32), FallbackPlan.Domain.Jobs.JobState.Complete, 1_000, 2_000,
                SnapshotId: new string('e', 64), Detail: "120 file(s), 100 unchanged",
                FilesSeen: 120, FilesDone: 118, FilesReused: 100, FilesFailed: 2,
                BytesSeen: 4_096_000, BytesStored: 512_000,
                TotalFiles: 120, TotalBytes: 4_096_000),
            FrameCodec.SerializerOptions);

        Assert.Contains("\"files_done\":118", modern, StringComparison.Ordinal);
        Assert.Contains("\"files_reused\":100", modern, StringComparison.Ordinal);
        Assert.Contains("\"files_failed\":2", modern, StringComparison.Ordinal);
        Assert.Contains("\"bytes_stored\":512000", modern, StringComparison.Ordinal);
        Assert.Contains("\"total_files\":120", modern, StringComparison.Ordinal);

        var old = modern
            .Replace(",\"files_seen\":120", "", StringComparison.Ordinal)
            .Replace(",\"files_done\":118", "", StringComparison.Ordinal)
            .Replace(",\"files_reused\":100", "", StringComparison.Ordinal)
            .Replace(",\"files_failed\":2", "", StringComparison.Ordinal)
            .Replace(",\"bytes_seen\":4096000", "", StringComparison.Ordinal)
            .Replace(",\"bytes_stored\":512000", "", StringComparison.Ordinal)
            .Replace(",\"total_files\":120", "", StringComparison.Ordinal)
            .Replace(",\"total_bytes\":4096000", "", StringComparison.Ordinal);
        Assert.AreNotEqual(modern, old, "the strip must have removed the fields, or the old frame proves nothing");

        var parsed = JsonSerializer.Deserialize<JobDescriptor>(old, FrameCodec.SerializerOptions)!;
        Assert.IsNull(parsed.FilesDone);
        Assert.IsNull(parsed.BytesStored);
        Assert.IsNull(parsed.TotalFiles);
        Assert.AreEqual("job-1", parsed.Id);
    }

    [TestMethod]
    public void ListJobsLimit_WireNameAndPre122Default()
    {
        // Contract 1.22: `list_jobs` takes an optional bound, because the
        // journal grows for the life of the installation and FrameCodec caps
        // a frame at 8 MiB. Null keeps the old ask-for-everything meaning, so
        // an old client's frame — which never mentions the field — is
        // unchanged in behaviour.
        var modern = JsonSerializer.Serialize<ServiceCommand>(
            new ListJobsCommand(ActiveOnly: false, Limit: 200), FrameCodec.SerializerOptions);
        Assert.Contains("\"limit\":200", modern, StringComparison.Ordinal);

        var parsed = JsonSerializer.Deserialize<ServiceCommand>(
            """{ "command": "list_jobs", "active_only": false }""", FrameCodec.SerializerOptions);
        Assert.IsInstanceOfType<ListJobsCommand>(parsed, out var command);
        Assert.IsNull(command.Limit);
    }

    [TestMethod]
    public async Task TheDrillDownVerbs_RoundTripAcrossALocalConnection()
    {
        // Contract 1.22's two read verbs: the run diff and the failure
        // listing, typed end to end.
        var service = new FakeService
        {
            Respond = command => command switch
            {
                JobChangesCommand changes => new JobChangesResult(
                    "docs", new string('e', 64), BaselineSnapshotId: new string('b', 64),
                    BaselineCapturedAt: 5_000, Unchanged: 90,
                    New: new ChangeBucketDescriptor(3, ["fresh.txt"]),
                    Changed: new ChangeBucketDescriptor(2, ["edited.txt"]),
                    Removed: new ChangeBucketDescriptor(1, ["gone.txt"]),
                    SampleLimit: changes.SampleLimit ?? 20),
                _ => new JobFailuresResult(
                    "docs", new string('e', 64), Failures: 2,
                    [new CaptureFailureDescriptor("home/locked.db", "permission", "Access denied.")],
                    SampleLimit: 100),
            },
        };
        await using var listener = LocalServiceListener.Start(service, _state);
        await using var client = await LocalServiceClient.ConnectAsync(_state, "test", _timeout.Token);

        var diff = await client.ExecuteAsync(new JobChangesCommand("job-1", SampleLimit: 5), _timeout.Token);
        Assert.IsInstanceOfType<JobChangesResult>(diff, out var changes);
        Assert.AreEqual(90L, changes.Unchanged);
        Assert.AreEqual(3L, changes.New.Count);
        Assert.AreEqual(5, changes.SampleLimit);

        var failures = await client.ExecuteAsync(new JobFailuresCommand("job-1"), _timeout.Token);
        Assert.IsInstanceOfType<JobFailuresResult>(failures, out var listing);
        Assert.AreEqual(2L, listing.Failures);
        Assert.AreEqual("permission", Assert.ContainsSingle(listing.Sample).Reason);
    }

    [TestMethod]
    public void TheDrillDownVerbs_WireNamesAreThePublishedOnes()
    {
        var command = JsonSerializer.Serialize<ServiceCommand>(
            new JobChangesCommand("job-1", SampleLimit: 5), FrameCodec.SerializerOptions);
        Assert.Contains("\"command\":\"job_changes\"", command, StringComparison.Ordinal);
        Assert.Contains("\"sample_limit\":5", command, StringComparison.Ordinal);

        var result = JsonSerializer.Serialize<ServiceResult>(
            new JobChangesResult(
                "docs", new string('e', 64), null, null, 0,
                new ChangeBucketDescriptor(0, []), new ChangeBucketDescriptor(0, []),
                new ChangeBucketDescriptor(0, []), SampleLimit: 20),
            FrameCodec.SerializerOptions);
        Assert.Contains("\"result\":\"job_changes\"", result, StringComparison.Ordinal);
        Assert.Contains("\"baseline_snapshot_id\":null", result, StringComparison.Ordinal);

        var failures = JsonSerializer.Serialize<ServiceResult>(
            new JobFailuresResult("docs", new string('e', 64), 0, [], SampleLimit: 100),
            FrameCodec.SerializerOptions);
        Assert.Contains("\"result\":\"job_failures\"", failures, StringComparison.Ordinal);
        Assert.Contains("\"sample_limit\":100", failures, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheDirectShipFlag_WireNameAndPre123Default()
    {
        // Contract 1.23: the set descriptor carries the storage shape. Null
        // preserves — the semantics every field this surface adds shares —
        // so a pre-1.23 client's upsert cannot silently convert a set.
        var modern = JsonSerializer.Serialize<ServiceCommand>(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('a', 32), "docs", "/src", null, [], [], ["vault"], DirectShip: true)),
            FrameCodec.SerializerOptions);
        Assert.Contains("\"direct_ship\":true", modern, StringComparison.Ordinal);

        var old = modern.Replace(",\"direct_ship\":true", "", StringComparison.Ordinal);
        Assert.AreNotEqual(modern, old, "the strip must have removed the field, or the old frame proves nothing");

        var parsed = JsonSerializer.Deserialize<ServiceCommand>(old, FrameCodec.SerializerOptions);
        Assert.IsInstanceOfType<UpsertBackupSetCommand>(parsed, out var command);
        Assert.IsNull(command.Set.DirectShip);
    }

    [TestMethod]
    public void TheBehindReason_WireNamesAndPre122Defaults()
    {
        // Contract 1.22: the status matrix carries the machine cause beside
        // the prose, and the set row carries the demotion's operand — the
        // last completed backup the destination is compared against.
        var json = JsonSerializer.Serialize<ServiceResult>(
            new StatusResult(
                "hub",
                [
                    new BackupSetStatusDescriptor(
                        "docs", new BackupSetStatus(ProtectionState.Degraded, null, []), NextRun: null,
                        [
                            new DestinationStatusDescriptor(
                                "local", "local-path", "behind", LastSuccessAt: 1_000, Detail: "a backup completed after this destination's last sync",
                                "same-machine", "unproven", Reason: "catching-up"),
                        ],
                        LastCompletedAt: 5_000),
                ],
                ObservedAt: 10_000,
                Notices: []),
            FrameCodec.SerializerOptions);

        Assert.Contains("\"reason\":\"catching-up\"", json, StringComparison.Ordinal);
        Assert.Contains("\"last_completed_at\":5000", json, StringComparison.Ordinal);

        // A pre-1.22 frame that never mentions either parses to null.
        var row = JsonSerializer.Deserialize<DestinationStatusDescriptor>(
            """
            { "name": "vault", "kind": "local-path", "state": "behind",
              "last_success_at": 9000, "detail": null,
              "failure_domain": "same-machine", "verification": "unproven" }
            """,
            FrameCodec.SerializerOptions)!;
        Assert.IsNull(row.Reason);

        // The set row's old frame is the modern one with the addition
        // stripped, so the fixture cannot drift from the real serialization.
        var modernSet = JsonSerializer.Serialize(
            new BackupSetStatusDescriptor(
                "docs", new BackupSetStatus(ProtectionState.Degraded, null, []), NextRun: null, [],
                LastCompletedAt: 5_000),
            FrameCodec.SerializerOptions);
        var oldSet = modernSet.Replace(",\"last_completed_at\":5000", "", StringComparison.Ordinal);
        Assert.AreNotEqual(modernSet, oldSet, "the strip must have removed the field, or the old frame proves nothing");

        var set = JsonSerializer.Deserialize<BackupSetStatusDescriptor>(oldSet, FrameCodec.SerializerOptions)!;
        Assert.IsNull(set.LastCompletedAt);
    }

    [TestMethod]
    public void TheCurrentFile_WireNameAndPre122Default()
    {
        // Contract 1.22: the live feed names the file being processed. A
        // pre-1.22 frame that never mentions it reads as null — the
        // counts-only feed every earlier client sent.
        var modern = JsonSerializer.Serialize(
            new FallbackPlan.Domain.Jobs.JobProgress(
                "job-1", FallbackPlan.Domain.Jobs.JobState.Publishing, 10, 4, 1, 0, 4096, 2048,
                TotalFiles: 500, TotalBytes: 1_000_000, CurrentFile: "docs/photos/img-001.jpg"),
            FrameCodec.SerializerOptions);

        Assert.Contains("\"current_file\":\"docs/photos/img-001.jpg\"", modern, StringComparison.Ordinal);

        var old = modern.Replace(",\"current_file\":\"docs/photos/img-001.jpg\"", "", StringComparison.Ordinal);
        Assert.AreNotEqual(modern, old, "the strip must have removed the field, or the old frame proves nothing");

        var parsed = JsonSerializer.Deserialize<FallbackPlan.Domain.Jobs.JobProgress>(
            old, FrameCodec.SerializerOptions)!;
        Assert.IsNull(parsed.CurrentFile);
    }

    [TestMethod]
    public void TheSessionCarryingWatch_WireNameAndPre120Default()
    {
        // The watch frame's session (contract 1.20): named on the bytes,
        // and a pre-1.20 frame that never mentions it reads as null — the
        // anonymous watch every earlier client sent.
        var modern = JsonSerializer.Serialize<WireFrame>(new WatchFrame("abc123"), FrameCodec.SerializerOptions);
        Assert.Contains("\"session\":\"abc123\"", modern, StringComparison.Ordinal);

        var old = modern.Replace(",\"session\":\"abc123\"", "", StringComparison.Ordinal)
            .Replace("\"session\":\"abc123\",", "", StringComparison.Ordinal);
        Assert.AreNotEqual(modern, old, "the strip must have removed the field, or the old frame proves nothing");

        var frame = JsonSerializer.Deserialize<WireFrame>(old, FrameCodec.SerializerOptions);
        Assert.IsInstanceOfType<WatchFrame>(frame, out var watch);
        Assert.IsNull(watch.Session);
    }

    [TestMethod]
    public void TheDerivationParameters_WireNamesAndPre128Defaults()
    {
        // Contract 1.28: describe_service carries the installation's public
        // derivation parameters — what a client holding the passphrase
        // derives a restore grant from without holding an archive.
        var modern = JsonSerializer.Serialize(
            new ServiceDescriptionResult(
                "1.28", "test", "machine", "/state", false, 0,
                KdfSalt: "000102030405060708090a0b0c0d0e0f", KdfMemoryKib: 65536, KdfIterations: 3,
                KdfParallelism: 4, SealingPublicKey: new string('9', 64)),
            FrameCodec.SerializerOptions);

        Assert.Contains("\"kdf_salt\":\"000102030405060708090a0b0c0d0e0f\"", modern, StringComparison.Ordinal);
        Assert.Contains("\"kdf_memory_kib\":65536", modern, StringComparison.Ordinal);
        Assert.Contains("\"kdf_iterations\":3", modern, StringComparison.Ordinal);
        Assert.Contains("\"kdf_parallelism\":4", modern, StringComparison.Ordinal);
        Assert.Contains("\"sealing_public_key\":\"" + new string('9', 64) + "\"", modern, StringComparison.Ordinal);

        // A pre-1.28 frame carries none of them, which a client reads as
        // "not published" — the same as a service not yet set up.
        var old = modern
            .Replace(",\"kdf_salt\":\"000102030405060708090a0b0c0d0e0f\"", "", StringComparison.Ordinal)
            .Replace(",\"kdf_memory_kib\":65536", "", StringComparison.Ordinal)
            .Replace(",\"kdf_iterations\":3", "", StringComparison.Ordinal)
            .Replace(",\"kdf_parallelism\":4", "", StringComparison.Ordinal)
            .Replace(",\"sealing_public_key\":\"" + new string('9', 64) + "\"", "", StringComparison.Ordinal);
        Assert.AreNotEqual(modern, old, "the strip must have removed the fields, or the old frame proves nothing");

        var row = JsonSerializer.Deserialize<ServiceDescriptionResult>(old, FrameCodec.SerializerOptions)!;
        Assert.IsNull(row.KdfSalt);
        Assert.IsNull(row.KdfMemoryKib);
        Assert.IsNull(row.KdfIterations);
        Assert.IsNull(row.KdfParallelism);
        Assert.IsNull(row.SealingPublicKey);
    }

    [TestMethod]
    public void TheVerificationTiers_WireNamesAndPre132Defaults()
    {
        // Contract 1.32: which proof the last verification rested on — a
        // record's tag opened at the destination, or the whole sealed blob
        // hashed against the digest the writer signed.
        var modern = JsonSerializer.Serialize(
            new DestinationStatusDescriptor(
                "vault", "local-path", "in-sync", LastSuccessAt: 1_000, Detail: null,
                "other-drive", "proven", VerifiedSealed: 5, VerifiedDigest: 2),
            FrameCodec.SerializerOptions);

        Assert.Contains("\"verified_sealed\":5", modern, StringComparison.Ordinal);
        Assert.Contains("\"verified_digest\":2", modern, StringComparison.Ordinal);

        // A pre-1.32 frame carries neither and reads as zero of each: the
        // coverage the row always had, with no claim about which tier.
        var old = modern
            .Replace(",\"verified_sealed\":5", "", StringComparison.Ordinal)
            .Replace(",\"verified_digest\":2", "", StringComparison.Ordinal);
        Assert.AreNotEqual(modern, old, "the strip must have removed the fields, or the old frame proves nothing");

        var row = JsonSerializer.Deserialize<DestinationStatusDescriptor>(old, FrameCodec.SerializerOptions)!;
        Assert.AreEqual(0, row.VerifiedSealed);
        Assert.AreEqual(0, row.VerifiedDigest);
        Assert.AreEqual("proven", row.Verification);
    }

    [TestMethod]
    public void TheDrillLimit_WireNameAndPre127Default()
    {
        // Contract 1.27 (ADR-0054 Amendment 2): a drill on a write-only set
        // passes with a stated limit — the road back proved as far as the
        // sealed content — and the limit rides beside a null failure.
        var modern = JsonSerializer.Serialize(
            new DestinationStatusDescriptor(
                "vault", "local-path", "in-sync", LastSuccessAt: 1_000, Detail: null,
                "other-drive", "verified", DrilledAt: 7_000, DrillFiles: 3, DrillLimit: "content sealed"),
            FrameCodec.SerializerOptions);

        Assert.Contains("\"drill_limit\":\"content sealed\"", modern, StringComparison.Ordinal);
        Assert.Contains("\"drill_failure\":null", modern, StringComparison.Ordinal);

        // A pre-1.27 frame carries no limit and reads as a plain pass — which
        // overstates by exactly the limit it cannot see, and is the honest
        // reading of a field that did not exist.
        var old = modern.Replace(",\"drill_limit\":\"content sealed\"", "", StringComparison.Ordinal);
        Assert.AreNotEqual(modern, old, "the strip must have removed the field, or the old frame proves nothing");

        var row = JsonSerializer.Deserialize<DestinationStatusDescriptor>(old, FrameCodec.SerializerOptions)!;
        Assert.IsNull(row.DrillLimit);
        Assert.IsNull(row.DrillFailure);
        Assert.AreEqual(7_000UL, row.DrilledAt);
    }

    [TestMethod]
    public void TheRestoreDrillAnswer_WireNamesAndPre125Defaults()
    {
        // Contract 1.25 (ADR-0054): the status matrix carries when a drill
        // last restored a file out of this destination's own replica, how
        // many it brought back, and why it could not when it could not.
        var modern = JsonSerializer.Serialize(
            new DestinationStatusDescriptor(
                "vault", "local-path", "in-sync", LastSuccessAt: 1_000, Detail: null,
                "other-drive", "verified", DrilledAt: 7_000, DrillFiles: 3),
            FrameCodec.SerializerOptions);

        Assert.Contains("\"drilled_at\":7000", modern, StringComparison.Ordinal);
        Assert.Contains("\"drill_files\":3", modern, StringComparison.Ordinal);

        // A pre-1.25 frame reads as never drilled — which a client must show
        // as never drilled, not as a drill that passed. The three states are
        // distinguishable on the bytes: no stamp, a stamp alone, and a stamp
        // with a failure beside it.
        var old = modern
            .Replace(",\"drilled_at\":7000", "", StringComparison.Ordinal)
            .Replace(",\"drill_files\":3", "", StringComparison.Ordinal);
        Assert.AreNotEqual(modern, old, "the strip must have removed the fields, or the old frame proves nothing");

        var row = JsonSerializer.Deserialize<DestinationStatusDescriptor>(old, FrameCodec.SerializerOptions)!;
        Assert.IsNull(row.DrilledAt);
        Assert.AreEqual(0, row.DrillFiles);
        Assert.IsNull(row.DrillFailure);

        // A failed drill is a stamp AND a reason, never a bare absence: the
        // client that cannot tell it from "never drilled" reports an
        // unrecoverable destination as merely unexercised.
        var failed = JsonSerializer.Deserialize<DestinationStatusDescriptor>(
            JsonSerializer.Serialize(
                new DestinationStatusDescriptor(
                    "vault", "local-path", "in-sync", LastSuccessAt: 1_000, Detail: null,
                    "other-drive", "verified",
                    DrilledAt: 7_000, DrillFailure: "'docs/a.txt' would not restore"),
                FrameCodec.SerializerOptions),
            FrameCodec.SerializerOptions)!;
        Assert.AreEqual(7_000UL, failed.DrilledAt);
        Assert.AreEqual(0, failed.DrillFiles);
        Assert.IsNotNull(failed.DrillFailure);
    }
}
