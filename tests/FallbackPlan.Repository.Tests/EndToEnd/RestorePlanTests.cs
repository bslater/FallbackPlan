using System.Text.Json;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Catalogue.Forensic;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.TestSupport;
using System.Runtime.Versioning;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The restore planner and executor (phase-1 wave R;
/// FR-RST-001, FR-RST-003, FR-RST-004, FR-RST-005, FR-RST-006): conflicts and degradations surface at PLAN time —
/// which is FR-RST-003's whole claim, that the plan exists before any byte
/// moves — execution displaces what it would replace rather than overwriting
/// in place, applies metadata after content, and the receipt accounts for
/// every planned item and says what the restore as a whole achieved — plus
/// the partial-rebuild drill: a targeted forensic rebuild resolves one
/// snapshot's graph and restore works from it without the rest of the
/// repository. Repository path text is treated as untrusted, so a manifest
/// that does not name plain components under the root is refused. The three
/// selectors that exist — snapshot, path, destination — are exercised end to
/// end; device, time and file-version selection is phase-2 work that has not
/// been built, so FR-RST-001 is covered in part and says so.
/// </summary>
/// <remarks>
/// FR-RST-006 — historical content defaulting to a quarantine path — is
/// established by one named test and by nothing else here. It once had its
/// citation from a test of displaced-file preservation, which is a different
/// control: where restored content lands, versus what happens to a file
/// already sitting where it lands. Both are tested; they are not the same
/// test and must not share a citation again.
/// </remarks>
[TestClass]
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

    [TestMethod]
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

        Assert.AreEqual(2, plan.Conflicts.Count(conflict => conflict.Reason.Contains("Collides", StringComparison.Ordinal)));
        Assert.Contains(degradation => degradation.Capability == "posix-metadata", plan.Degradations);
        Assert.Contains(degradation => degradation.Capability == "symlinks", plan.Degradations);
        Assert.AreEqual(200ul, plan.SpaceEstimateBytes);

        // The same tree on a case-sensitive target has no collisions.
        var sensitivePlan = RestorePlanner.Plan(
            catalogue, snapshotId, string.Empty, RestoreTargetProfile.ForLocalPlatform() with { CaseSensitive = true });
        Assert.DoesNotContain(conflict => conflict.Reason.Contains("Collides", StringComparison.Ordinal), sensitivePlan.Conflicts);
    }

    [TestMethod]
    public async Task Execution_displaces_applies_metadata_after_content_and_accounts_for_everything()
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
        Assert.IsEmpty(plan.Conflicts);

        var output = Path.Combine(SpoolDirectory, "restore-out");

        // A pre-existing file at the destination — displacement material.
        var destination = Path.Combine(output, "data", "file.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "precious local edits");

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                // In place, because displacing an existing file is only
                // possible where the restore lands on top of one.
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "test",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.AreEqual(plan.Items.Count, receipt.Items.Count);
        SequenceAssert.AreEqual(content, File.ReadAllBytes(destination));

        // What was there is moved aside, not gone — and into THIS run's own
        // store, so a second restore of the same path cannot destroy it.
        Assert.AreEqual("data/file.bin", Assert.ContainsSingle(receipt.Displaced));
        Assert.AreEqual(
            "precious local edits",
            File.ReadAllText(Path.Combine(output, ".fbp-displaced", "test", "data", "file.bin")));

        // Metadata landed after content: mtime is the captured one.
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds(1_600_000_000_000).UtcDateTime,
            File.GetLastWriteTimeUtc(destination));
        // The receipt is a valid versioned JSON document.
        using var parsed = JsonDocument.Parse(receipt.ToJson());
        Assert.AreEqual(RestoreReceipt.CurrentSchemaVersion, parsed.RootElement.GetProperty("schema_version").GetInt32());
        Assert.AreEqual("Complete", parsed.RootElement.GetProperty("outcome").GetString());
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "restoring mode bits is a POSIX concern; Windows carries an ACL the executor applies differently")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task Restored_files_carry_their_captured_posix_mode()
    {
        // The metadata-after-content ordering is asserted platform-neutrally
        // above; this is the POSIX half of that contract — the captured mode
        // survives the round trip rather than defaulting to the umask.
        var source = new FakeFileSystemSource();
        var node = source.AddFile("data/file.bin", Deterministic(1_024, 7));
        node.Metadata = node.Metadata with { ModifiedAt = 1_600_000_000_000, PosixMode = 0x180 /* 0600 */ };

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("posix-mode.db");
        await CreateOrchestrator(store, keys, hierarchy, catalogue).PublishAsync(Job(source, 0xB2), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat((byte)0xB2, 16).ToArray(), string.Empty, target);
        var output = Path.Combine(SpoolDirectory, "restore-posix");

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output, new RestoreExecutionOptions { RunId = "test", NowUnixMilliseconds = 1_722_700_000_000 }, CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.AreEqual(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(receipt.WrittenTo, "data", "file.bin"))
                & (UnixFileMode.UserRead | UnixFileMode.UserWrite));
    }

    [TestMethod]
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
            Assert.IsTrue(report.TargetSatisfied);
        }

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(rebuilt, snapshotId, string.Empty, target);
        Assert.Contains(item => item.Path == "wanted/data.bin", plan.Items);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var output = Path.Combine(SpoolDirectory, "partial-restore");
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output, new RestoreExecutionOptions { RunId = "test", NowUnixMilliseconds = 1_722_700_000_000 }, CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        SequenceAssert.AreEqual(firstContent, File.ReadAllBytes(Path.Combine(receipt.WrittenTo, "wanted", "data.bin")));

        // Quarantine by default (FR-RST-006): under the chosen output, not in it.
        Assert.StartsWith(output, receipt.WrittenTo, StringComparison.Ordinal);
        Assert.AreNotEqual(output, receipt.WrittenTo);
    }

    [TestMethod]
    [DataRow("../escaped.bin")]
    [DataRow("data/../../escaped.bin")]
    [DataRow("./data/file.bin")]
    [DataRow("data//file.bin")]
    [DataRow("/etc/passwd")]
    [DataRow("data/sub\\file.bin")]
    [DataRow("C:/windows/system32/x.dll")]
    public async Task A_path_that_is_not_plain_components_is_refused(string hostilePath)
    {
        // The store is written by other participants and holds historical
        // data, both of which the threat model treats as adversarial. A
        // manifest is not a trusted source of filenames, so containment has
        // to be the executor's property rather than the writer's good manners.
        var (plan, target, reader, keys) = await OneRealItemAsync(0xD1);

        using (keys)
        using (reader)
        {
            var victim = plan.Items.Single(item => item.Path == "data/file.bin");
            var hostile = plan with
            {
                Items = [new RestorePlanItem(hostilePath, victim.Kind, victim.ObjectId, victim.Length)],
            };

            var output = Path.Combine(SpoolDirectory, "contained");
            var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
                hostile, output,
                new RestoreExecutionOptions { RunId = "test", NowUnixMilliseconds = 1_722_700_000_000 },
                CancellationToken.None);

            Assert.AreEqual(RestoreOutcome.Failed, receipt.Outcome);

            var item = Assert.ContainsSingle(receipt.Items);
            Assert.AreEqual("failed", item.Outcome);
            Assert.StartsWith("refused:", item.Detail, StringComparison.Ordinal);

            // A refusal is not a partial write: nothing escaped, and nothing
            // landed inside the root for this item either.
            Assert.IsFalse(File.Exists(Path.Combine(SpoolDirectory, "escaped.bin")));
            Assert.IsFalse(Directory.Exists(output) && Directory.EnumerateFiles(
                output, "*", SearchOption.AllDirectories).Any());
        }
    }

    [TestMethod]
    public async Task A_skipped_required_item_makes_the_restore_partial()
    {
        // Architecture 08 §3: a restore that recovered 9 999 of 10 000 files
        // is a failed restore that recovered 9 999 files. "Nothing threw" is
        // not the same claim, and reporting it as Complete is the reading that
        // sends someone away believing they have their data.
        var source = new FakeFileSystemSource();
        source.AddFile("data/file.bin", Deterministic(4_096, 7));
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "data/link",
            Kind = FallbackPlan.Filesystem.ScanEntryKind.Symlink,
            LinkTarget = "file.bin"u8.ToArray(),
        });

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("skipped.db");
        await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xD2), CancellationToken.None);

        // A target that cannot materialise symlinks. The plan declares the
        // degradation in advance, which is why the item is skipped rather than
        // failed — and is not a reason to report that nothing is missing.
        var target = RestoreTargetProfile.ForLocalPlatform() with { SupportsSymlinks = false };
        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat((byte)0xD2, 16).ToArray(), string.Empty, target);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, Path.Combine(SpoolDirectory, "partial-out"),
            new RestoreExecutionOptions { RunId = "test", NowUnixMilliseconds = 1_722_700_000_000 },
            CancellationToken.None);

        Assert.Contains(item => item.Outcome == "skipped", receipt.Items);
        Assert.DoesNotContain(item => item.Outcome == "failed", receipt.Items);
        Assert.AreEqual(RestoreOutcome.Partial, receipt.Outcome);
        Assert.AreEqual(RestoreReceipt.CurrentSchemaVersion, receipt.SchemaVersion);
    }

    [TestMethod]
    public async Task Two_restores_of_one_path_do_not_destroy_the_first_displaced_copy()
    {
        var (plan, target, reader, keys) = await OneRealItemAsync(0xD3);

        using (keys)
        using (reader)
        {
            var output = Path.Combine(SpoolDirectory, "twice");
            var destination = Path.Combine(output, "data", "file.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            File.WriteAllText(destination, "the first local edits");
            await Restore("run-one");

            File.WriteAllText(destination, "the second local edits");
            await Restore("run-two");

            // A single shared refuge with overwrite:true was worse than no
            // refuge at all — the second run silently destroyed exactly the
            // file the policy exists to keep.
            Assert.AreEqual(
                "the first local edits",
                File.ReadAllText(Path.Combine(output, ".fbp-displaced", "run-one", "data", "file.bin")));
            Assert.AreEqual(
                "the second local edits",
                File.ReadAllText(Path.Combine(output, ".fbp-displaced", "run-two", "data", "file.bin")));

            async Task Restore(string runId) =>
                await new RestoreExecutor(reader, target).ExecuteAsync(
                    plan, output,
                    new RestoreExecutionOptions
                    {
                        DestinationMode = RestoreDestinationMode.InPlace,
                        RunId = runId,
                        NowUnixMilliseconds = 1_722_700_000_000,
                    },
                    CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task Historical_content_lands_in_quarantine_unless_in_place_is_asked_for()
    {
        var (plan, target, reader, keys) = await OneRealItemAsync(0xD4);

        using (keys)
        using (reader)
        {
            var output = Path.Combine(SpoolDirectory, "fr-rst-006");

            var quarantined = await new RestoreExecutor(reader, target).ExecuteAsync(
                plan, output,
                new RestoreExecutionOptions { RunId = "run", NowUnixMilliseconds = 1_722_700_000_000 },
                CancellationToken.None);

            // FR-RST-006. A snapshot may hold malware that was present when it
            // was captured; restoring it is what the user asked for, and
            // restoring it into the live tree *by default* is not.
            Assert.AreEqual(RestoreOutcome.Complete, quarantined.Outcome);
            Assert.AreNotEqual(output, quarantined.WrittenTo);
            Assert.StartsWith(
                Path.Combine(output, ".fbp-quarantine"), quarantined.WrittenTo, StringComparison.Ordinal);
            Assert.IsTrue(File.Exists(Path.Combine(quarantined.WrittenTo, "data", "file.bin")));
            Assert.IsFalse(File.Exists(Path.Combine(output, "data", "file.bin")));

            // In place is available and is a deliberate choice, not the thing
            // that happens when the operator presses Enter.
            var inPlace = await new RestoreExecutor(reader, target).ExecuteAsync(
                plan, Path.Combine(SpoolDirectory, "fr-rst-006-live"),
                new RestoreExecutionOptions
                {
                    DestinationMode = RestoreDestinationMode.InPlace,
                    RunId = "run",
                    NowUnixMilliseconds = 1_722_700_000_000,
                },
                CancellationToken.None);

            Assert.AreEqual(Path.Combine(SpoolDirectory, "fr-rst-006-live"), inPlace.WrittenTo);
            Assert.IsTrue(File.Exists(Path.Combine(inPlace.WrittenTo, "data", "file.bin")));
        }
    }

    [TestMethod]
    public async Task Restore_selects_by_snapshot_by_path_and_by_destination()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("keep/wanted.bin", Deterministic(3_000, 11));
        source.AddFile("keep/nested/also.bin", Deterministic(1_500, 12));
        source.AddFile("skip/unwanted.bin", Deterministic(2_000, 13));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("catalogue-selectors.db");

        await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xE7), CancellationToken.None);

        // A second snapshot the selector must not reach into: same paths,
        // different content, so choosing the wrong one restores wrong bytes
        // rather than merely failing.
        source.AddFile("keep/wanted.bin", Deterministic(3_000, 99));
        await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xE8), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();

        // Snapshot selector: the earlier one, by identifier.
        // Path selector: a subtree, not the whole snapshot.
        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat((byte)0xE7, 16).ToArray(), "keep", target);

        Assert.Contains(item => item.Path == "keep/wanted.bin", plan.Items);
        Assert.Contains(item => item.Path == "keep/nested/also.bin", plan.Items);
        Assert.DoesNotContain(item => item.Path.StartsWith("skip", StringComparison.Ordinal), plan.Items);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        // Destination selector: an alternate root, not the original location.
        var destination = Path.Combine(SpoolDirectory, "fr-rst-001-elsewhere");
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, destination,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "selectors",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.AreEqual(destination, receipt.WrittenTo);

        // The bytes are the selected snapshot's, not the later one's — which
        // is the only thing that distinguishes a working snapshot selector
        // from one that quietly restores the newest version.
        SequenceAssert.AreEqual(
            Deterministic(3_000, 11),
            await File.ReadAllBytesAsync(Path.Combine(destination, "keep", "wanted.bin")));
        Assert.IsTrue(File.Exists(Path.Combine(destination, "keep", "nested", "also.bin")));
        Assert.IsFalse(Directory.Exists(Path.Combine(destination, "skip")));
    }

    /// <summary>Publishes one file and returns a plan that restores it.</summary>
    private async Task<(RestorePlan Plan, RestoreTargetProfile Target, RepositoryReader Reader, RepositoryKeySet Keys)>
        OneRealItemAsync(byte seed)
    {
        var source = new FakeFileSystemSource();
        source.AddFile("data/file.bin", Deterministic(4_096, 7));

        var store = CreateStore();
        var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue($"catalogue-{seed:x2}.db");
        await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, seed), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, target);

        var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        return (plan, target, reader, keys);
    }
}
