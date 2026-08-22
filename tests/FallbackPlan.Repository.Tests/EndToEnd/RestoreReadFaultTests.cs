using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The read-fault slice of the restore interruption matrix (architecture
/// 08 §3; NFR-REL-005): a transient store fault arrives mid-restore, after
/// the plan was made and the blobs were loaded. The promise under test is
/// the same one the write path holds against a failed put — the failure is
/// contained to the item it hit, accounted for in the receipt, and gone on
/// the rerun: no partial file left visible as complete, no receipt-less
/// abort, no operator action to recover.
/// </summary>
[TestClass]
public sealed class RestoreReadFaultTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    [TestMethod]
    public async Task Restore_AReadFailsMidRun_FailsThatItemInTheReceiptAndTheRerunCompletes()
    {
        var source = new FakeFileSystemSource();
        var alpha = Deterministic(4_096, 3);
        var beta = Deterministic(4_096, 7);
        source.AddFile("data/alpha.bin", alpha);
        source.AddFile("data/beta.bin", beta);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();
        await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xE1), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat((byte)0xE1, 16).ToArray(), string.Empty, target);

        // Load the world through a store whose reads will start failing only
        // once the restore is under way — the transient fault, not a store
        // that was broken from the start.
        var faulting = new ReadFaultingObjectStore(store);
        using var reader = new RepositoryReader(Repo, keys, faulting);
        await reader.LoadBlobsAsync(CancellationToken.None);
        faulting.Arm(key => key.StartsWith("blobs/", StringComparison.Ordinal));

        var output = Path.Combine(SpoolDirectory, "read-fault-out");
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions { RunId = "faulted", NowUnixMilliseconds = 1_722_700_000_000 },
            CancellationToken.None);

        // The fault is contained: a receipt exists, the items it hit are
        // failed rather than absent, and nothing half-written is visible as
        // a completed file.
        Assert.AreEqual(RestoreOutcome.Failed, receipt.Outcome);
        Assert.AreEqual(plan.Items.Count, receipt.Items.Count,
            "every planned item is accounted for even when reads fail");
        foreach (var failed in receipt.Items.Where(item => item.Outcome == "failed"))
        {
            Assert.IsFalse(File.Exists(Path.Combine(receipt.WrittenTo, failed.Path)),
                $"'{failed.Path}' failed but a file is present at its destination");
        }

        // The fault clears; the rerun completes without repair or operator
        // action, and the bytes are right.
        faulting.Heal();
        using var freshReader = new RepositoryReader(Repo, keys, store);
        await freshReader.LoadBlobsAsync(CancellationToken.None);

        var rerun = await new RestoreExecutor(freshReader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions { RunId = "healed", NowUnixMilliseconds = 1_722_700_001_000 },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, rerun.Outcome);
        SequenceAssert.AreEqual(alpha, await File.ReadAllBytesAsync(Path.Combine(rerun.WrittenTo, "data", "alpha.bin")));
        SequenceAssert.AreEqual(beta, await File.ReadAllBytesAsync(Path.Combine(rerun.WrittenTo, "data", "beta.bin")));
    }

    private CatalogueDb OpenCatalogue() =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, "read-fault.db"), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, KeyHierarchy hierarchy, CatalogueDb catalogue) =>
        new(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "read-fault-sequence.txt"))),
            SpoolDirectory, observer: null, catalogue);

    private static SnapshotJob Job(FakeFileSystemSource source, byte seed) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(seed, 16).ToArray(),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "read-fault-tests/1.0",
    };

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }
}
