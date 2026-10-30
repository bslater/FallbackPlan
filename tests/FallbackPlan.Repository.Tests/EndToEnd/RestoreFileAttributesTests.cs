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
/// restore replaces is replaced.
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
            foreach (var receipt in new[] { first, again })
            {
                Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
                Assert.IsTrue(receipt.Items.All(item => !(item.NotApplied?.Contains("file_attributes") ?? false)));
            }
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
