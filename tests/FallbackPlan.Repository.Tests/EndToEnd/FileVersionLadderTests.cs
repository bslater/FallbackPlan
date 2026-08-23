using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// One file, three versions, every version restored and compared byte for
/// byte (FR-SNP-002, FR-RST-004).
/// </summary>
/// <remarks>
/// <para>
/// The suite had size breadth in one snapshot and reuse arithmetic across two,
/// but nothing that took a multi-blob file through several versions and then
/// asked for each of them back. That is the shape a person actually has: a
/// file edited near its end, again and again, over months — and the question
/// they ask is not "did the newest survive" but "give me the one from before
/// the mistake".
/// </para>
/// <para>
/// The base content is the harness's mixed compressible/incompressible file
/// rather than a run of zeroes, deliberately. Under fixed 64 KiB segmentation
/// three mebibytes of zeroes is forty-eight <em>identical</em> segments, which
/// deduplicate to a single record — the file would span one blob and the test
/// would prove nothing about reassembling across many. Distinct bytes make
/// "spans multiple blobs" true rather than merely intended; what the versions
/// vary is what the user varies, the tail and the length.
/// </para>
/// </remarks>
[TestClass]
public sealed class FileVersionLadderTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private const string FilePath = "ledger/book.bin";

    /// <summary>How much of the tail each version rewrites, and how much it then adds.</summary>
    private const int Rewritten = 32 * 1024;

    private const int Appended = 64 * 1024;

    [TestMethod]
    public async Task EveryVersionOfAMultiBlobFile_RestoresByteIdentical_LongAfterItWasReplaced()
    {
        var versions = Ladder();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = CatalogueDb.Open(
            Path.Combine(SpoolDirectory, "ladder.db"), Repo);

        var source = new FakeFileSystemSource();
        var node = source.AddFile(FilePath, versions[0], fileId: 4_242);

        var snapshotIds = new List<byte[]>();
        byte[]? prior = null;
        for (var version = 0; version < versions.Count; version++)
        {
            if (version > 0)
            {
                node.Content = versions[version];

                // Without a fresh mtime the incremental short-circuit is
                // entitled to skip the file on identity, size and time — and
                // the size does change here, but leaning on that would make
                // the test depend on which half of the short-circuit fired.
                node.Metadata = node.Metadata with { ModifiedAt = 1_722_700_000_000 + (ulong)version };
            }

            var snapshotId = Enumerable.Repeat((byte)(0xE0 + version), 16).ToArray();
            await CreateOrchestrator(store, keys, hierarchy, catalogue, version).PublishAsync(
                Job(source, snapshotId, 1_722_600_000_000 + ((ulong)version * 1000)) with
                {
                    PriorSnapshotId = prior,
                },
                CancellationToken.None);

            snapshotIds.Add(snapshotId);
            prior = snapshotId;
        }

        // The point of the whole exercise: each snapshot, restored on its own,
        // lands exactly the bytes that file held when that snapshot was taken.
        // A version manifest that named the wrong tail segment, or a
        // reassembly that stopped at the previous length, shows up here as a
        // byte mismatch rather than as a missing file.
        var target = RestoreTargetProfile.ForLocalPlatform();
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        for (var version = 0; version < versions.Count; version++)
        {
            var plan = RestorePlanner.Plan(catalogue, snapshotIds[version], string.Empty, target);
            var output = Path.Combine(SpoolDirectory, $"out-{version}");

            var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
                plan, output,
                new RestoreExecutionOptions
                {
                    DestinationMode = RestoreDestinationMode.InPlace,
                    RunId = $"ladder-{version}",
                    NowUnixMilliseconds = 1_722_800_000_000,
                },
                CancellationToken.None);

            Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome, $"version {version} did not restore");

            var landed = Path.Combine(
                output, FilePath.Replace('/', Path.DirectorySeparatorChar));
            SequenceAssert.AreEqual(versions[version], await File.ReadAllBytesAsync(landed));
        }
    }

    [TestMethod]
    public async Task ALaterVersion_ReusesTheUnchangedPrefix_AndStillRestoresWhole()
    {
        // The other half of the guarantee: keeping every version must not mean
        // storing every version whole. The prefix is untouched between the
        // rungs, so the second publication writes records for the rewritten
        // tail and the appended bytes and nothing else — and the version it
        // describes still comes back complete, which is what makes the saving
        // safe rather than merely cheap.
        var versions = Ladder();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = CatalogueDb.Open(
            Path.Combine(SpoolDirectory, "reuse.db"), Repo);

        var source = new FakeFileSystemSource();
        var node = source.AddFile(FilePath, versions[0], fileId: 4_243);

        var first = await CreateOrchestrator(store, keys, hierarchy, catalogue, 0).PublishAsync(
            Job(source, [.. Enumerable.Repeat((byte)0xF0, 16)], 1_722_600_000_000),
            CancellationToken.None);

        node.Content = versions[1];
        node.Metadata = node.Metadata with { ModifiedAt = 1_722_700_000_000 };

        var second = await CreateOrchestrator(store, keys, hierarchy, catalogue, 1).PublishAsync(
            Job(source, [.. Enumerable.Repeat((byte)0xF1, 16)], 1_722_600_001_000) with
            {
                PriorSnapshotId = [.. Enumerable.Repeat((byte)0xF0, 16)],
            },
            CancellationToken.None);

        var firstFile = Assert.ContainsSingle(first.Files);
        var secondFile = Assert.ContainsSingle(second.Files);

        // The file grew, so it carries more segments than it did.
        Assert.IsGreaterThan(
            firstFile.Archive!.SegmentReferences.Count, secondFile.Archive!.SegmentReferences.Count);

        // But the bytes newly stored are a small fraction of it: only the
        // segments the index could not already locate.
        var written = second.ContentBlobs.Sum(blob => blob.RecordCount);
        Assert.IsLessThan(
            secondFile.Archive.SegmentReferences.Count / 2,
            written,
            "the unchanged prefix should have been located, not re-stored");

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        using var restored = new MemoryStream();
        var outcome = await reader.RestoreAsync(
            secondFile.Archive.SegmentReferences, restored, CancellationToken.None);

        Assert.IsTrue(outcome.Success, outcome.FailureDetail);
        SequenceAssert.AreEqual(versions[1], restored.ToArray());
    }

    /// <summary>
    /// Three versions of one file. Each rewrites the last <see cref="Rewritten"/>
    /// bytes and then adds <see cref="Appended"/> more, so both the tail and
    /// the length differ every time while the prefix stays put.
    /// </summary>
    private static List<byte[]> Ladder()
    {
        var versions = new List<byte[]> { BuildTestFile() };
        for (var version = 1; version <= 2; version++)
        {
            var previous = versions[^1];
            var mark = (byte)(0x10 * version);
            versions.Add(
            [
                .. previous[..^Rewritten],
                .. Enumerable.Repeat(mark, Rewritten + Appended),
            ]);
        }

        return versions;
    }

    private PublicationOrchestrator CreateOrchestrator(
        Storage.Local.LocalFileSystemObjectStore store,
        RepositoryKeySet keys,
        KeyHierarchy hierarchy,
        CatalogueDb catalogue,
        int version)
    {
        var spool = Path.Combine(SpoolDirectory, $"publish-{version}");
        Directory.CreateDirectory(spool);
        return new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(
                Path.Combine(SpoolDirectory, "sequence.txt"))),
            spool, observer: null, catalogue);
    }

    private static SnapshotJob Job(FakeFileSystemSource source, byte[] snapshotId, ulong now) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = [.. Enumerable.Repeat((byte)0x22, 16)],
        BackupSetId = [.. Enumerable.Repeat((byte)0x33, 16)],
        SnapshotId = snapshotId,
        NowUnixMilliseconds = now,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "file-version-ladder-tests/1.0",
    };
}
