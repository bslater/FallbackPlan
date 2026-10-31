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
/// A restore on Windows makes a symlink a link where the account may make
/// one (FR-RST-004; ADR-0181; architecture 06 §3, "preserve, privilege
/// permitting"), as it already does on Linux and macOS, and the plan no
/// longer says links will be skipped there (FR-RST-003). A link to a folder
/// is a folder's link: by its captured attribute bits where Windows captured
/// it, and by what it names otherwise. Its target is written with Windows'
/// separators. A link gets its own modification, access and creation times
/// back, onto the link and never through it, and the rule the plan and the
/// receipt hold a link to grants on every platform what that platform sets
/// on one (ADR-0181 Amendment 2). A link Windows captured gets its attribute
/// bits back the same way, and a bit no restore sets is named as a file's
/// is (ADR-0181 Amendment 3).
/// </summary>
/// <remarks>
/// Windows makes a link only for an account that holds the privilege to, or
/// on a machine in developer mode. CI's account does. Where neither holds,
/// the restore skips links as before, and the plan says so.
/// </remarks>
[TestClass]
public sealed class RestoreWindowsLinkTests : ArchiveTestHarness
{
    private const uint FolderBit = 0x10;
    private const uint ReparsePointBit = 0x400;
    private const uint HiddenBit = 0x2;
    private const uint SystemBit = 0x4;
    private const uint ArchiveBit = 0x20;
    private const uint EncryptedBit = 0x4000;
    private const uint CompressedBit = 0x800;

    /// <summary>The bits the attribute tests read: those a restore writes, and those that say what an entry is.</summary>
    private const FileAttributes Watched = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive
        | FileAttributes.Directory | FileAttributes.ReparsePoint;
    private const ulong Modified = 1_722_000_000_000;

    /// <summary>When a link was modified: 2021-03-04.</summary>
    private const ulong LinkModified = 1_614_834_367_890;

    /// <summary>When a link was last read: 2022-04-15.</summary>
    private const ulong LinkAccessed = 1_650_000_000_000;

    /// <summary>When a link was made: 2020-05-20.</summary>
    private const ulong LinkCreated = 1_590_000_000_000;

    [TestMethod]
    public void TheLocalProfile_MakesLinks_WhereTheAccountMayMakeOne() =>
        Assert.AreEqual(SymbolicLinks.CanCreate, RestoreTargetProfile.ForLocalPlatform().SupportsSymlinks);

    [TestMethod]
    public void TheRule_ForALink_GrantsWhatThisPlatformSetsOnALink_AndNeverItsDescriptorOrStreams()
    {
        // A target that keeps everything, so the platform alone decides.
        var everything = RestoreTargetProfile.ForLocalPlatform() with
        {
            SupportsSymlinks = true,
            SupportsPosixMetadata = true,
            SupportsCreationTimes = true,
            SupportsFileAttributes = true,
            SupportsSecurityDescriptors = true,
            SupportsAlternateStreams = true,
        };

        var applied = RestoreMetadata.Applied(EntryKind.Symlink, everything);

        Assert.AreEqual(SymlinkMetadata.CanSetTimes, applied.HasFlag(CapturedMetadata.ModifiedAt | CapturedMetadata.AccessedAt));
        Assert.AreEqual(SymlinkMetadata.CanSetCreationTime, applied.HasFlag(CapturedMetadata.CreatedAt));
        Assert.AreEqual(!OperatingSystem.IsWindows(), applied.HasFlag(CapturedMetadata.PosixMode), "Windows writes no permissions");
        Assert.AreEqual(SymlinkMetadata.CanSetAttributes, applied.HasFlag(CapturedMetadata.FileAttributes));
        Assert.AreEqual(
            CapturedMetadata.None,
            applied & (CapturedMetadata.SecurityDescriptor | CapturedMetadata.AlternateStreams),
            "a link's descriptor and streams are written onto nothing");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows decides whether a link may be made")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task OnWindows_ALinkCapturedElsewhere_IsMadeALink_ThatResolves_WithWindowsSeparators()
    {
        Assert.IsTrue(SymbolicLinks.CanCreate, "the premise: this account may make a link");
        var content = Deterministic(900, 1);
        var source = new FakeFileSystemSource();
        source.AddFile("data/target.bin", content).Metadata = new EntryMetadata { ModifiedAt = Modified };
        AddLink(source, "link", "data/target.bin", fileAttributes: null);

        var (plan, _, receipt, output) = await PublishAndRestoreAsync(source, 0xB1);

        Assert.DoesNotContain(degradation => degradation.Capability == "symlinks", plan.Degradations);
        Assert.AreEqual("restored", receipt.Items.Single(item => item.Path == "link").Outcome);
        var link = new FileInfo(Path.Combine(output, "link"));
        Assert.AreEqual(@"data\target.bin", link.LinkTarget);
        Assert.IsFalse(Directory.Exists(link.FullName), "a link to a file is a file's link");
        CollectionAssert.AreEqual(content, File.ReadAllBytes(link.FullName), "the link resolves to the file it named");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows tells a folder's link from a file's")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task OnWindows_ALinkToAFolder_IsAFoldersLink_ByWhatItNames_OrByItsCapturedBits()
    {
        Assert.IsTrue(SymbolicLinks.CanCreate, "the premise: this account may make a link");
        var source = new FakeFileSystemSource();
        source.AddFile("docs/inside.txt", Deterministic(300, 2)).Metadata = new EntryMetadata { ModifiedAt = Modified };
        source.AddFile("notes.txt", Deterministic(300, 3)).Metadata = new EntryMetadata { ModifiedAt = Modified };

        // Each sorts before what it names, so the folder is not yet made when
        // the link is: what the plan makes is what decides.
        AddLink(source, "a-docs", "docs", fileAttributes: null);
        AddLink(source, "a-windows-folder", "notes.txt", fileAttributes: FolderBit | ReparsePointBit);
        AddLink(source, "a-windows-file", "docs", fileAttributes: ReparsePointBit);

        var (_, _, receipt, output) = await PublishAndRestoreAsync(source, 0xB2);

        Assert.IsTrue(receipt.Items.Where(item => item.Path.StartsWith("a-", StringComparison.Ordinal)).All(item => item.Outcome == "restored"));
        Assert.IsTrue(Directory.Exists(Path.Combine(output, "a-docs")), "a link naming a folder the restore makes is a folder's link");
        Assert.AreEqual("docs", new DirectoryInfo(Path.Combine(output, "a-docs")).LinkTarget);
        Assert.IsTrue(File.Exists(Path.Combine(output, "a-docs", "inside.txt")), "and it resolves");
        Assert.AreEqual("notes.txt", new DirectoryInfo(Path.Combine(output, "a-windows-folder")).LinkTarget);
        Assert.IsTrue(
            (File.GetAttributes(Path.Combine(output, "a-windows-folder")) & FileAttributes.Directory) != 0,
            "a link Windows captured as a folder's is made one");
        Assert.IsTrue(
            (File.GetAttributes(Path.Combine(output, "a-windows-file")) & FileAttributes.Directory) == 0,
            "and one it captured as a file's is made one, whatever it names here");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "a link's own times are written through Windows' own handle on it")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task OnWindows_ALink_GetsItsOwnTimesBack_AndWhatItPointsAtKeepsItsOwn()
    {
        Assert.IsTrue(SymbolicLinks.CanCreate, "the premise: this account may make a link");
        var times = new EntryMetadata { ModifiedAt = LinkModified, AccessedAt = LinkAccessed, CreatedAt = LinkCreated };
        var source = new FakeFileSystemSource();
        source.AddFile("data/target.bin", Deterministic(500, 4)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, AccessedAt = Modified, CreatedAt = Modified };
        source.Folders["data"] = new EntryMetadata { ModifiedAt = Modified, CreatedAt = Modified };

        // Each sorts after the file it names, so a write that went through a
        // link would land on a file already given its own times.
        AddLink(source, "z-file", "data/target.bin", times);
        AddLink(source, "z-folder", "data", times);
        AddLink(source, "z-nothing", "not-there.bin", times);

        var (_, declared, receipt, output) = await PublishAndRestoreAsync(source, 0xB3);

        Assert.DoesNotContain(degradation => degradation.Detail.Contains("symlink", StringComparison.Ordinal), declared);
        foreach (var name in new[] { "z-file", "z-folder", "z-nothing" })
        {
            var item = receipt.Items.Single(item => item.Path == name);
            Assert.AreEqual("restored", item.Outcome, name);
            Assert.IsNull(item.NotApplied, name);
            FileSystemInfo link = name == "z-folder" ? new DirectoryInfo(Path.Combine(output, name)) : new FileInfo(Path.Combine(output, name));
            Assert.AreEqual(
                (Utc(LinkModified), Utc(LinkAccessed), Utc(LinkCreated)),
                (link.LastWriteTimeUtc, link.LastAccessTimeUtc, link.CreationTimeUtc),
                name);
        }

        var target = Path.Combine(output, "data", "target.bin");
        Assert.AreEqual(
            (Utc(Modified), Utc(Modified), Utc(Modified)),
            (File.GetLastWriteTimeUtc(target), File.GetLastAccessTimeUtc(target), File.GetCreationTimeUtc(target)),
            "a link's times went through it");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "a link's own bits are written through Windows' own handle on it")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task OnWindows_ALinkWindowsCaptured_GetsItsAttributeBitsBack_AndABitNoRestoreSetsIsNamed_CompressionAmongThem()
    {
        Assert.IsTrue(SymbolicLinks.CanCreate, "the premise: this account may make a link");
        var source = new FakeFileSystemSource();
        source.AddFile("data/target.bin", Deterministic(400, 5)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, FileAttributes = ArchiveBit };
        source.Folders["data"] = new EntryMetadata { ModifiedAt = Modified, FileAttributes = FolderBit };
        AddLink(source, "z-hidden", "data/target.bin", fileAttributes: ReparsePointBit | HiddenBit | SystemBit);
        AddLink(source, "z-hidden-folder", "data", fileAttributes: FolderBit | ReparsePointBit | HiddenBit);
        AddLink(source, "z-encrypted", "data/target.bin", fileAttributes: ReparsePointBit | HiddenBit | EncryptedBit);

        // This target compresses files and folders, and nothing compresses a link.
        AddLink(source, "z-compressed", "data/target.bin", fileAttributes: ReparsePointBit | CompressedBit);

        var (_, declared, receipt, output) = await PublishAndRestoreAsync(source, 0xB4);

        Assert.IsTrue(RestoreTargetProfile.ForLocalPlatform().SupportsCompression, "the premise: this target compresses");
        var unset = Assert.ContainsSingle(declared.Where(degradation => degradation.Detail.Contains("symlink", StringComparison.Ordinal)));
        Assert.AreEqual("unset-file-attributes", unset.Capability);
        Assert.Contains("2 symlink(s) include bits no restore sets: Compressed, Encrypted.", unset.Detail, StringComparison.Ordinal);
        foreach (var name in new[] { "z-hidden", "z-hidden-folder" })
        {
            var item = receipt.Items.Single(item => item.Path == name);
            Assert.AreEqual("restored", item.Outcome, name);
            Assert.IsNull(item.Detail, name);
            Assert.IsNull(item.NotApplied, name);
        }

        Assert.AreEqual(
            FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            File.GetAttributes(Path.Combine(output, "z-hidden")) & Watched);
        Assert.AreEqual(
            FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.ReparsePoint,
            File.GetAttributes(Path.Combine(output, "z-hidden-folder")) & Watched);
        var encrypted = receipt.Items.Single(item => item.Path == "z-encrypted");
        Assert.AreEqual("file attribute(s) not applied: Encrypted (not set by a restore)", encrypted.Detail);
        CollectionAssert.AreEqual(new[] { "file_attributes" }, encrypted.NotApplied!.ToArray());
        Assert.AreEqual(FileAttributes.Hidden | FileAttributes.ReparsePoint, File.GetAttributes(Path.Combine(output, "z-encrypted")) & Watched);
        Assert.AreEqual(
            "file attribute(s) not applied: Compressed (not set by a restore)",
            receipt.Items.Single(item => item.Path == "z-compressed").Detail);

        Assert.AreEqual(FileAttributes.Archive, File.GetAttributes(Path.Combine(output, "data", "target.bin")) & Watched, "a link's bits went through it");
        Assert.AreEqual(FileAttributes.Directory, File.GetAttributes(Path.Combine(output, "data")) & Watched, "a folder's link's bits went through it");
    }

    private static void AddLink(FakeFileSystemSource source, string path, string target, uint? fileAttributes) =>
        AddLink(source, path, target, new EntryMetadata { ModifiedAt = Modified, FileAttributes = fileAttributes });

    private static void AddLink(FakeFileSystemSource source, string path, string target, EntryMetadata metadata) =>
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = path,
            Kind = ScanEntryKind.Symlink,
            LinkTarget = System.Text.Encoding.UTF8.GetBytes(target),
            Metadata = metadata,
        });

    private static DateTime Utc(ulong milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds).UtcDateTime;

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
        ClientVersion = "restore-windows-link-tests/1.0",
    };

    /// <summary>
    /// Backs <paramref name="source"/> up, plans its restore here, and runs
    /// it, answering the plan, the metadata lines the plan declares over the
    /// items' facts, the receipt, and where it landed.
    /// </summary>
    private async Task<(RestorePlan Plan, IReadOnlyList<MetadataDegradation> Declared, RestoreReceipt Receipt, string Output)> PublishAndRestoreAsync(
        FakeFileSystemSource source, byte seed)
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using (var credential = CreateCredential())
        using (var publishing = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"links-{seed:x2}.db"), Repo))
        {
            await new PublicationOrchestrator(
                    SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                    new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, $"sequence-{seed:x2}.txt"))),
                    SpoolDirectory, FormatVersions.SealedDataPlane, observer: null, publishing)
                .PublishAsync(Job(source, seed), CancellationToken.None);
        }

        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"links-{seed:x2}.db"), Repo);
        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, SnapshotOf(seed), string.Empty, target);
        var declared = RestoreMetadata.Declare(
            plan, (await RestoreBlobSet.ResolveAsync(catalogue, plan, store, Repo, keys, CancellationToken.None)).Facts, target);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var output = Path.Combine(SpoolDirectory, $"restored-{seed:x2}");
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "windows-links",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);
        return (plan, declared, receipt, output);
    }
}
