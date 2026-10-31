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
/// §4.1 key 13), so a backup says what it did not keep.
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

        var published = await PublishAsync();

        Assert.Contains("file-flags: no-dump", (await ManifestOfAsync(published, "flagged.bin")).CaptureDiagnostics);
        Assert.IsEmpty((await ManifestOfAsync(published, "plain.bin")).CaptureDiagnostics);
    }

    private async Task<PublishedTreeSnapshot> PublishAsync()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();

        return await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
                SpoolDirectory, FormatVersions.RelocatableRecords)
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
