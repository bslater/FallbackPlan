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
/// A real file's flags that no format key keeps are named in its published
/// manifest's capture diagnostics (FR-MAN-003; ADR-0180; specification 06
/// §4.1 key 13), so a backup says what it did not keep. From format 6 the
/// manifest keeps them in metadata key 11 and names them nowhere, and a
/// folder's tree keeps a folder's alike (ADR-0180 Amendment 3).
/// </summary>
[TestClass]
public sealed class FileFlagsCaptureTests : ArchiveTestHarness
{
    private readonly string _sourceRoot =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-file-flags-capture-tests", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux keeps a file's inode flags")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task OnLinux_AFilesNoDumpFlag_IsNamedInItsManifest_AndAPlainFilesManifestNamesNone()
    {
        var flagged = Path.Combine(_sourceRoot, "flagged.bin");
        File.WriteAllBytes(flagged, [1, 2, 3]);
        DiskFlags.AddOnLinux(flagged, DiskFlags.LinuxNoDump);
        File.WriteAllBytes(Path.Combine(_sourceRoot, "plain.bin"), [4, 5, 6]);

        var published = await PublishAsync(FormatVersions.RelocatableRecords);

        Assert.Contains("file-flags: no-dump", (await ManifestOfAsync(published, "flagged.bin")).CaptureDiagnostics);
        Assert.IsEmpty((await ManifestOfAsync(published, "plain.bin")).CaptureDiagnostics);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux keeps a file's and a folder's inode flags")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task OnLinux_AtFormatSix_AFilesAndAFoldersNoDumpFlag_AreKeptInKeyEleven_AndNamedNowhere()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_sourceRoot, "cache")).FullName;
        var flagged = Path.Combine(folder, "flagged.bin");
        File.WriteAllBytes(flagged, [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_sourceRoot, "plain.bin"), [4, 5, 6]);

        // Set last: ext4 gives a new file the no-dump flag of its folder.
        DiskFlags.AddOnLinux(flagged, DiskFlags.LinuxNoDump);
        DiskFlags.AddOnLinux(folder, DiskFlags.LinuxNoDump);

        var published = await PublishAsync(FormatVersions.FileFlagsAndLinkForms);

        var kept = await ManifestOfAsync(published, "cache/flagged.bin");
        Assert.AreEqual(FileFlagBits.NoDump, kept.Metadata.FileFlags);
        Assert.IsEmpty(kept.CaptureDiagnostics);

        var plain = await ManifestOfAsync(published, "plain.bin");
        Assert.AreEqual(FileFlagBits.None, plain.Metadata.FileFlags);
        Assert.IsEmpty(plain.CaptureDiagnostics);

        var tree = await FolderOfAsync(published, "cache");
        Assert.IsNotNull(tree.Metadata);
        Assert.AreEqual(FileFlagBits.NoDump, tree.Metadata.FileFlags);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux keeps a file's and a folder's inode flags")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task OnLinux_AtFormatFive_AFilesFlagIsNamed_AndNeitherAFilesNorAFoldersIsKept()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_sourceRoot, "cache")).FullName;
        var flagged = Path.Combine(folder, "flagged.bin");
        File.WriteAllBytes(flagged, [1, 2, 3]);
        DiskFlags.AddOnLinux(flagged, DiskFlags.LinuxNoDump);
        DiskFlags.AddOnLinux(folder, DiskFlags.LinuxNoDump);

        var published = await PublishAsync(FormatVersions.DeltaCheckpoints);

        // A reader of format 5 refuses key 11, so a format-5 repository
        // never holds it.
        var named = await ManifestOfAsync(published, "cache/flagged.bin");
        Assert.AreEqual(FileFlagBits.None, named.Metadata.FileFlags);
        Assert.Contains("file-flags: no-dump", named.CaptureDiagnostics);

        var tree = await FolderOfAsync(published, "cache");
        Assert.AreEqual(FileFlagBits.None, tree.Metadata?.FileFlags ?? FileFlagBits.None);
    }

    private async Task<PublishedTreeSnapshot> PublishAsync(ushort formatVersion)
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
                    Source = new LocalFileSystemSource(),
                    Roots = [new ScanRoot(_sourceRoot)],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat((byte)0x44, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_000,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "file-flags-capture-tests/1.0",
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

    private async Task<TreeManifest> FolderOfAsync(PublishedTreeSnapshot published, string name)
    {
        using var keys = CreateKeys();
        using var reader = new RepositoryReader(Repo, keys, CreateStore(), Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var root = await reader.ReadSegmentAsync(published.RootTreeObjectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, root.Outcome);
        var entry = TreeManifestCodec.Decode(root.Plaintext!).Entries
            .Single(candidate => System.Text.Encoding.UTF8.GetString(candidate.Name.Span) == name);
        Assert.AreEqual(EntryKind.DirectoryPlaceholder, entry.EntryKind);

        var folder = await reader.ReadSegmentAsync(entry.ObjectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, folder.Outcome);
        return TreeManifestCodec.Decode(folder.Plaintext!);
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
