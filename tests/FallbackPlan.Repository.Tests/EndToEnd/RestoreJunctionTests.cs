using System.Text;
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
/// A restore makes a junction a junction again where the target makes one
/// (FR-RST-004; ADR-0183 Amendment 2): a link whose manifest names it a
/// mount point, in key 14 from format 6 or in its capture diagnostics before
/// it, and which names a folder by its drive's path. A symbolic link beside
/// it is made as before. Where the maker refuses one, that item fails alone,
/// in its words. A target that makes no junction makes it a symbolic link to
/// the same path, and its plan and receipt say so (FR-RST-003).
/// </summary>
/// <remarks>
/// The executor's cases stand a recorder in for the junction maker, so they
/// hold on every platform. The last case makes a real junction on Windows.
/// Every case makes its symbolic links for real, which on Windows needs the
/// privilege CI's account holds.
/// </remarks>
[TestClass]
public sealed class RestoreJunctionTests : ArchiveTestHarness
{
    private const string FolderOnADrive = @"C:\Users\Shared\Projects";
    private const string MadeAsALink = "made as a symbolic link: this target makes no junction";
    private const ulong Modified = 1_722_000_000_000;

    [TestMethod]
    public async Task Receipt_AJunction_IsMadeByTheJunctionMaker_WhereTheTargetMakesThem_AndASymbolicLinkIsNot()
    {
        var maker = new Maker();
        var target = RestoreTargetProfile.ForLocalPlatform() with { MakesJunctions = true, Junctions = maker };

        var (declared, receipt, output) = await PublishAndRestoreAsync(Linked(), 0xB1, FormatVersions.FileFlagsAndLinkForms, target);

        CollectionAssert.AreEqual(new[] { (Path.Combine(output, "junction"), FolderOnADrive) }, maker.Made);
        Assert.AreEqual("restored", Item(receipt, "junction").Outcome);
        Assert.DoesNotContain("symbolic link", Item(receipt, "junction").Detail ?? string.Empty, StringComparison.Ordinal);

        Assert.AreEqual("restored", Item(receipt, "link").Outcome);
        Assert.IsNotNull(LinkTargetOf(Path.Combine(output, "link")), "a symbolic link is made a symbolic link");
        Assert.DoesNotContain(degradation => degradation.Capability == "junctions", declared);
    }

    [TestMethod]
    public async Task Receipt_AJunctionNamedInItsDiagnostics_BelowFormatSix_IsMadeByTheJunctionMakerToo()
    {
        var maker = new Maker();
        var target = RestoreTargetProfile.ForLocalPlatform() with { MakesJunctions = true, Junctions = maker };

        var (_, receipt, output) = await PublishAndRestoreAsync(Linked(), 0xB2, FormatVersions.DeltaCheckpoints, target);

        CollectionAssert.AreEqual(new[] { (Path.Combine(output, "junction"), FolderOnADrive) }, maker.Made);
        Assert.AreEqual("restored", Item(receipt, "junction").Outcome);
    }

    [TestMethod]
    public async Task Receipt_AJunctionTheMakerRefuses_FailsThatItemAlone_InTheMakersWords()
    {
        var maker = new Maker(refusal: "refused by the target (error 5)");
        var target = RestoreTargetProfile.ForLocalPlatform() with { MakesJunctions = true, Junctions = maker };

        var (_, receipt, output) = await PublishAndRestoreAsync(Linked(), 0xB3, FormatVersions.FileFlagsAndLinkForms, target);

        var junction = Item(receipt, "junction");
        Assert.AreEqual("failed", junction.Outcome);
        Assert.AreEqual("refused by the target (error 5)", junction.Detail);
        Assert.IsFalse(Path.Exists(Path.Combine(output, "junction")));
        Assert.AreEqual("restored", Item(receipt, "link").Outcome);
    }

    [TestMethod]
    public async Task OnATargetThatMakesNoJunction_AJunctionIsMadeASymbolicLinkToItsPath_AndThePlanAndTheReceiptSaySo()
    {
        var maker = new Maker();
        var target = RestoreTargetProfile.ForLocalPlatform() with { MakesJunctions = false, Junctions = maker };

        var (declared, receipt, output) = await PublishAndRestoreAsync(Linked(), 0xB4, FormatVersions.FileFlagsAndLinkForms, target);

        Assert.IsEmpty(maker.Made);
        Assert.AreEqual(FolderOnADrive, LinkTargetOf(Path.Combine(output, "junction")));
        Assert.AreEqual("restored", Item(receipt, "junction").Outcome);
        Assert.Contains(MadeAsALink, Item(receipt, "junction").Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(MadeAsALink, Item(receipt, "link").Detail ?? string.Empty, StringComparison.Ordinal);

        Assert.AreEqual(
            "1 symlink(s) captured as Windows junctions will be made as symbolic links to the same path: this target makes no junction.",
            Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "junctions")).Detail);
    }

    [TestMethod]
    public async Task AMountPointNamingNoDrivesFolder_IsMadeASymbolicLink_AndNothingSaysItIsAJunction()
    {
        // Capture never names one so; a manifest that does is made what its
        // target can be.
        var source = new FakeFileSystemSource();
        AddLink(source, "odd", "projects/elsewhere", ["link-form: mount-point"]);
        var maker = new Maker();

        foreach (var (makes, seed) in new[] { (true, (byte)0xB5), (false, (byte)0xB6) })
        {
            var target = RestoreTargetProfile.ForLocalPlatform() with { MakesJunctions = makes, Junctions = maker };

            var (declared, receipt, output) = await PublishAndRestoreAsync(source, seed, FormatVersions.FileFlagsAndLinkForms, target);

            Assert.IsEmpty(maker.Made);
            Assert.IsNotNull(LinkTargetOf(Path.Combine(output, "odd")));
            Assert.DoesNotContain(MadeAsALink, Item(receipt, "odd").Detail ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(degradation => degradation.Capability == "junctions", declared);
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows makes a junction")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task OnWindows_AJunction_IsRestoredAJunctionNamingItsFolder_AndAFoldersSymbolicLinkASymbolicLink()
    {
        var folder = Directory.CreateDirectory(Path.Combine(SpoolDirectory, "projects")).FullName;
        File.WriteAllText(Path.Combine(folder, "kept.txt"), "through a junction");
        var source = new FakeFileSystemSource();
        AddLink(source, "junction", folder, ["link-form: mount-point"]);
        AddLink(source, "link", folder, []);

        var (declared, receipt, output) = await PublishAndRestoreAsync(
            source, 0xB7, FormatVersions.FileFlagsAndLinkForms, RestoreTargetProfile.ForLocalPlatform());

        var junction = Path.Combine(output, "junction");
        Assert.AreEqual("restored", Item(receipt, "junction").Outcome, Item(receipt, "junction").Detail);
        Assert.IsTrue(Junctions.IsMountPoint(junction), "a junction, not a folder's symbolic link");
        Assert.AreEqual(folder, new DirectoryInfo(junction).LinkTarget);
        Assert.AreEqual("through a junction", File.ReadAllText(Path.Combine(junction, "kept.txt")));

        var link = Path.Combine(output, "link");
        Assert.AreEqual("restored", Item(receipt, "link").Outcome, Item(receipt, "link").Detail);
        Assert.IsFalse(Junctions.IsMountPoint(link), "a folder's symbolic link stays one");
        Assert.AreEqual(folder, new DirectoryInfo(link).LinkTarget);
        Assert.DoesNotContain(degradation => degradation.Capability == "junctions", declared);
    }

    /// <summary>A junction to a folder on a drive, named a mount point as capture names one, and a symbolic link.</summary>
    private static FakeFileSystemSource Linked()
    {
        var source = new FakeFileSystemSource();
        AddLink(source, "junction", FolderOnADrive, ["link-form: mount-point"]);
        AddLink(source, "link", "projects/elsewhere", []);
        return source;
    }

    private static void AddLink(FakeFileSystemSource source, string path, string target, IReadOnlyList<string> diagnostics) =>
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = path,
            Kind = ScanEntryKind.Symlink,
            LinkTarget = Encoding.UTF8.GetBytes(target),
            Metadata = new EntryMetadata { ModifiedAt = Modified, FileAttributes = 0x410 },
            Diagnostics = diagnostics,
        });

    /// <summary>What the link at <paramref name="path"/> names, whether a file's link or a folder's.</summary>
    private static string? LinkTargetOf(string path) =>
        new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget;

    private static ReceiptItem Item(RestoreReceipt receipt, string path) =>
        receipt.Items.Single(item => item.Path == path);

    private static byte[] SnapshotOf(byte seed) => Enumerable.Repeat(seed, 16).ToArray();

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
        ClientVersion = "restore-junction-tests/1.0",
    };

    /// <summary>
    /// Backs <paramref name="source"/> up at <paramref name="formatVersion"/>,
    /// plans its restore to <paramref name="target"/>, and runs it, answering
    /// the metadata lines the plan declares, the receipt, and where it landed.
    /// </summary>
    private async Task<(IReadOnlyList<MetadataDegradation> Declared, RestoreReceipt Receipt, string Output)> PublishAndRestoreAsync(
        FakeFileSystemSource source, byte seed, ushort formatVersion, RestoreTargetProfile target)
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"junctions-{seed:x2}.db"), Repo);
        using (var credential = CreateCredential())
        {
            await new PublicationOrchestrator(
                    SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                    new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
                    SpoolDirectory, formatVersion, observer: null, catalogue)
                .PublishAsync(Job(source, seed), CancellationToken.None);
        }

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
                RunId = "junctions",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);
        return (declared, receipt, output);
    }

    /// <summary>
    /// Stands in for the platform's junction maker: records each junction
    /// asked for and makes a folder in its place, or refuses in the words it
    /// is given and makes nothing.
    /// </summary>
    private sealed class Maker(string? refusal = null) : IMountPoints
    {
        public List<(string Path, string Target)> Made { get; } = [];

        public string? Make(string path, string target)
        {
            if (refusal is not null)
            {
                return refusal;
            }

            Made.Add((path, target));
            Directory.CreateDirectory(path);
            return null;
        }
    }
}
