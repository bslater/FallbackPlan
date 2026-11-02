using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A link capture names a mount point is published with that form
/// (FR-MAN-003; ADR-0183 Amendment 1; ADR-0184 §2): from format 6 its
/// manifest keeps the form in key 14 and names it nowhere, and below format 6
/// no key keeps it, so its capture diagnostics name it. A symbolic link's
/// manifest says nothing of a form at either, and only a link is given one.
/// On Windows a real directory junction is captured so.
/// </summary>
[TestClass]
public sealed class LinkFormCaptureTests : ArchiveTestHarness
{
    private const string JunctionTarget = @"C:\Users\Shared\Projects";

    private readonly string _sourceRoot =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-link-form-capture-tests", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public async Task AtFormatSix_ALinkCapturedAsAMountPoint_IsKeptInKeyFourteen_AndNamedNowhere()
    {
        var published = await PublishAsync(Linked(), FormatVersions.FileFlagsAndLinkForms);

        var junction = await ManifestOfAsync(published, "junction");
        Assert.AreEqual(EntryKind.Symlink, junction.EntryKind);
        Assert.AreEqual(LinkForm.MountPoint, junction.LinkForm);
        Assert.AreEqual(JunctionTarget, Encoding.UTF8.GetString(junction.LinkTarget!.Value.Span));
        CollectionAssert.AreEqual(new[] { "captured-inconsistent: 2" }, junction.CaptureDiagnostics.ToArray());

        var link = await ManifestOfAsync(published, "link");
        Assert.AreEqual(LinkForm.SymbolicLink, link.LinkForm);
        Assert.IsEmpty(link.CaptureDiagnostics);
    }

    [TestMethod]
    public async Task AtFormatFive_ALinkCapturedAsAMountPoint_StaysNamed_AndNoKeyKeepsIt()
    {
        // A reader of format 5 refuses key 14, so a format-5 repository
        // never holds it.
        var published = await PublishAsync(Linked(), FormatVersions.DeltaCheckpoints);

        var junction = await ManifestOfAsync(published, "junction");
        Assert.AreEqual(LinkForm.SymbolicLink, junction.LinkForm);
        Assert.Contains("link-form: mount-point", junction.CaptureDiagnostics);
        Assert.AreEqual(JunctionTarget, Encoding.UTF8.GetString(junction.LinkTarget!.Value.Span));

        Assert.AreEqual(LinkForm.SymbolicLink, (await ManifestOfAsync(published, "link")).LinkForm);
    }

    [TestMethod]
    public async Task AtFormatSix_OnlyALinkIsGivenAForm_AnEntryOfAnotherKindStaysAsItWasNamed()
    {
        // A reader refuses key 14 on anything but a link, so a form named on
        // another kind of entry is left as said rather than kept.
        var source = new FakeFileSystemSource();
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "pipe",
            Kind = ScanEntryKind.Special,
            Diagnostics = ["special-kind: fifo", "link-form: mount-point"],
        });

        var published = await PublishAsync(source, FormatVersions.FileFlagsAndLinkForms);

        var special = await ManifestOfAsync(published, "pipe");
        Assert.AreEqual(EntryKind.Special, special.EntryKind);
        Assert.AreEqual(LinkForm.SymbolicLink, special.LinkForm);
        Assert.Contains("link-form: mount-point", special.CaptureDiagnostics);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows makes a directory junction")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task OnWindows_AtFormatSix_ADirectoryJunction_IsKeptAsAMountPoint_NamingItsFolder()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_sourceRoot, "projects")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "kept.bin"), [1, 2, 3]);
        Assert.IsTrue(Junctions.Make(Path.Combine(_sourceRoot, "junction"), folder), "the premise: a junction was made");

        var published = await PublishAsync(new LocalFileSystemSource(), FormatVersions.FileFlagsAndLinkForms, _sourceRoot);

        var junction = await ManifestOfAsync(published, "junction");
        Assert.AreEqual(EntryKind.Symlink, junction.EntryKind);
        Assert.AreEqual(LinkForm.MountPoint, junction.LinkForm);
        Assert.AreEqual(folder, Encoding.UTF8.GetString(junction.LinkTarget!.Value.Span));
        Assert.DoesNotContain("link-form: mount-point", junction.CaptureDiagnostics);
        Assert.ContainsSingle(published.Files.Where(file => file.RelativePath.EndsWith("kept.bin", StringComparison.Ordinal)));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows makes a directory junction")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task OnWindows_AtFormatFive_ADirectoryJunction_IsNamedAMountPoint()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_sourceRoot, "projects")).FullName;
        Assert.IsTrue(Junctions.Make(Path.Combine(_sourceRoot, "junction"), folder), "the premise: a junction was made");

        var published = await PublishAsync(new LocalFileSystemSource(), FormatVersions.DeltaCheckpoints, _sourceRoot);

        var junction = await ManifestOfAsync(published, "junction");
        Assert.AreEqual(LinkForm.SymbolicLink, junction.LinkForm);
        Assert.Contains("link-form: mount-point", junction.CaptureDiagnostics);
    }

    /// <summary>
    /// A junction, named a mount point beside a diagnostic of another kind,
    /// and a symbolic link, named nothing, as capture gives them.
    /// </summary>
    private static FakeFileSystemSource Linked()
    {
        var source = new FakeFileSystemSource();
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "junction",
            Kind = ScanEntryKind.Symlink,
            LinkTarget = Encoding.UTF8.GetBytes(JunctionTarget),
            Metadata = new EntryMetadata { ModifiedAt = 1_700_000_000_000, FileAttributes = 0x410 },
            Diagnostics = ["captured-inconsistent: 2", "link-form: mount-point"],
        });
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "link",
            Kind = ScanEntryKind.Symlink,
            LinkTarget = Encoding.UTF8.GetBytes("projects/elsewhere"),
            Metadata = new EntryMetadata { ModifiedAt = 1_700_000_000_000 },
        });
        return source;
    }

    private async Task<PublishedTreeSnapshot> PublishAsync(IFileSystemSource source, ushort formatVersion, string root = "/")
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();

        return await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
                SpoolDirectory, formatVersion)
            .PublishAsync(
                new SnapshotJob
                {
                    Source = source,
                    Roots = [new ScanRoot(root)],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat((byte)0x44, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_000,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "link-form-capture-tests/1.0",
                },
                CancellationToken.None);
    }

    private async Task<FileVersionManifest> ManifestOfAsync(PublishedTreeSnapshot published, string name)
    {
        using var keys = CreateKeys();
        using var reader = new RepositoryReader(Repo, keys, CreateStore(), Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var read = await reader.ReadSegmentAsync(
            published.Files.Single(file => file.RelativePath == name).ObjectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        return FileVersionManifestCodec.Decode(read.Plaintext!);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(_sourceRoot))
        {
            try
            {
                Directory.Delete(_sourceRoot, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A stale temp directory, never a failed assertion.
            }
        }

        base.Dispose(disposing);
    }
}
