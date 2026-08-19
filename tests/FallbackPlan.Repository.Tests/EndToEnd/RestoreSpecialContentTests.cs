using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Names and shapes the ASCII-only restore suites never touched: every
/// restored path in every earlier test was plain ASCII, and no test had
/// ever asked whether an empty leaf directory comes back. Both run against
/// the REAL <see cref="LocalFileSystemSource"/> and the real executor to
/// real disk, because filename encoding is exactly where a fake proves
/// nothing.
/// </summary>
[TestClass]
public sealed class RestoreSpecialContentTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private readonly string _sourceRoot =
        Path.Combine(Path.GetTempPath(), "fbp-special-content-tests", Guid.NewGuid().ToString("n"));

    [TestMethod]
    public async Task RestoreExecution_UnicodeAndSpacedNames_RoundTripByteIdentically()
    {
        // NFC-composed names with accents, a space-and-parenthesis name of
        // the shape the beside policy itself generates, and a non-Latin
        // directory — captured from disk, restored to disk, compared.
        var random = new Random(11);
        var files = new Dictionary<string, byte[]>
        {
            ["naïve café.txt"] = new byte[2_000],
            ["résumé (final).docx"] = new byte[4_000],
            ["snow ☃ folder/deep file.bin"] = new byte[70_000],
        };
        foreach (var (path, content) in files)
        {
            random.NextBytes(content);
            var full = Path.Combine(_sourceRoot, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
        }

        var (catalogue, store, keys) = await PublishAsync("unicode", 0xB1);
        using var _ = keys;
        using var db = catalogue;

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(db, Enumerable.Repeat((byte)0xB1, 16).ToArray(), string.Empty, target);
        Assert.IsEmpty(plan.Conflicts);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var output = Path.Combine(SpoolDirectory, "unicode-out");
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "unicode-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        foreach (var (path, content) in files)
        {
            var landed = Path.Combine(output, path.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(landed), $"'{path}' did not come back under its own name");
            SequenceAssert.AreEqual(content, await File.ReadAllBytesAsync(landed));
        }
    }

    [TestMethod]
    public async Task RestoreExecution_AnEmptyLeafDirectory_IsMaterialised()
    {
        Directory.CreateDirectory(Path.Combine(_sourceRoot, "kept", "empty-inside"));
        File.WriteAllBytes(Path.Combine(_sourceRoot, "kept", "anchor.txt"), "anchor"u8.ToArray());
        Directory.CreateDirectory(Path.Combine(_sourceRoot, "hollow"));

        var (catalogue, store, keys) = await PublishAsync("emptydir", 0xB2);
        using var _ = keys;
        using var db = catalogue;

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(db, Enumerable.Repeat((byte)0xB2, 16).ToArray(), string.Empty, target);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var output = Path.Combine(SpoolDirectory, "emptydir-out");
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "emptydir-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        // A directory with nothing in it is still part of the tree the user
        // captured: both the empty leaf beside a file and the empty root
        // child come back as directories, accounted for in the receipt.
        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.IsTrue(Directory.Exists(Path.Combine(output, "kept", "empty-inside")), "the empty leaf directory is missing");
        Assert.IsTrue(Directory.Exists(Path.Combine(output, "hollow")), "the empty root-level directory is missing");
        Assert.AreEqual(
            "restored",
            Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "kept/empty-inside")).Outcome);
    }

    private async Task<(CatalogueDb Catalogue, Storage.Local.LocalFileSystemObjectStore Store, RepositoryKeySet Keys)>
        PublishAsync(string name, byte seed)
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"catalogue-{name}.db"), Repo);

        var spool = Path.Combine(SpoolDirectory, name);
        Directory.CreateDirectory(spool);
        var orchestrator = new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool, observer: null, catalogue);

        var published = await orchestrator.PublishAsync(
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
                ClientVersion = "restore-special-content-tests/1.0",
            },
            CancellationToken.None);
        Assert.IsEmpty(published.Failures);

        return (catalogue, store, keys);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(_sourceRoot))
        {
            Directory.Delete(_sourceRoot, recursive: true);
        }

        base.Dispose(disposing);
    }
}
