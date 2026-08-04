using System.Text.Json;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Catalogue.Forensic;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The restore planner and executor (phase-1 wave R; FR-RST-004/005):
/// conflicts and degradations surface at PLAN time, execution quarantines
/// what it would replace, applies metadata after content, and the receipt
/// accounts for every planned item — plus the partial-rebuild drill: a
/// targeted forensic rebuild resolves one snapshot's graph and restore
/// works from it without the rest of the repository.
/// </summary>
public sealed class RestorePlanTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private CatalogueDb OpenCatalogue(string name = "catalogue.db") =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, name), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, KeyHierarchy hierarchy, CatalogueDb catalogue) =>
        new(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory, observer: null, catalogue);

    private static SnapshotJob Job(FakeFileSystemSource source, byte seed) => new()
    {
        Source = source,
        RootPath = "/",
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(seed, 16).ToArray(),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "restore-tests/1.0",
    };

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }

    [Fact]
    public async Task The_plan_surfaces_case_collisions_and_degradations_before_any_byte_moves()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/Readme.txt", Deterministic(100, 1));
        source.AddFile("docs/readme.TXT", Deterministic(100, 2));
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "link",
            Kind = FallbackPlan.Filesystem.ScanEntryKind.Symlink,
            LinkTarget = "docs"u8.ToArray(),
        });

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();
        await CreateOrchestrator(store, keys, hierarchy, catalogue).PublishAsync(Job(source, 0xA1), CancellationToken.None);

        var snapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray();

        // A case-insensitive, symlink-less, path-limited target.
        var plan = RestorePlanner.Plan(catalogue, snapshotId, string.Empty, new RestoreTargetProfile
        {
            CaseSensitive = false,
            SupportsPosixMetadata = false,
            SupportsSymlinks = false,
            MaxPathBytes = 4096,
        });

        Assert.Equal(2, plan.Conflicts.Count(conflict => conflict.Reason.Contains("Collides", StringComparison.Ordinal)));
        Assert.Contains(plan.Degradations, degradation => degradation.Capability == "posix-metadata");
        Assert.Contains(plan.Degradations, degradation => degradation.Capability == "symlinks");
        Assert.Equal(200ul, plan.SpaceEstimateBytes);

        // The same tree on a case-sensitive target has no collisions.
        var sensitivePlan = RestorePlanner.Plan(
            catalogue, snapshotId, string.Empty, RestoreTargetProfile.ForLocalPlatform() with { CaseSensitive = true });
        Assert.DoesNotContain(sensitivePlan.Conflicts, conflict => conflict.Reason.Contains("Collides", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execution_quarantines_applies_metadata_after_content_and_accounts_for_everything()
    {
        var content = Deterministic(50_000, 5);
        var source = new FakeFileSystemSource();
        var node = source.AddFile("data/file.bin", content);
        node.Metadata = node.Metadata with { ModifiedAt = 1_600_000_000_000, PosixMode = 0x180 /* 0600 */ };

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();
        await CreateOrchestrator(store, keys, hierarchy, catalogue).PublishAsync(Job(source, 0xB1), CancellationToken.None);

        var snapshotId = Enumerable.Repeat((byte)0xB1, 16).ToArray();
        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, snapshotId, string.Empty, target);
        Assert.Empty(plan.Conflicts);

        var output = Path.Combine(SpoolDirectory, "restore-out");

        // A pre-existing file at the destination — quarantine material.
        var destination = Path.Combine(output, "data", "file.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "precious local edits");

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output, new RestoreExecutionOptions { NowUnixMilliseconds = 1_722_700_000_000 }, CancellationToken.None);

        Assert.True(receipt.Complete);
        Assert.Equal(plan.Items.Count, receipt.Items.Count);
        Assert.Equal(content, File.ReadAllBytes(destination));

        // What was there is in quarantine, not gone (08 §3).
        Assert.Equal("data/file.bin", Assert.Single(receipt.Quarantined));
        Assert.Equal(
            "precious local edits",
            File.ReadAllText(Path.Combine(output, ".fbp-quarantine", "data", "file.bin")));

        // Metadata landed after content: mtime is the captured one.
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_600_000_000_000).UtcDateTime,
            File.GetLastWriteTimeUtc(destination));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(destination) & (UnixFileMode.UserRead | UnixFileMode.UserWrite));
        }

        // The receipt is a valid versioned JSON document.
        using var parsed = JsonDocument.Parse(receipt.ToJson());
        Assert.Equal(1, parsed.RootElement.GetProperty("schema_version").GetInt32());
        Assert.True(parsed.RootElement.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task A_targeted_forensic_rebuild_is_enough_to_restore_that_snapshot()
    {
        // Two snapshots; the drill targets only the first. The rebuilt
        // catalogue plus the repository must restore it even though the
        // scan stopped as soon as the target's graph resolved
        // (NFR-PERF-015's shape: restore before full recovery).
        var firstContent = Deterministic(80_000, 7);
        var source = new FakeFileSystemSource();
        source.AddFile("wanted/data.bin", firstContent);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using (var live = OpenCatalogue())
        {
            await CreateOrchestrator(store, keys, hierarchy, live).PublishAsync(Job(source, 0xC1), CancellationToken.None);
            source.AddFile("other/late.bin", Deterministic(90_000, 9));
            await CreateOrchestrator(store, keys, hierarchy, live).PublishAsync(Job(source, 0xC2), CancellationToken.None);
        }

        // Fresh catalogue, targeted forensic rebuild — no index objects used.
        var snapshotId = Enumerable.Repeat((byte)0xC1, 16).ToArray();
        using var rebuilt = OpenCatalogue("forensic.db");
        using (var rebuilder = new ForensicRebuilder(store, Repo, hierarchy))
        {
            var report = await rebuilder.RebuildAsync(
                rebuilt, new ForensicTarget.Snapshot(snapshotId), CancellationToken.None);
            Assert.True(report.TargetSatisfied);
        }

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(rebuilt, snapshotId, string.Empty, target);
        Assert.Contains(plan.Items, item => item.Path == "wanted/data.bin");

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var output = Path.Combine(SpoolDirectory, "partial-restore");
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output, new RestoreExecutionOptions { NowUnixMilliseconds = 1_722_700_000_000 }, CancellationToken.None);

        Assert.True(receipt.Complete);
        Assert.Equal(firstContent, File.ReadAllBytes(Path.Combine(output, "wanted", "data.bin")));
    }
}
