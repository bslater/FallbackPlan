using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A restore gives a file and a folder their Windows attribute bits back
/// where the target keeps them (FR-RST-004; ADR-0175): read-only, hidden,
/// system, archive and not-content-indexed, after every other write, and the
/// plan and the receipt no longer list them there. A read-only file a later
/// restore replaces is replaced. A file and a folder captured compressed come
/// back compressed where the target keeps compression, and one captured
/// uncompressed comes back uncompressed (ADR-0175 Amendment 2).
/// </summary>
[TestClass]
public sealed class RestoreFileAttributesTests : ArchiveTestHarness
{
    private readonly string _sourceRoot =
        Path.Combine(Path.GetTempPath(), "fbp-file-attributes-restore-tests", Guid.NewGuid().ToString("n"));

    [TestMethod]
    public void Profile_ForThisPlatform_WritesAttributeBitsOnlyOnWindows()
    {
        var target = RestoreTargetProfile.ForLocalPlatform();

        Assert.AreEqual(OperatingSystem.IsWindows(), target.SupportsFileAttributes);
        Assert.IsTrue((target with { SupportsFileAttributes = true }).Metadata.SupportsFileAttributes);
        Assert.IsFalse((target with { SupportsFileAttributes = false }).Metadata.SupportsFileAttributes);
        foreach (var kind in new[] { EntryKind.File, EntryKind.DirectoryPlaceholder })
        {
            Assert.IsTrue(RestoreMetadata.Applied(kind, target with { SupportsFileAttributes = true }).HasFlag(CapturedMetadata.FileAttributes));
            Assert.IsFalse(RestoreMetadata.Applied(kind, target with { SupportsFileAttributes = false }).HasFlag(CapturedMetadata.FileAttributes));
        }
    }

    [TestMethod]
    public void Profile_ForThisPlatform_KeepsCompressionOnlyOnWindows_AndPassesItToTheWriter()
    {
        var target = RestoreTargetProfile.ForLocalPlatform();

        Assert.AreEqual(OperatingSystem.IsWindows(), target.SupportsCompression);
        Assert.IsTrue((target with { SupportsCompression = true }).Metadata.SupportsCompression);
        Assert.IsFalse((target with { SupportsCompression = false }).Metadata.SupportsCompression);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's compression")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task OnWindows_AFileAndAFolderCapturedCompressed_ComeBackCompressed_AndOneCapturedUncompressedDoesNot()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_sourceRoot, "packed")).FullName;
        Assert.IsNull(FileCompression.OfThisPlatform.Apply(folder, compressed: true));
        File.WriteAllText(Path.Combine(folder, "inside.bin"), new string('a', 64 * 1024));
        File.WriteAllText(Path.Combine(_sourceRoot, "loose.bin"), new string('b', 64 * 1024));

        var (store, keys, catalogue) = await PublishAsync(0xD2);
        using var _ = keys;
        using var db = catalogue;
        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(db, Enumerable.Repeat((byte)0xD2, 16).ToArray(), string.Empty, target);
        Assert.DoesNotContain(degradation => degradation.Capability is "file-attributes" or "unset-file-attributes", plan.Degradations);

        var output = Path.Combine(SpoolDirectory, "compressed-out");
        var receipt = await RestoreAsync(store, keys, plan, target, output, ExistingDestinationPolicy.Preserve);

        Assert.IsTrue(File.GetAttributes(Path.Combine(output, "packed")).HasFlag(FileAttributes.Compressed));
        Assert.IsTrue(File.GetAttributes(Path.Combine(output, "packed", "inside.bin")).HasFlag(FileAttributes.Compressed));
        Assert.IsFalse(File.GetAttributes(Path.Combine(output, "loose.bin")).HasFlag(FileAttributes.Compressed));
        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.IsTrue(receipt.Items.All(item => !(item.NotApplied?.Contains("file_attributes") ?? false)));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's attribute bits")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task OnWindows_AFileAndItsFolder_GetTheirBitsBack_AndAReadOnlyFileIsReplacedByALaterRestore()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_sourceRoot, "data")).FullName;
        var file = Path.Combine(folder, "flags.bin");
        File.WriteAllText(file, "kept read-only and hidden");
        var modified = new DateTime(2024, 7, 26, 9, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, modified);
        File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden);
        File.SetAttributes(folder, FileAttributes.Directory | FileAttributes.Hidden);

        try
        {
            var (store, keys, catalogue) = await PublishAsync(0xD1);
            using var _ = keys;
            using var db = catalogue;
            var target = RestoreTargetProfile.ForLocalPlatform();
            var plan = RestorePlanner.Plan(db, Enumerable.Repeat((byte)0xD1, 16).ToArray(), string.Empty, target);
            Assert.DoesNotContain(degradation => degradation.Capability == "file-attributes", plan.Degradations);

            var output = Path.Combine(SpoolDirectory, "attributes-out");
            var first = await RestoreAsync(store, keys, plan, target, output, ExistingDestinationPolicy.Preserve);
            var again = await RestoreAsync(store, keys, plan, target, output, ExistingDestinationPolicy.Replace);

            var landed = Path.Combine(output, "data", "flags.bin");
            Assert.AreEqual(FileAttributes.ReadOnly | FileAttributes.Hidden, File.GetAttributes(landed));
            Assert.AreEqual(FileAttributes.Directory | FileAttributes.Hidden, File.GetAttributes(Path.Combine(output, "data")));
            Assert.AreEqual(modified, File.GetLastWriteTimeUtc(landed));
            Assert.AreEqual("kept read-only and hidden", File.ReadAllText(landed));
            Assert.AreEqual(RestoreOutcome.Complete, first.Outcome);
            Assert.AreEqual(RestoreOutcome.Complete, again.Outcome);
            Assert.IsTrue(first.Items.All(item => !(item.NotApplied?.Contains("file_attributes") ?? false)));

            // By the second run the folder stands, and keeps its own (ADR-0086).
            var replaced = Assert.ContainsSingle(again.Items.Where(item => item.Path.EndsWith("flags.bin", StringComparison.Ordinal)));
            Assert.DoesNotContain("file_attributes", replaced.NotApplied ?? []);
        }
        finally
        {
            ClearBits(_sourceRoot);
            ClearBits(SpoolDirectory);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(_sourceRoot))
        {
            ClearBits(_sourceRoot);
            Directory.Delete(_sourceRoot, recursive: true);
        }

        base.Dispose(disposing);
    }

    /// <summary>Clears the bits that keep Windows from deleting what a case wrote.</summary>
    private static void ClearBits(string root)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
        }
    }

    private static async Task<RestoreReceipt> RestoreAsync(
        LocalFileSystemObjectStore store,
        RepositoryKeySet keys,
        RestorePlan plan,
        RestoreTargetProfile target,
        string output,
        ExistingDestinationPolicy existing)
    {
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        return await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                ExistingDestination = existing,
                RunId = $"attributes-{existing}".ToLowerInvariant(),
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);
    }

    private async Task<(LocalFileSystemObjectStore Store, RepositoryKeySet Keys, CatalogueDb Catalogue)> PublishAsync(byte seed)
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"attributes-{seed:x2}.db"), Repo);
        var spool = Path.Combine(SpoolDirectory, $"spool-{seed:x2}");
        Directory.CreateDirectory(spool);

        var result = await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                spool, FormatVersions.SealedDataPlane, observer: null, catalogue)
            .PublishAsync(
                new SnapshotJob
                {
                    Source = new LocalFileSystemSource(),
                    Roots = [new ScanRoot(_sourceRoot)],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat(seed, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_000,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "restore-file-attributes-tests/1.0",
                },
                CancellationToken.None);
        Assert.IsEmpty(result.Failures);
        return (store, keys, catalogue);
    }
}
