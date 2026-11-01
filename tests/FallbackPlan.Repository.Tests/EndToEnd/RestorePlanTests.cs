using System.Text.Json;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue.Forensic;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.TestSupport;
using System.Runtime.Versioning;
using FallbackPlan.Filesystem;

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
/// that does not name plain components under the root is refused. A plan is
/// judged by the target's naming rules, stated rather than taken from the
/// running platform: names a POSIX source wrote that Windows would refuse or
/// alter are each named before any byte moves, and Windows' own names plan
/// clean on a POSIX target (ADR-0170), save one longer in bytes than a
/// target that counts in bytes holds (ADR-0170 Amendment 1). Two paths are
/// named as colliding only where the target takes them for one: by case on
/// a case-insensitive target, and by Unicode normalisation only where the
/// target ignores it, as macOS does and Windows does not, so a name's
/// composed and decomposed forms each restore with their own content on
/// Windows and Linux (ADR-0170 Amendment 2). What a target takes for one
/// name is read from the volume of each folder a restore writes to, where
/// that volume says, so a FAT drive on Linux folds case and a case-sensitive
/// folder on Windows does not (ADR-0170 Amendment 5). Where the volume a run writes
/// to takes two of a snapshot's names for one, whatever the plan judged, the
/// second never displaces, replaces or fails vaguely over the first: it fails
/// naming the name the run restored there, which keeps its own content, and
/// under rename it lands beside it (FR-RST-005; ADR-0170 Amendment 3). The three
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
    private CatalogueDb OpenCatalogue(string name = "catalogue.db") =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, name), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, RepositoryWriteCredential credential, CatalogueDb catalogue) =>
        new(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory,
            FormatVersions.SealedDataPlane, observer: null, catalogue);

    private static SnapshotJob Job(FakeFileSystemSource source, byte seed) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
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
    public async Task RestorePlan_CaseCollisionsAndDegradations_AreSurfacedBeforeAnyByteMoves()
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
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, 0xA1), CancellationToken.None);

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

        // What a plan reports, whichever of the service and the command line
        // made it (ADR-0107): each conflict by its path and why.
        var reported = RestorePlanFigures.Conflicts(plan);
        Assert.HasCount(plan.Conflicts.Count, reported);
        Assert.AreEqual(2, reported.Count(line => line.Contains("Collides", StringComparison.Ordinal)));
        Assert.IsTrue(plan.Conflicts.All(conflict => reported.Any(line => line.StartsWith(conflict.Path, StringComparison.Ordinal))));
        Assert.Contains(degradation => degradation.Capability == "posix-metadata", plan.Degradations);
        Assert.Contains(degradation => degradation.Capability == "symlinks", plan.Degradations);
        Assert.AreEqual(200ul, plan.SpaceEstimateBytes);

        // The same tree on a case-sensitive target has no collisions.
        var sensitivePlan = RestorePlanner.Plan(
            catalogue, snapshotId, string.Empty, RestoreTargetProfile.ForLocalPlatform() with { CaseSensitive = true });
        Assert.DoesNotContain(conflict => conflict.Reason.Contains("Collides", StringComparison.Ordinal), sensitivePlan.Conflicts);
    }

    /// <summary>"café.txt" with its accent composed into one character.</summary>
    private const string Composed = "names/caf\u00E9.txt";

    /// <summary>"café.txt" with its accent a mark of its own after the "e".</summary>
    private const string Decomposed = "names/cafe\u0301.txt";

    [TestMethod]
    public async Task RestorePlan_NamesThatDifferOnlyInTheirNormalisation_CollideOnlyWhereTheTargetFoldsThem()
    {
        var source = new FakeFileSystemSource();
        source.AddFile(Composed, Deterministic(64, 1));
        source.AddFile(Decomposed, Deterministic(64, 2));
        source.AddFile("names/Notes.txt", Deterministic(64, 3));
        source.AddFile("names/notes.txt", Deterministic(64, 4));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, 0xA7), CancellationToken.None);
        var snapshotId = Enumerable.Repeat((byte)0xA7, 16).ToArray();

        string[] Colliding(RestoreTargetProfile target) =>
        [
            .. RestorePlanner.Plan(catalogue, snapshotId, string.Empty, target).Conflicts
                .Where(conflict => conflict.Reason.Contains("Collides", StringComparison.Ordinal))
                .Select(conflict => conflict.Path)
                .Order(StringComparer.Ordinal),
        ];

        // Windows folds case and keeps the two forms apart, as NTFS does.
        SequenceAssert.AreEqual(
            new[] { "names/Notes.txt", "names/notes.txt" },
            Colliding(PosixTarget() with { CaseSensitive = false, NormalisationInsensitive = false }));

        // macOS folds both.
        SequenceAssert.AreEqual(
            new[] { Decomposed, "names/Notes.txt", Composed, "names/notes.txt" }.Order(StringComparer.Ordinal).ToArray(),
            Colliding(PosixTarget() with { CaseSensitive = false, NormalisationInsensitive = true }));

        // A case-sensitive volume on macOS still folds the two forms.
        SequenceAssert.AreEqual(
            new[] { Decomposed, Composed }.Order(StringComparer.Ordinal).ToArray(),
            Colliding(PosixTarget() with { NormalisationInsensitive = true }));

        // Linux folds neither.
        Assert.IsEmpty(Colliding(PosixTarget()));

        // Each names the other it would land on.
        var composed = RestorePlanner.Plan(
            catalogue, snapshotId, string.Empty, PosixTarget() with { NormalisationInsensitive = true }).Conflicts
            .Single(conflict => conflict.Path == Composed);
        Assert.Contains(Decomposed, composed.Reason, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RestorePlan_WrittenWhereAVolumeTakesNamesOtherwiseThanItsPlatform_IsJudgedAsTheVolumeTakesThem()
    {
        var source = new FakeFileSystemSource();
        source.AddFile(Composed, Deterministic(64, 1));
        source.AddFile(Decomposed, Deterministic(64, 2));
        source.AddFile("names/Notes.txt", Deterministic(64, 3));
        source.AddFile("names/notes.txt", Deterministic(64, 4));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, 0xA8), CancellationToken.None);
        var snapshotId = Enumerable.Repeat((byte)0xA8, 16).ToArray();

        string[] Colliding(RestoreTargetProfile target) =>
        [
            .. RestorePlanner.Plan(catalogue, snapshotId, string.Empty, target).Conflicts
                .Where(conflict => conflict.Reason.Contains("Collides", StringComparison.Ordinal))
                .Select(conflict => conflict.Path)
                .Order(StringComparer.Ordinal),
        ];

        // The flow's own word for each folder: a FAT drive, a case-sensitive
        // folder, a folder that folds case itself, and one nothing is said of.
        var fat = Path.Combine(SpoolDirectory, "fat");
        var caseSensitive = Path.Combine(SpoolDirectory, "case-sensitive");
        var casefolded = Path.Combine(SpoolDirectory, "casefolded");
        VolumeNaming.OfInFlow = folder =>
            folder == fat ? new VolumeNaming(CaseSensitive: false, NormalisationInsensitive: false)
            : folder == caseSensitive ? new VolumeNaming(CaseSensitive: true, NormalisationInsensitive: false)
            : folder == casefolded ? new VolumeNaming(CaseSensitive: false, NormalisationInsensitive: true)
            : null;
        string[] casePair = ["names/Notes.txt", "names/notes.txt"];
        var all = new[] { Decomposed, "names/Notes.txt", Composed, "names/notes.txt" }.Order(StringComparer.Ordinal).ToArray();

        // A FAT drive on Linux folds case though Linux's own volumes do not.
        SequenceAssert.AreEqual(casePair, Colliding(PosixTarget().AsWrittenTo([fat])));

        // A case-sensitive folder on Windows keeps what Windows' volumes fold.
        Assert.IsEmpty(Colliding((PosixTarget() with { CaseSensitive = false }).AsWrittenTo([caseSensitive])));

        // A folder that folds case itself folds the two forms of a name too.
        SequenceAssert.AreEqual(all, Colliding(PosixTarget().AsWrittenTo([casefolded])));

        // Written to more than one volume, the plan folds what any of them folds.
        SequenceAssert.AreEqual(all, Colliding(PosixTarget().AsWrittenTo([caseSensitive, casefolded])));

        // A volume nothing is said of, or no folder at all, leaves the
        // platform's word as it was stated.
        Assert.IsEmpty(Colliding(PosixTarget().AsWrittenTo([Path.Combine(SpoolDirectory, "unsaid")])));
        SequenceAssert.AreEqual(
            casePair, Colliding((PosixTarget() with { CaseSensitive = false }).AsWrittenTo([Path.Combine(SpoolDirectory, "unsaid")])));
        SequenceAssert.AreEqual(casePair, Colliding((PosixTarget() with { CaseSensitive = false }).AsWrittenTo([])));

        // A plan is made again only where how the target takes names moved,
        // by case or by normalisation.
        Assert.IsTrue(PosixTarget().NamesAs(PosixTarget() with { MaxPathBytes = 1 }));
        Assert.IsFalse(PosixTarget().NamesAs(PosixTarget() with { CaseSensitive = false }));
        Assert.IsFalse(PosixTarget().NamesAs(PosixTarget() with { NormalisationInsensitive = true }));
    }

    [TestMethod]
    public void ForLocalPlatform_NormalisationInsensitive_IsWhatThisMachinesVolumeDoes()
    {
        Assert.AreEqual(OperatingSystem.IsMacOS(), RestoreTargetProfile.ForLocalPlatform().NormalisationInsensitive);

        // The volume the tests write to answers the same: on macOS a name
        // written composed is found decomposed, and elsewhere it is not.
        var folder = Path.Combine(SpoolDirectory, "normalisation");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "caf\u00E9.txt"), "composed");
        Assert.AreEqual(
            RestoreTargetProfile.ForLocalPlatform().NormalisationInsensitive,
            File.Exists(Path.Combine(folder, "cafe\u0301.txt")));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows | TestPlatforms.Linux, "macOS takes the two forms for one name")]
    [PlatformTrait(TestPlatforms.Windows | TestPlatforms.Linux)]
    public async Task RestoreExecution_NamesThatDifferOnlyInTheirNormalisation_EachLandWithItsOwnContent_WhereTheTargetKeepsThemApart()
    {
        var composedContent = Deterministic(4_096, 6);
        var decomposedContent = Deterministic(4_096, 7);
        var source = new FakeFileSystemSource();
        source.AddFile(Composed, composedContent);
        source.AddFile(Decomposed, decomposedContent);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, 0xB2), CancellationToken.None);
        var snapshotId = Enumerable.Repeat((byte)0xB2, 16).ToArray();

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, snapshotId, string.Empty, target);
        Assert.IsEmpty(plan.Conflicts);

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var output = Path.Combine(SpoolDirectory, "restore-out");
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions { RunId = "test", NowUnixMilliseconds = 1_722_700_000_000 },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.IsEmpty(receipt.Displaced);
        SequenceAssert.AreEqual(composedContent, File.ReadAllBytes(Path.Combine(receipt.WrittenTo, "names", "caf\u00E9.txt")));
        SequenceAssert.AreEqual(decomposedContent, File.ReadAllBytes(Path.Combine(receipt.WrittenTo, "names", "cafe\u0301.txt")));
    }

    [TestMethod]
    [DataRow(ExistingDestinationPolicy.Preserve)]
    [DataRow(ExistingDestinationPolicy.Replace)]
    [DataRow(ExistingDestinationPolicy.Fail)]
    public async Task RestoreExecution_TwoNamesTheVolumeTakesForOne_TheSecondFailsNamingTheFirst_WhichKeepsItsContent(
        ExistingDestinationPolicy policy)
    {
        var (receipt, contents) = await RestoreNamesAVolumeMayFoldAsync(0xB3, policy);

        // Judged by the volume the run wrote to, which is the test's own:
        // Windows' and macOS's fold case, macOS's normalisation too, and
        // Linux's neither.
        var foldsCase = VolumeFolds("Probe.txt", "probe.txt");
        var foldsForm = VolumeFolds("caf\u00E9.txt", "cafe\u0301.txt");
        AssertOneLandedPerName(receipt, contents, foldsCase, "names/Notes.txt", "names/notes.txt");
        AssertOneLandedPerName(receipt, contents, foldsForm, Composed, Decomposed);
        Assert.IsEmpty(receipt.Displaced, "nothing the run restored is moved aside by it");
        Assert.AreEqual(foldsCase || foldsForm ? RestoreOutcome.Failed : RestoreOutcome.Complete, receipt.Outcome);
    }

    [TestMethod]
    public async Task RestoreExecution_TwoNamesTheVolumeTakesForOne_UnderRename_EachLandsWithItsOwnContent()
    {
        var (receipt, contents) = await RestoreNamesAVolumeMayFoldAsync(0xB4, ExistingDestinationPolicy.WriteBeside);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.IsEmpty(receipt.Displaced);
        foreach (var item in receipt.Items.Where(item => contents.ContainsKey(item.Path)))
        {
            Assert.AreEqual("restored", item.Outcome, item.Detail);
            SequenceAssert.AreEqual(
                contents[item.Path], File.ReadAllBytes(Path.Combine(receipt.WrittenTo, item.WrittenAs ?? item.Path)));
        }
    }

    /// <summary>
    /// Restores a snapshot holding two names that differ only in case and two
    /// that differ only in normalisation, under <paramref name="policy"/>, to
    /// this test's volume.
    /// </summary>
    private async Task<(RestoreReceipt Receipt, Dictionary<string, byte[]> Contents)> RestoreNamesAVolumeMayFoldAsync(
        byte seed, ExistingDestinationPolicy policy)
    {
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["names/Notes.txt"] = Deterministic(4_096, 8),
            ["names/notes.txt"] = Deterministic(4_096, 9),
            [Composed] = Deterministic(4_096, 10),
            [Decomposed] = Deterministic(4_096, 11),
        };
        var source = new FakeFileSystemSource();
        foreach (var (path, content) in contents)
        {
            source.AddFile(path, content);
        }

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, seed), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, target);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan,
            Path.Combine(SpoolDirectory, "restore-out"),
            new RestoreExecutionOptions { RunId = "test", NowUnixMilliseconds = 1_722_700_000_000, ExistingDestination = policy },
            CancellationToken.None);
        return (receipt, contents);
    }

    /// <summary>Whether this test's volume finds a file written as <paramref name="written"/> when asked for <paramref name="asked"/>.</summary>
    private bool VolumeFolds(string written, string asked)
    {
        var folder = Path.Combine(SpoolDirectory, $"folds-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, written), []);
        return File.Exists(Path.Combine(folder, asked));
    }

    /// <summary>
    /// Where the volume keeps <paramref name="one"/> and <paramref name="other"/>
    /// apart, each landed with its own content. Where it takes them for one,
    /// one landed with its own content and the other failed naming it.
    /// </summary>
    private static void AssertOneLandedPerName(
        RestoreReceipt receipt, Dictionary<string, byte[]> contents, bool folds, string one, string other)
    {
        var items = receipt.Items.Where(item => item.Path == one || item.Path == other).ToList();
        Assert.HasCount(2, items);
        if (!folds)
        {
            foreach (var item in items)
            {
                Assert.AreEqual("restored", item.Outcome, item.Detail);
                SequenceAssert.AreEqual(contents[item.Path], File.ReadAllBytes(Path.Combine(receipt.WrittenTo, item.Path)));
            }

            return;
        }

        var landed = Assert.ContainsSingle(items.Where(item => item.Outcome == "restored"));
        var refused = Assert.ContainsSingle(items.Where(item => item.Outcome == "failed"));
        Assert.Contains($"this run restored {landed.Path} there", refused.Detail!, StringComparison.Ordinal);
        SequenceAssert.AreEqual(contents[landed.Path], File.ReadAllBytes(Path.Combine(receipt.WrittenTo, landed.Path)));
    }

    [TestMethod]
    public async Task RestorePlan_PosixNamesTargetingWindowsRules_ReportsEveryUnrepresentableNameBeforeAnyByteMoves()
    {
        // Names a POSIX source holds as written and Windows would refuse or
        // alter: a device name it would open instead of a file, characters it
        // refuses, a trailing period or space it strips, a component longer
        // than it allows.
        string[] windowsRefuses =
        [
            "names/CON", "names/aux.txt", "names/what?.txt", "names/star*.txt",
            "names/trailing.", "names/trailing ", "names/" + new string('n', 256),
        ];

        // A name no target restores: a drive marker is refused everywhere.
        const string NoTargetRestores = "names/drive:name";
        string[] kept = ["names/fine.txt", "names/My Docs.txt"];

        var source = new FakeFileSystemSource();
        var seed = (byte)1;
        foreach (var path in windowsRefuses.Append(NoTargetRestores).Concat(kept))
        {
            source.AddFile(path, Deterministic(64, seed++));
        }

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, 0xA4), CancellationToken.None);
        var snapshotId = Enumerable.Repeat((byte)0xA4, 16).ToArray();

        var windows = RestorePlanner.Plan(catalogue, snapshotId, string.Empty, new RestoreTargetProfile
        {
            CaseSensitive = false,
            SupportsPosixMetadata = false,
            SupportsSymlinks = false,
            WindowsNamingRules = true,
        });

        // Each is named, with why, by the plan: nothing has been read or
        // written yet, and the operator decides with the list in hand.
        CollectionAssert.AreEquivalent(
            windowsRefuses.Append(NoTargetRestores).ToArray(), windows.Conflicts.Select(conflict => conflict.Path).ToArray());
        Assert.IsTrue(
            windows.Conflicts.Where(conflict => conflict.Path != NoTargetRestores)
                .All(conflict => conflict.Reason.Contains("Windows", StringComparison.Ordinal)),
            string.Join(Environment.NewLine, windows.Conflicts));
        Assert.HasCount(windows.Conflicts.Count, RestorePlanFigures.Conflicts(windows));

        // On a POSIX target only the name no target restores is a conflict.
        var posix = RestorePlanner.Plan(catalogue, snapshotId, string.Empty, PosixTarget());
        Assert.AreEqual(NoTargetRestores, Assert.ContainsSingle(posix.Conflicts).Path);

        // And the profile of the platform the restore runs on names by
        // Windows' rules exactly where that platform is Windows.
        Assert.AreEqual(OperatingSystem.IsWindows(), RestoreTargetProfile.ForLocalPlatform().WindowsNamingRules);
    }

    [TestMethod]
    public async Task RestorePlan_WindowsNamesTargetingPosixRules_PlansWithoutDegradation()
    {
        // The other direction is clean, and a test that says so is what keeps
        // a fix for the first from turning it into a problem.
        var source = new FakeFileSystemSource();
        source.AddFile("Docs/Readme.TXT", Deterministic(64, 1));
        source.AddFile("Docs/My Docs/a b.txt", Deterministic(64, 2));
        source.AddFile("Docs/über straße " + new string('w', 200) + ".txt", Deterministic(64, 3));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, 0xA5), CancellationToken.None);
        var snapshotId = Enumerable.Repeat((byte)0xA5, 16).ToArray();

        var posix = RestorePlanner.Plan(catalogue, snapshotId, string.Empty, PosixTarget());
        Assert.IsEmpty(posix.Conflicts);
        Assert.IsEmpty(posix.Degradations);

        // They are Windows' own names, so its rules keep them too.
        var windows = RestorePlanner.Plan(
            catalogue, snapshotId, string.Empty, PosixTarget() with { CaseSensitive = false, WindowsNamingRules = true });
        Assert.IsEmpty(windows.Conflicts);
    }

    [TestMethod]
    public async Task RestorePlan_WindowsNamesTargetingABytesLimit_NamesEveryNameLongerThanTheTargetHolds()
    {
        // Windows counts a name in UTF-16 units and Linux in bytes, so a
        // name Windows holds can be one Linux does not: 86 three-byte
        // characters are 86 units and 258 bytes (ADR-0170 Amendment 1).
        var tooLong = "names/" + new string('語', 86);
        var longest = "names/" + new string('語', 85);
        var ascii = "names/" + new string('n', 255);
        var source = new FakeFileSystemSource();
        source.AddFile(tooLong, Deterministic(64, 1));
        source.AddFile(longest, Deterministic(64, 2));
        source.AddFile(ascii, Deterministic(64, 3));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, 0xA6), CancellationToken.None);
        var snapshotId = Enumerable.Repeat((byte)0xA6, 16).ToArray();

        var bytes = RestorePlanner.Plan(catalogue, snapshotId, string.Empty, PosixTarget() with { MaxNameBytes = 255 });
        var conflict = Assert.ContainsSingle(bytes.Conflicts);
        Assert.AreEqual(tooLong, conflict.Path);
        Assert.Contains("255 bytes", conflict.Reason, StringComparison.Ordinal);
        Assert.Contains("258", conflict.Reason, StringComparison.Ordinal);

        // A target that does not count in bytes, or whose limit is not
        // known, is not judged by one.
        Assert.IsEmpty(RestorePlanner.Plan(catalogue, snapshotId, string.Empty, PosixTarget()).Conflicts);
        Assert.IsEmpty(RestorePlanner.Plan(
            catalogue, snapshotId, string.Empty, PosixTarget() with { CaseSensitive = false, WindowsNamingRules = true }).Conflicts);

        // The platform the restore runs on states its limit where it is
        // Linux, whose filesystems commonly hold 255 bytes in a name.
        uint? stated = OperatingSystem.IsLinux() ? 255u : null;
        Assert.AreEqual(stated, RestoreTargetProfile.ForLocalPlatform().MaxNameBytes);
    }

    /// <summary>A case-sensitive target that keeps POSIX metadata and links, stated rather than taken from the running platform.</summary>
    /// <summary>This machine's target, as one that writes no alternate stream back, as only Windows does (ADR-0174).</summary>
    /// <summary>
    /// This machine's target, keeping a file's streams in no form: neither as
    /// streams nor as attributes of their names (ADR-0179).
    /// </summary>
    private static RestoreTargetProfile WritesNoStreams() =>
        RestoreTargetProfile.ForLocalPlatform() with { SupportsAlternateStreams = false, SupportsExtendedAttributes = false };

    private static RestoreTargetProfile PosixTarget() => new()
    {
        CaseSensitive = true,
        SupportsPosixMetadata = true,
        SupportsSymlinks = true,
        MaxPathBytes = 4096,
    };

    [TestMethod]
    public async Task RestorePlan_AFileCarryingAlternateStreams_DeclaresTheDegradation()
    {
        // RR-6's honesty half: the format captures alternate data streams
        // end to end, and a target that writes none back gets only the main
        // stream — so a plan over a tree that carries them must SAY so,
        // exactly as it says so for symlinks and POSIX metadata, instead of
        // the receipt later reporting complete while the streams are dropped.
        var source = new FakeFileSystemSource();
        var carrier = source.AddFile("data/streams.bin", Deterministic(4_096, 3));
        carrier.AlternateStreams["Zone.Identifier"] = "[ZoneTransfer]\nZoneId=3"u8.ToArray();
        source.AddFile("data/plain.bin", Deterministic(4_096, 5), fileId: 9_002);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue("ads-plan.db");
        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, 0xA7), CancellationToken.None);

        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat((byte)0xA7, 16).ToArray(), string.Empty, WritesNoStreams());

        var degradation = Assert.ContainsSingle(
            plan.Degradations.Where(candidate => candidate.Capability == "alternate-streams"));
        Assert.Contains("main stream", degradation.Detail, StringComparison.Ordinal);

        // A tree with no stream-carrying file declares nothing — the
        // degradation is presence-gated like the symlink one, not blanket.
        var plainPlan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat((byte)0xA7, 16).ToArray(), "data/plain.bin", WritesNoStreams());
        Assert.DoesNotContain(candidate => candidate.Capability == "alternate-streams", plainPlan.Degradations);
    }

    [TestMethod]
    public async Task RestoreExecution_AFileCarryingAlternateStreams_ReportsTheDegradedOutcomeNotComplete()
    {
        // The receipt half of RR-6's honesty: a restore that wrote the main
        // stream and dropped the alternate ones did NOT completely restore
        // that file, and `complete` was a lie the receipt told for as long
        // as nothing consumed the plan's declaration.
        var content = Deterministic(50_000, 9);
        var source = new FakeFileSystemSource();
        var carrier = source.AddFile("data/streams.bin", content);
        carrier.AlternateStreams["Zone.Identifier"] = "[ZoneTransfer]\nZoneId=3"u8.ToArray();

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue("ads-receipt.db");
        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, 0xA8), CancellationToken.None);

        var target = WritesNoStreams();
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat((byte)0xA8, 16).ToArray(), string.Empty, target);

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var output = Path.Combine(SpoolDirectory, "ads-out");
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "ads-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        // The main stream restored byte-identical — the shortfall is honest,
        // not destructive.
        SequenceAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(output, "data", "streams.bin")));

        // And the receipt says what actually happened: the item degraded,
        // the run is Partial, and nothing reads as complete while streams
        // were dropped (FR-RST-005's spirit, applied to metadata).
        var item = Assert.ContainsSingle(receipt.Items.Where(candidate => candidate.Path == "data/streams.bin"));
        Assert.AreEqual("degraded", item.Outcome);
        Assert.IsNotNull(item.Detail);
        Assert.Contains("alternate", item.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(RestoreOutcome.Partial, receipt.Outcome);
    }

    [TestMethod]
    public async Task RestoreExecution_AnExistingFileIsInTheWay_DisplacesItAndAppliesMetadataAfterContent()
    {
        var content = Deterministic(50_000, 5);
        var source = new FakeFileSystemSource();
        var node = source.AddFile("data/file.bin", content);
        node.Metadata = node.Metadata with { ModifiedAt = 1_600_000_000_000, PosixMode = 0x180 /* 0600 */ };

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, 0xB1), CancellationToken.None);

        var snapshotId = Enumerable.Repeat((byte)0xB1, 16).ToArray();
        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, snapshotId, string.Empty, target);
        Assert.IsEmpty(plan.Conflicts);

        var output = Path.Combine(SpoolDirectory, "restore-out");

        // A pre-existing file at the destination — displacement material.
        var destination = Path.Combine(output, "data", "file.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "precious local edits");

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
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
    public async Task RestoreExecution_APosixSource_RestoresEachFilesCapturedMode()
    {
        // The metadata-after-content ordering is asserted platform-neutrally
        // above; this is the POSIX half of that contract — the captured mode
        // survives the round trip rather than defaulting to the umask.
        var source = new FakeFileSystemSource();
        var node = source.AddFile("data/file.bin", Deterministic(1_024, 7));
        node.Metadata = node.Metadata with { ModifiedAt = 1_600_000_000_000, PosixMode = 0x180 /* 0600 */ };

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue("posix-mode.db");
        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, 0xB2), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat((byte)0xB2, 16).ToArray(), string.Empty, target);
        var output = Path.Combine(SpoolDirectory, "restore-posix");

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
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
    public async Task RestoreExecution_AfterATargetedForensicRebuild_RestoresThatSnapshot()
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
        using var credential = CreateCredential();
        using (var live = OpenCatalogue())
        {
            await CreateOrchestrator(store, keys, credential, live).PublishAsync(Job(source, 0xC1), CancellationToken.None);
            source.AddFile("other/late.bin", Deterministic(90_000, 9));
            await CreateOrchestrator(store, keys, credential, live).PublishAsync(Job(source, 0xC2), CancellationToken.None);
        }

        // Fresh catalogue, targeted forensic rebuild — no index objects used.
        var snapshotId = Enumerable.Repeat((byte)0xC1, 16).ToArray();
        using var rebuilt = OpenCatalogue("forensic.db");
        using (var rebuilder = new ForensicRebuilder(store, Repo, credential))
        {
            var report = await rebuilder.RebuildAsync(
                rebuilt, new ForensicTarget.Snapshot(snapshotId), CancellationToken.None);
            Assert.IsTrue(report.TargetSatisfied);
        }

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(rebuilt, snapshotId, string.Empty, target);
        Assert.Contains(item => item.Path == "wanted/data.bin", plan.Items);

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
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
    public async Task RestorePlan_APathIsNotPlainComponents_IsRefused(string hostilePath)
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
    public async Task RestoreExecution_APreSeededSymlinkPointsOutsideTheRoot_IsNotWrittenThrough()
    {
        // Lexical containment (Path.GetFullPath) is not enough: an attacker who
        // can seed the restore target — or an earlier item that restored a
        // symlink — can leave a directory link pointing outside the root, and a
        // later item whose path traverses it escapes though every component
        // reads as plain. Containment must resolve links, not just normalise
        // dots (architecture 08 §3).
        var (plan, target, reader, keys) = await OneRealItemAsync(0xD7);

        using (keys)
        using (reader)
        {
            var output = Path.Combine(SpoolDirectory, "seeded");
            Directory.CreateDirectory(output);
            var outside = Path.Combine(SpoolDirectory, "outside");
            Directory.CreateDirectory(outside);

            // A directory symlink inside the root pointing out of it.
            Directory.CreateSymbolicLink(Path.Combine(output, "link"), outside);

            var victim = plan.Items.Single(item => item.Path == "data/file.bin");
            var throughLink = plan with
            {
                Items = [new RestorePlanItem("link/file.bin", victim.Kind, victim.ObjectId, victim.Length)],
            };

            var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
                throughLink, output,
                new RestoreExecutionOptions
                {
                    RunId = "test",
                    NowUnixMilliseconds = 1_722_700_000_000,
                    DestinationMode = RestoreDestinationMode.InPlace,
                },
                CancellationToken.None);

            Assert.AreEqual(RestoreOutcome.Failed, receipt.Outcome);
            Assert.StartsWith("refused:", Assert.ContainsSingle(receipt.Items).Detail, StringComparison.Ordinal);

            // The file must not have been written through the link to the
            // outside directory.
            Assert.IsFalse(File.Exists(Path.Combine(outside, "file.bin")),
                "a file was written through a symlink that escapes the restore root");
        }
    }

    [TestMethod]
    public async Task RestoreExecution_ARequiredItemIsSkipped_ReportsTheRestoreAsPartial()
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
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue("skipped.db");
        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xD2), CancellationToken.None);

        // A target that cannot materialise symlinks. The plan declares the
        // degradation in advance, which is why the item is skipped rather than
        // failed — and is not a reason to report that nothing is missing.
        var target = RestoreTargetProfile.ForLocalPlatform() with { SupportsSymlinks = false };
        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat((byte)0xD2, 16).ToArray(), string.Empty, target);

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
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
    public async Task RestoreExecution_Cancelled_ReportsCancelledWithAReceiptRatherThanThrowing()
    {
        // Architecture 08 §3's outcome enum promises Cancelled; it was
        // unreachable because the loop threw on the cancellation token before
        // the receipt was ever built, so a cancelled restore produced no
        // account of what it had done. Stopping cooperatively between items
        // leaves every restored item whole and still yields a receipt.
        var (plan, target, reader, keys) = await OneRealItemAsync(0xD9);

        using (keys)
        using (reader)
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();

            var output = Path.Combine(SpoolDirectory, "cancelled-out");
            var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
                plan, output,
                new RestoreExecutionOptions { RunId = "test", NowUnixMilliseconds = 1_722_700_000_000 },
                cancelled.Token);

            Assert.AreEqual(RestoreOutcome.Cancelled, receipt.Outcome);
            Assert.IsEmpty(receipt.Items);
        }
    }

    [TestMethod]
    public async Task RestoreExecution_OneFileOfSeveralCannotBeRestored_ReportsFailedAndKeepsTheRest()
    {
        // FR-RST-005 / architecture 08 §3, the aggregate claim the suite never
        // held: a multi-file restore where one file fails and the others
        // succeed must report Failed — not Complete, not Partial — while the
        // files that did restore are present and the receipt names the one that
        // did not. Skipping (Partial) and containment refusal are covered
        // elsewhere; this is a required item that simply could not be produced.
        var source = new FakeFileSystemSource();
        source.AddFile("data/a.bin", Deterministic(4_096, 3));
        source.AddFile("data/b.bin", Deterministic(4_096, 7));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue("one-fails.db");
        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xD8), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat((byte)0xD8, 16).ToArray(), string.Empty, target);

        // One real file, and one whose object identifier resolves to nothing —
        // the shape a missing or unreadable blob leaves the executor: the file
        // is required, the plan names it, and it cannot be produced.
        var good = plan.Items.Single(item => item.Path == "data/a.bin");
        var doomed = plan.Items.Single(item => item.Path == "data/b.bin");
        var mixed = plan with
        {
            Items = [good, doomed with { ObjectId = ObjectId.FromBytes(Enumerable.Repeat((byte)0xEE, ObjectId.Size).ToArray()) }],
        };

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var output = Path.Combine(SpoolDirectory, "one-fails-out");
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            mixed, output,
            new RestoreExecutionOptions { RunId = "test", NowUnixMilliseconds = 1_722_700_000_000 },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Failed, receipt.Outcome);

        var failed = Assert.ContainsSingle(receipt.Items.Where(item => item.Outcome == "failed"));
        Assert.AreEqual("data/b.bin", failed.Path);

        var restored = Assert.ContainsSingle(receipt.Items.Where(item => item.Outcome == "restored" && item.Bytes > 0));
        Assert.AreEqual("data/a.bin", restored.Path);

        // The file that could be restored is on disk; the one that failed is not.
        Assert.IsTrue(File.Exists(Path.Combine(receipt.WrittenTo, "data", "a.bin")));
        Assert.IsFalse(File.Exists(Path.Combine(receipt.WrittenTo, "data", "b.bin")));
    }

    [TestMethod]
    public async Task RestoreExecution_TheSamePathRestoredTwice_KeepsTheFirstDisplacedCopy()
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
    public async Task RestoreExecution_HistoricalContentWithoutInPlaceAsked_LandsInQuarantine()
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
    public async Task RestorePlan_SelectingBySnapshotPathAndDestination_ChoosesTheRightItems()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("keep/wanted.bin", Deterministic(3_000, 11));
        source.AddFile("keep/nested/also.bin", Deterministic(1_500, 12));
        source.AddFile("skip/unwanted.bin", Deterministic(2_000, 13));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue("catalogue-selectors.db");

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xE7), CancellationToken.None);

        // A second snapshot the selector must not reach into: same paths,
        // different content, so choosing the wrong one restores wrong bytes
        // rather than merely failing.
        source.AddFile("keep/wanted.bin", Deterministic(3_000, 99));
        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xE8), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();

        // Snapshot selector: the earlier one, by identifier.
        // Path selector: a subtree, not the whole snapshot.
        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat((byte)0xE7, 16).ToArray(), "keep", target);

        Assert.Contains(item => item.Path == "keep/wanted.bin", plan.Items);
        Assert.Contains(item => item.Path == "keep/nested/also.bin", plan.Items);
        Assert.DoesNotContain(item => item.Path.StartsWith("skip", StringComparison.Ordinal), plan.Items);

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
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
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue($"catalogue-{seed:x2}.db");
        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, seed), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, target);

        var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        return (plan, target, reader, keys);
    }
}
