using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The recovery tool makes a junction a junction again where the output
/// makes one, as the executor does (FR-RST-005; ADR-0183 Amendment 3): a
/// link whose manifest names it a mount point, in key 14 from format 6 or in
/// its capture diagnostics before it, and which names a folder by its
/// drive's path, once whatever stood at its name is cleared, as for any
/// link. A symbolic link beside it is made as before. Where the
/// maker refuses one, that entry fails alone, noted in its words. A
/// platform that makes none makes it a symbolic link to the same path, and
/// notes so.
/// </summary>
/// <remarks>
/// The tool's cases stand a recorder in for the junction maker, so they hold
/// on every platform. Two cases use the platform's own: off Windows, where
/// none is made, and on Windows, where a real one is. Every case makes its
/// symbolic links for real, which on Windows needs the privilege CI's
/// account holds.
/// </remarks>
[TestClass]
public sealed class RecoveryJunctionTests : IDisposable
{
    private const string FolderOnADrive = @"C:\Users\Shared\Projects";
    private const ulong Modified = 1_722_000_000_000;
    private const string PassphraseText = "recovery-junctions-passphrase!!";

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-recovery-junctions", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public async Task RecoveryRestore_AJunction_IsMadeByTheJunctionMaker_AndASymbolicLinkBesideItIsNot()
    {
        var output = Path.Combine(_root, "out");
        var maker = new Maker();

        var report = await RestoreIntoAsync(Linked(), output, FormatVersions.FileFlagsAndLinkForms, maker);

        CollectionAssert.AreEqual(new[] { (Path.Combine(output, "junction"), FolderOnADrive) }, maker.Made);
        Assert.AreEqual(2, report.Restored);
        Assert.AreEqual(0, report.Failed);
        Assert.IsNotNull(LinkTargetOf(Path.Combine(output, "link")), "a symbolic link is made a symbolic link");
        Assert.DoesNotContain(note => note.Kind == RecoveryNoteKind.JunctionMadeAsALink, report.Notes);
    }

    [TestMethod]
    public async Task RecoveryRestore_AJunctionNamedInItsDiagnostics_BelowFormatSix_IsMadeByTheJunctionMakerToo()
    {
        var output = Path.Combine(_root, "out");
        var maker = new Maker();

        await RestoreIntoAsync(Linked(), output, FormatVersions.DeltaCheckpoints, maker);

        CollectionAssert.AreEqual(new[] { (Path.Combine(output, "junction"), FolderOnADrive) }, maker.Made);
    }

    [TestMethod]
    public async Task RecoveryRestore_AJunctionTheMakerRefuses_FailsThatEntryAlone_NotedInTheMakersWords()
    {
        var output = Path.Combine(_root, "out");

        var report = await RestoreIntoAsync(Linked(), output, FormatVersions.FileFlagsAndLinkForms, new Maker(refusal: "refused by the target (error 5)"));

        var refused = Assert.ContainsSingle(report.Notes.Where(note => note.Kind == RecoveryNoteKind.WriteFailed));
        Assert.AreEqual("junction", refused.Path);
        Assert.AreEqual("refused by the target (error 5)", refused.Detail);
        Assert.AreEqual(1, report.Failed);
        Assert.AreEqual(1, report.Restored, "the symbolic link beside it is restored");
        Assert.IsFalse(Path.Exists(Path.Combine(output, "junction")));
    }

    [TestMethod]
    public async Task RecoveryRestore_AFileStandingAtAJunctionsName_IsClearedFirst_AsAtAnyLinks()
    {
        var output = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        File.WriteAllText(Path.Combine(output, "junction"), "already here");
        var maker = new Maker();

        var report = await RestoreIntoAsync(Linked(), output, FormatVersions.FileFlagsAndLinkForms, maker);

        CollectionAssert.AreEqual(new[] { (Path.Combine(output, "junction"), FolderOnADrive) }, maker.Made);
        Assert.AreEqual(2, report.Restored, string.Join("; ", report.Notes));
        Assert.IsTrue(Directory.Exists(Path.Combine(output, "junction")), "what the maker made stands, not the file");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Windows makes junctions")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task RecoveryRestore_OffWindows_AJunctionIsMadeASymbolicLinkToItsPath_AndNotedSo()
    {
        var output = Path.Combine(_root, "out");

        var report = await RestoreIntoAsync(Linked(), output, FormatVersions.FileFlagsAndLinkForms, junctions: null);

        Assert.AreEqual(FolderOnADrive, LinkTargetOf(Path.Combine(output, "junction")));
        Assert.AreEqual(2, report.Restored);
        var note = Assert.ContainsSingle(report.Notes.Where(note => note.Kind == RecoveryNoteKind.JunctionMadeAsALink));
        Assert.AreEqual("junction", note.Path);
        Assert.AreEqual("junction: made as a symbolic link: this target makes no junction", note.ToString());
    }

    [TestMethod]
    public async Task RecoveryRestore_AMountPointNamingNoDrivesFolder_IsMadeASymbolicLink_AndNothingSaysItIsAJunction()
    {
        // Capture never names one so; a manifest that does is made what its
        // target can be.
        var source = new FakeFileSystemSource();
        AddLink(source, "odd", "projects/elsewhere", ["link-form: mount-point"]);
        var output = Path.Combine(_root, "out");
        var maker = new Maker();

        var report = await RestoreIntoAsync(source, output, FormatVersions.FileFlagsAndLinkForms, maker);

        Assert.IsEmpty(maker.Made);
        Assert.IsNotNull(LinkTargetOf(Path.Combine(output, "odd")));
        Assert.DoesNotContain(note => note.Kind == RecoveryNoteKind.JunctionMadeAsALink, report.Notes);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows makes a junction")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task RecoveryRestore_OnWindows_AJunctionIsRestoredAJunctionNamingItsFolder_AndAFoldersSymbolicLinkASymbolicLink()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "projects")).FullName;
        File.WriteAllText(Path.Combine(folder, "kept.txt"), "through a junction");
        var source = new FakeFileSystemSource();
        AddLink(source, "junction", folder, ["link-form: mount-point"]);
        AddLink(source, "link", folder, []);
        var output = Path.Combine(_root, "out");

        var report = await RestoreIntoAsync(source, output, FormatVersions.FileFlagsAndLinkForms, junctions: null);

        Assert.AreEqual(2, report.Restored, string.Join("; ", report.Notes));
        var junction = Path.Combine(output, "junction");
        Assert.IsTrue(Junctions.IsMountPoint(junction), "a junction, not a folder's symbolic link");
        Assert.AreEqual(folder, new DirectoryInfo(junction).LinkTarget);
        Assert.AreEqual("through a junction", File.ReadAllText(Path.Combine(junction, "kept.txt")));

        var link = Path.Combine(output, "link");
        Assert.IsFalse(Junctions.IsMountPoint(link), "a folder's symbolic link stays one");
        Assert.AreEqual(folder, new DirectoryInfo(link).LinkTarget);
        Assert.DoesNotContain(note => note.Kind == RecoveryNoteKind.JunctionMadeAsALink, report.Notes);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale temp directory, never a failed assertion.
        }
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

    /// <summary>
    /// Backs <paramref name="source"/> up at <paramref name="formatVersion"/>
    /// into a repository made from the passphrase, then restores its one
    /// snapshot into <paramref name="output"/> with the recovery session
    /// alone, making junctions through <paramref name="junctions"/>, or the
    /// platform's own where it is null.
    /// </summary>
    private async Task<RecoveryRestoreReport> RestoreIntoAsync(
        FakeFileSystemSource source, string output, ushort formatVersion, IMountPoints? junctions)
    {
        var store = new LocalFileSystemObjectStore(Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("n")));
        using (var passphrase = Passphrase.Create(PassphraseText))
        {
            var (repository, authority) = await RepositoryLifecycle.CreateFromPassphraseAsync(
                store, passphrase, RepositoryCreationSettings.Default with { FormatVersion = formatVersion },
                createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None);
            using var _repository = repository;
            using var _authority = authority;

            var spool = Directory.CreateDirectory(Path.Combine(_root, "spool-" + Guid.NewGuid().ToString("n"))).FullName;
            await new PublicationOrchestrator(
                    CapturePolicy.Default with { SegmentSize = SegmentSize.Create(64 * 1024) },
                    repository.RepositoryId,
                    WriterId.FromBytes(Enumerable.Repeat((byte)0xA0, 16).ToArray()),
                    repository.CurrentDataGeneration,
                    repository.Keys,
                    repository.Credential,
                    store,
                    new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                    spool,
                    formatVersion)
                .PublishAsync(
                    new SnapshotJob
                    {
                        Source = source,
                        Roots = [new ScanRoot("/")],
                        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                        SnapshotId = Enumerable.Repeat((byte)0x69, 16).ToArray(),
                        NowUnixMilliseconds = 1_722_600_000_001,
                        DeclaredMaxDurationMs = 3_600_000,
                        ExpiryGeneration = 5,
                        ClientVersion = "recovery-junctions/1.0",
                    },
                    CancellationToken.None);
        }

        using var open = Passphrase.Create(PassphraseText);
        using var session = await RecoverySession.OpenAsync(open, store, CancellationToken.None);
        await session.LoadBlobsAsync(CancellationToken.None);
        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));
        return await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, makeLink: null, streams: null, junctions, CancellationToken.None);
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
