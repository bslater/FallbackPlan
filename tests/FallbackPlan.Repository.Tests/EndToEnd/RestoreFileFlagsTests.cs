using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A restore gives a file's, a folder's and a link's flags back where the
/// target sets them, and its receipt names only those that did not land
/// (FR-RST-004; ADR-0180 Amendment 4). A file's flags go on once every item
/// has landed, because an immutable or append-only file refuses a further
/// name, and a folder's go on last in its own settle, because an immutable
/// folder refuses its own times. A stopped run sets none, so a resume can
/// still write into what it made, and none is set through a link put where
/// a file landed.
/// </summary>
/// <remarks>
/// The executor's cases stand a recorder in for the platform's calls, so
/// they hold on every platform. The last case sets them for real, as this
/// platform and the account running the suite may: no-dump everywhere but
/// Windows, and immutable on macOS, or on Linux with
/// <c>CAP_LINUX_IMMUTABLE</c>, which CI's runner does not hold.
/// </remarks>
[TestClass]
public sealed class RestoreFileFlagsTests : ArchiveTestHarness
{
    private const ulong Shared = 900;

    private const ulong FolderModified = 1_721_500_000_000;

    private const FileFlagBits LinuxWithCapability = FileFlagBits.Immutable | FileFlagBits.AppendOnly | FileFlagBits.NoDump;

    private static readonly RestoreTargetProfile Posix = new()
    {
        CaseSensitive = true,
        SupportsPosixMetadata = true,
        SupportsSymlinks = true,
    };

    [TestMethod]
    public async Task Receipt_TheFlagsTheTargetSets_AreAskedFor_AndOnlyThoseThatDidNotLandAreNamed()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("data/locked.bin", Deterministic(800, 1)).Diagnostics = ["file-flags: immutable, no-dump"];
        source.AddFile("data/appended.log", Deterministic(800, 2)).Diagnostics = ["file-flags: append-only"];
        source.AddFile("data/hidden.bin", Deterministic(800, 3)).Diagnostics = ["file-flags: hidden"];
        source.AddFile("data/plain.bin", Deterministic(800, 4));
        var recorder = new Recorder(lands: FileFlagBits.NoDump | FileFlagBits.AppendOnly);

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xA1, Setting(LinuxWithCapability, FileFlagBits.None, recorder));

        CollectionAssert.AreEquivalent(
            new[]
            {
                (Path.Combine(output, "data", "appended.log"), FileFlagBits.AppendOnly, EntryKind.File),
                (Path.Combine(output, "data", "locked.bin"), FileFlagBits.Immutable | FileFlagBits.NoDump, EntryKind.File),
            },
            recorder.Asked,
            "a flag the target does not set is not asked for, and a file with none asks nothing");
        Assert.AreEqual("file flag(s) not applied: immutable", Item(receipt, "data/locked.bin").Detail);
        Assert.IsNull(Item(receipt, "data/appended.log").Detail, "a flag given back is not named");
        Assert.AreEqual("file flag(s) not applied: hidden", Item(receipt, "data/hidden.bin").Detail);
        Assert.IsNull(Item(receipt, "data/plain.bin").Detail);
        Assert.IsTrue(receipt.Items.All(item => item.Outcome == "restored"), "a flag alone does not change what the content achieved");
    }

    [TestMethod]
    public async Task Run_SetsAFilesFlags_OnceEveryItemHasLanded_SoEachNameOfItsGroupIsMadeFirst()
    {
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 5);
        foreach (var name in new[] { "docs/a.bin", "docs/b.bin" })
        {
            source.AddFile(name, content, linkCount: 2, fileId: Shared).Diagnostics = ["file-flags: immutable"];
        }

        source.AddFile("docs/z.bin", Deterministic(700, 6));
        var output = Path.Combine(SpoolDirectory, "restored-a2");
        var landedWhenAsked = new List<bool>();
        var recorder = new Recorder(
            lands: FileFlagBits.Immutable,
            onApply: _ => landedWhenAsked.Add(new[] { "a.bin", "b.bin", "z.bin" }.All(name => File.Exists(Path.Combine(output, "docs", name)))));

        var receipt = await PublishAndRestoreIntoAsync(source, 0xA2, Setting(LinuxWithCapability, FileFlagBits.None, recorder), output);

        var asked = Assert.ContainsSingle(recorder.Asked, "the group is one file, so its flags are set once");
        Assert.AreEqual((Path.Combine(output, "docs", "a.bin"), FileFlagBits.Immutable, EntryKind.File), asked);
        Assert.IsTrue(Assert.ContainsSingle(landedWhenAsked), "every item had landed before any flag was set");
        Assert.AreEqual("docs/a.bin", Item(receipt, "docs/b.bin").LinkedTo);
        Assert.IsNull(Item(receipt, "docs/a.bin").Detail);
        Assert.IsNull(Item(receipt, "docs/b.bin").Detail);
    }

    [TestMethod]
    public async Task Receipt_ALinkedName_NamesTheFlagsItsFileDidNotGet_AsTheFirstNameDoes()
    {
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 7);
        foreach (var name in new[] { "docs/a.bin", "docs/b.bin" })
        {
            source.AddFile(name, content, linkCount: 2, fileId: Shared).Diagnostics = ["file-flags: immutable, no-dump"];
        }

        var recorder = new Recorder(lands: FileFlagBits.NoDump);

        var (receipt, _) = await PublishAndRestoreAsync(source, 0xA3, Setting(LinuxWithCapability, FileFlagBits.None, recorder));

        Assert.ContainsSingle(recorder.Asked);
        Assert.AreEqual("docs/a.bin", Item(receipt, "docs/b.bin").LinkedTo);
        Assert.AreEqual("file flag(s) not applied: immutable", Item(receipt, "docs/a.bin").Detail);
        Assert.AreEqual(Item(receipt, "docs/a.bin").Detail, Item(receipt, "docs/b.bin").Detail);
    }

    [TestMethod]
    public async Task Run_SetsAFoldersFlags_LastInItsOwnSettle_DeepestFolderFirst()
    {
        var source = new FakeFileSystemSource();
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = FolderModified, FileFlags = FileFlagBits.NoDump };
        source.Folders["docs/sub"] = new EntryMetadata { ModifiedAt = FolderModified, FileFlags = FileFlagBits.NoDump | FileFlagBits.Immutable };
        source.AddFile("docs/sub/a.bin", Deterministic(700, 8));
        var output = Path.Combine(SpoolDirectory, "restored-a4");
        var timesWhenAsked = new List<DateTime>();
        var recorder = new Recorder(
            lands: FileFlagBits.NoDump,
            onApply: path => timesWhenAsked.Add(Directory.GetLastWriteTimeUtc(path)));

        var receipt = await PublishAndRestoreIntoAsync(
            source, 0xA4, Setting(LinuxWithCapability, FileFlagBits.None, recorder), output, FormatVersions.FileFlagsAndLinkForms);

        CollectionAssert.AreEqual(
            new[]
            {
                (Path.Combine(output, "docs", "sub"), FileFlagBits.NoDump | FileFlagBits.Immutable, EntryKind.DirectoryPlaceholder),
                (Path.Combine(output, "docs"), FileFlagBits.NoDump, EntryKind.DirectoryPlaceholder),
            },
            recorder.Asked);
        var captured = DateTimeOffset.FromUnixTimeMilliseconds((long)FolderModified).UtcDateTime;
        CollectionAssert.AreEqual(new[] { captured, captured }, timesWhenAsked, "each folder had its times before its flags");
        Assert.AreEqual("file flag(s) not applied: immutable", Item(receipt, "docs/sub").Detail);
        Assert.IsNull(Item(receipt, "docs").Detail);
        Assert.AreEqual("restored", Item(receipt, "docs/sub").Outcome);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a symlink needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Receipt_ALinksOwnFlags_AreSetWhereTheTargetSetsALinks_AndNamedWhereItDoesNot()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("data/target.bin", Deterministic(500, 9));
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "link",
            Kind = ScanEntryKind.Symlink,
            LinkTarget = "data/target.bin"u8.ToArray(),
            Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000 },
            Diagnostics = ["file-flags: hidden, no-dump"],
        });
        var recorder = new Recorder(lands: FileFlagBits.Hidden | FileFlagBits.NoDump);

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xA5, Setting(FileFlagBits.None, FileFlagBits.Hidden, recorder));

        Assert.AreEqual((Path.Combine(output, "link"), FileFlagBits.Hidden, EntryKind.Symlink), Assert.ContainsSingle(recorder.Asked));
        Assert.AreEqual("file flag(s) not applied: no-dump", Item(receipt, "link").Detail);
    }

    [TestMethod]
    public async Task AStoppedRun_SetsNoFlag_AndNamesTheFlagsOfWhatItLanded()
    {
        var source = new FakeFileSystemSource();
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = FolderModified, FileFlags = FileFlagBits.NoDump };
        var content = Deterministic(5_000, 10);
        foreach (var name in new[] { "docs/a.bin", "docs/b.bin" })
        {
            source.AddFile(name, content, linkCount: 2, fileId: Shared).Diagnostics = ["file-flags: immutable"];
        }

        source.AddFile("docs/c.bin", Deterministic(700, 11));
        var recorder = new Recorder(lands: FileFlagBits.Immutable);
        using var stop = new CancellationTokenSource();

        var receipt = await PublishAndRestoreIntoAsync(
            source, 0xA6, Setting(LinuxWithCapability, FileFlagBits.None, recorder), Path.Combine(SpoolDirectory, "restored-a6"),
            FormatVersions.FileFlagsAndLinkForms,
            makeLink: (existing, name) =>
            {
                stop.Cancel();
                return HardLinks.TryCreate(existing, name);
            },
            cancellationToken: stop.Token);

        Assert.AreEqual(RestoreOutcome.Cancelled, receipt.Outcome, "the premise: the run stopped after the second name");
        Assert.IsEmpty(recorder.Asked, "a resume writes into what the run made, which an immutable flag would refuse");
        Assert.AreEqual("file flag(s) not applied: immutable", Item(receipt, "docs/a.bin").Detail);
        Assert.AreEqual(Item(receipt, "docs/a.bin").Detail, Item(receipt, "docs/b.bin").Detail);
        Assert.AreEqual("file flag(s) not applied: no-dump", Item(receipt, "docs").Detail, "a folder the run made is settled, its flags left");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a symlink needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Run_SetsNoFlagThroughALinkPutWhereAFileLanded()
    {
        // The flags go on once the run has landed everything, so whatever
        // can write beside a file has had the run to put a link in its place.
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(700, 14)).Diagnostics = ["file-flags: no-dump"];
        var content = Deterministic(5_000, 15);
        foreach (var name in new[] { "docs/g1.bin", "docs/g2.bin" })
        {
            source.AddFile(name, content, linkCount: 2, fileId: Shared);
        }

        var output = Path.Combine(SpoolDirectory, "restored-a8");
        var elsewhere = Path.Combine(SpoolDirectory, "elsewhere.bin");
        var recorder = new Recorder(lands: FileFlagBits.NoDump);

        var receipt = await PublishAndRestoreIntoAsync(
            source, 0xA8, Setting(LinuxWithCapability, FileFlagBits.None, recorder), output,
            makeLink: (existing, name) =>
            {
                var landed = Path.Combine(output, "docs", "a.bin");
                File.WriteAllBytes(elsewhere, [1]);
                File.Delete(landed);
                File.CreateSymbolicLink(landed, elsewhere);
                return HardLinks.TryCreate(existing, name);
            });

        Assert.IsEmpty(recorder.Asked, "nothing is set through what took the file's place");
        Assert.AreEqual("file flag(s) not applied: no-dump", Item(receipt, "docs/a.bin").Detail);
    }

    [TestMethod]
    public void TheLocalTarget_SetsWhatThisPlatformMay_AndItsWriterIsTold()
    {
        var target = RestoreTargetProfile.ForLocalPlatform();

        Assert.AreEqual(FileFlags.Settable(EntryKind.File), target.SettableFlags);
        Assert.AreEqual(FileFlags.Settable(EntryKind.Symlink), target.SettableLinkFlags);
        Assert.AreEqual(FileFlags.NeedingPrivilege, target.FlagsNeedingPrivilege);
        Assert.AreSame(FileFlags.OfThisPlatform, target.Flags);

        var recorder = new Recorder(lands: FileFlagBits.None);
        var written = (Posix with { SettableFlags = FileFlagBits.NoDump, SettableLinkFlags = FileFlagBits.Hidden, Flags = recorder }).Metadata;
        Assert.AreEqual(FileFlagBits.NoDump, written.SettableFlags);
        Assert.AreEqual(FileFlagBits.Hidden, written.SettableLinkFlags);
        Assert.AreSame(recorder, written.Flags);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS keep a file's and a folder's flags")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task OnThisPlatform_ARestoreGivesBackTheFlagsTheAccountMaySet_AndAGroupStaysOneFile()
    {
        var source = new FakeFileSystemSource();
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = FolderModified, FileFlags = FileFlagBits.NoDump };
        var content = Deterministic(5_000, 12);
        foreach (var name in new[] { "docs/a.bin", "docs/b.bin" })
        {
            source.AddFile(name, content, linkCount: 2, fileId: Shared).Diagnostics = ["file-flags: immutable, no-dump"];
        }

        source.AddFile("docs/c.bin", Deterministic(700, 13)).Diagnostics = ["file-flags: no-dump"];
        var output = Path.Combine(SpoolDirectory, "restored-a7");
        var first = Path.Combine(output, "docs", "a.bin");
        var second = Path.Combine(output, "docs", "b.bin");
        var wanted = FileFlagBits.Immutable | FileFlagBits.NoDump;
        var landed = wanted & FileFlags.Settable(EntryKind.File);
        try
        {
            var receipt = await PublishAndRestoreIntoAsync(
                source, 0xA7, RestoreTargetProfile.ForLocalPlatform(), output, FormatVersions.FileFlagsAndLinkForms);

            Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
            Assert.AreEqual("docs/a.bin", Item(receipt, "docs/b.bin").LinkedTo, "an immutable file takes no further name, so it is set last");
            Assert.AreEqual(MetadataRules.FileFlagsNotApplied(wanted, landed), Item(receipt, "docs/a.bin").Detail);
            Assert.AreEqual(Item(receipt, "docs/a.bin").Detail, Item(receipt, "docs/b.bin").Detail);
            Assert.IsNull(Item(receipt, "docs/c.bin").Detail);
            Assert.IsNull(Item(receipt, "docs").Detail);
            Assert.AreEqual(landed, OnDisk(first));
            Assert.AreEqual(landed, OnDisk(second));
            Assert.AreEqual(FileFlagBits.NoDump, OnDisk(Path.Combine(output, "docs", "c.bin")));
            Assert.AreEqual(FileFlagBits.NoDump, OnDisk(Path.Combine(output, "docs")));
        }
        finally
        {
            DiskFlags.ClearUnder(output);
        }

        AssertOneFile(first, second);
    }

    /// <summary>The flags this test looks for, as the file or folder at <paramref name="path"/> carries them.</summary>
    private static FileFlagBits OnDisk(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            var darwin = DiskFlags.OnMacOs(path);
            return ((darwin & DiskFlags.DarwinImmutable) != 0 ? FileFlagBits.Immutable : FileFlagBits.None)
                | ((darwin & DiskFlags.DarwinNoDump) != 0 ? FileFlagBits.NoDump : FileFlagBits.None);
        }

        var linux = DiskFlags.OnLinux(path);
        return ((linux & DiskFlags.LinuxImmutable) != 0 ? FileFlagBits.Immutable : FileFlagBits.None)
            | ((linux & DiskFlags.LinuxNoDump) != 0 ? FileFlagBits.NoDump : FileFlagBits.None);
    }

    private static void AssertOneFile(string first, string second)
    {
        var before = File.ReadAllBytes(first);
        File.AppendAllText(second, "+");
        Assert.HasCount(before.Length + 1, File.ReadAllBytes(first), $"{second} is a copy of {first}, not a second name for it");
    }

    private static RestoreTargetProfile Setting(FileFlagBits flags, FileFlagBits linkFlags, IFileFlags calls) =>
        Posix with { SettableFlags = flags, SettableLinkFlags = linkFlags, Flags = calls };

    private static ReceiptItem Item(RestoreReceipt receipt, string path) =>
        receipt.Items.Single(item => item.Path == path);

    private static byte[] SnapshotOf(byte seed) => Enumerable.Repeat(seed, 16).ToArray();

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }

    private static SnapshotJob Job(FakeFileSystemSource source, byte seed) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = SnapshotOf(seed),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "restore-file-flags-tests/1.0",
    };

    private async Task<(RestoreReceipt Receipt, string Output)> PublishAndRestoreAsync(
        FakeFileSystemSource source, byte seed, RestoreTargetProfile target)
    {
        var output = Path.Combine(SpoolDirectory, $"restored-{seed:x2}");
        return (await PublishAndRestoreIntoAsync(source, seed, target, output), output);
    }

    private async Task<RestoreReceipt> PublishAndRestoreIntoAsync(
        FakeFileSystemSource source,
        byte seed,
        RestoreTargetProfile target,
        string output,
        ushort formatVersion = FormatVersions.SealedDataPlane,
        Func<string, string, bool>? makeLink = null,
        CancellationToken cancellationToken = default)
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"flags-{seed:x2}.db"), Repo);
        using (var credential = CreateCredential())
        {
            await new PublicationOrchestrator(
                    SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                    new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, $"sequence-{seed:x2}.txt"))),
                    SpoolDirectory, formatVersion, observer: null, catalogue)
                .PublishAsync(Job(source, seed), CancellationToken.None);
        }

        var plan = RestorePlanner.Plan(catalogue, SnapshotOf(seed), string.Empty, target);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        return await new RestoreExecutor(reader, target, makeLink: makeLink).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                ExistingDestination = ExistingDestinationPolicy.Preserve,
                RunId = "flags",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            cancellationToken);
    }

    /// <summary>Stands in for the platform's calls: records each ask, and lands what it is told to.</summary>
    private sealed class Recorder(FileFlagBits lands, Action<string>? onApply = null) : IFileFlags
    {
        public List<(string Path, FileFlagBits Flags, EntryKind Kind)> Asked { get; } = [];

        public FileFlagBits Apply(string path, FileFlagBits flags, EntryKind kind)
        {
            onApply?.Invoke(path);
            Asked.Add((path, flags, kind));
            return flags & lands;
        }
    }
}
