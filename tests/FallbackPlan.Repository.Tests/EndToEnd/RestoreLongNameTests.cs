using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A file whose name is as long as a target holds restores through the
/// executor (FR-RST-005, ADR-0173). Its spool is written beside it under a
/// short name of its own, so the file is not failed for a name longer than
/// its own. The common Linux filesystems hold 255 bytes in one name and NTFS
/// 255 UTF-16 units, so a name of 255 ASCII characters is the longest both
/// hold.
/// </summary>
[TestClass]
public sealed class RestoreLongNameTests : ArchiveTestHarness
{
    [TestMethod]
    [DataRow("n", 251, ".bin", DisplayName = "255 ASCII characters")]
    [DataRow("語", 84, ".bi", DisplayName = "255 UTF-8 bytes, most of them in three-byte characters")]
    public async Task AFileWhoseNameIsAsLongAsATargetHolds_IsRestoredByteForByte(string unit, int count, string extension)
    {
        var name = string.Concat(Enumerable.Repeat(unit, count)) + extension;
        Assert.AreEqual(255, System.Text.Encoding.UTF8.GetByteCount(name));
        var content = Deterministic(4_096, 7);
        var source = new FakeFileSystemSource();
        source.AddFile("data/" + name, content, fileId: 9_101);
        source.AddFile("data/short.bin", Deterministic(512, 8), fileId: 9_102);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue-long.db"), Repo);
        var spool = Directory.CreateDirectory(Path.Combine(SpoolDirectory, "long")).FullName;
        await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                spool,
                FormatVersions.SealedDataPlane, observer: null, catalogue)
            .PublishAsync(
                new SnapshotJob
                {
                    Source = source,
                    Roots = [new ScanRoot("/")],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat((byte)0xD1, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_000,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "restore-long-name-tests/1.0",
                },
                CancellationToken.None);
        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat((byte)0xD1, 16).ToArray(), string.Empty, target);
        Assert.IsEmpty(plan.Conflicts, string.Join("; ", plan.Conflicts.Select(conflict => conflict.Reason)));

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var output = Path.Combine(SpoolDirectory, "long-out");
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "long-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        Assert.AreEqual(
            RestoreOutcome.Complete, receipt.Outcome,
            string.Join("; ", receipt.Items.Where(item => item.Outcome != "restored").Select(item => item.Detail)));
        SequenceAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(output, "data", name)));
        Assert.HasCount(2, Directory.GetFiles(Path.Combine(output, "data")), "no spool is left beside them");
    }

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
