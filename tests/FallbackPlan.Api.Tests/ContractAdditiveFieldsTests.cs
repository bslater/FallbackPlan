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
}
