using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A sparse file restores sparse (FR-ARCH-013; specification 09 §4). Its
/// holes stay holes in the file a restore lands: through the engine the CLI's
/// restore verb drives, through the service's executor, and through the
/// standalone recovery tool. They stay holes in the spool the engine holds the
/// file in until its hash verifies, too. Its bytes and its whole-file hash are
/// the captured file's exactly. Where a destination cannot take a hole, it is
/// given the zeroes written out, and the bytes are the same.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AllocatedSize"/> is the oracle, because only allocation tells a
/// hole from written zeroes; the restore these replace wrote every hole out
/// and passed every byte comparison. The file is 98 MiB holding 2 MiB of data,
/// all on 1 MiB boundaries, so no filesystem's allocation unit straddles data
/// and hole. Each of its three holes is 32 MiB, twice the shortest APFS keeps:
/// APFS fills a shorter hole with zeroes when it writes the file back, which
/// <c>Domain.Tests/SparseFileTests</c> pins. Holding allocation under the data
/// plus half the shortest hole separates a restore that left every hole from
/// one that filled even one of them, with room for any filesystem's rounding.
/// </para>
/// <para>
/// A file that is one hole from end to end is here too. Capture used to
/// refuse it, by recording no extent for a file with no data, which leaves a
/// manifest that does not cover its own length.
/// </para>
/// </remarks>
[TestClass]
public sealed class SparseRestoreTests : ArchiveTestHarness
{
    private const int MiB = 1024 * 1024;
    private const long Logical = 98L * MiB;
    private const string PassphraseText = "sparse-restore-drill-passphrase!";

    private static readonly byte[] SnapshotId = Enumerable.Repeat((byte)0x5e, 16).ToArray();

    // In MiB: [0, 32) hole · [32, 33) data · [33, 65) hole · [65, 66) data · [66, 98) hole.
    private static readonly SparseExtent[] Holes =
    [
        new(0, 32 * MiB),
        new(33 * MiB, 32 * MiB),
        new(66 * MiB, 32 * MiB),
    ];

    private string Root => Path.GetDirectoryName(StoreRoot)!;

    [TestMethod]
    public async Task TheEngine_RestoringIntoAFile_LeavesTheHolesUnallocated()
    {
        // The shape of the CLI's restore verb: a file it created, handed to
        // the engine as a stream.
        var content = Layout();
        using var published = await PublishAsync(Holes, content);
        var path = Path.Combine(Root, "engine.img");

        RestoreResult result;
        await using (var destination = File.Create(path))
        {
            result = await new RestoreEngine(published.Reader).RestoreFileAsync(
                published.Manifest, destination, CancellationToken.None);

            // Where a dense copy leaves it, though the file ends in a hole
            // that nothing was written to.
            Assert.AreEqual(Logical, destination.Position, "the destination was not left at the end of the file");
        }

        Assert.IsTrue(result.Success, result.FailureDetail);
        Assert.AreEqual(Logical, result.Length);
        AssertRestoredSparse(path, content, Holes);
    }

    [TestMethod]
    public async Task TheEngine_ItsSpool_HoldsOnlyTheData()
    {
        // The spool is measured while the engine emits from it, which is the
        // last moment it exists.
        var content = Layout();
        using var published = await PublishAsync(Holes, content);
        var spoolDirectory = Directory.CreateDirectory(Path.Combine(Root, "engine-spool")).FullName;
        using var destination = new SpoolMeasuringStream(spoolDirectory);

        var result = await new RestoreEngine(published.Reader, spoolDirectory).RestoreFileAsync(
            published.Manifest, destination, CancellationToken.None);

        Assert.IsTrue(result.Success, result.FailureDetail);
        Assert.IsNotNull(destination.SpoolAllocated, "the engine emitted nothing while its spool existed");
        Assert.IsLessThan(
            AllocationBound(Logical, Holes), destination.SpoolAllocated.Value,
            $"the spool allocated {destination.SpoolAllocated} bytes for a file holding {DataIn(Logical, Holes)} bytes of data");
        AssertSameBytes(content, destination.Written);
        Assert.IsEmpty(Directory.GetFiles(spoolDirectory), "the spool outlived the restore");
    }

    [TestMethod]
    public async Task TheExecutor_RestoringASparseFile_LandsItWithItsHoles()
    {
        var content = Layout();
        using var published = await PublishAsync(Holes, content);
        var output = Path.Combine(Root, "restored");

        var receipt = await new RestoreExecutor(published.Reader, published.Target).ExecuteAsync(
            published.Plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "sparse",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        var item = Assert.ContainsSingle(receipt.Items.Where(candidate => candidate.Path == "disk.img"));
        Assert.AreEqual((ulong)Logical, item.Bytes);
        AssertRestoredSparse(Path.Combine(output, "disk.img"), content, Holes);
    }

    [TestMethod]
    public async Task TheRecoveryTool_RestoringASparseFile_LandsItWithItsHoles()
    {
        // The tool opens from the passphrase and the store alone, so the
        // repository is one a passphrase made.
        var content = Layout();
        var store = new LocalFileSystemObjectStore(Path.Combine(Root, "recovery-repo"));
        using (var passphrase = Passphrase.Create(PassphraseText))
        {
            var (repository, authority) = await RepositoryLifecycle.CreateFromPassphraseAsync(
                store, passphrase, RepositoryCreationSettings.Default,
                createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None);
            using var _repository = repository;
            using var _authority = authority;

            var spool = Directory.CreateDirectory(Path.Combine(Root, "recovery-spool")).FullName;
            await new PublicationOrchestrator(
                    SmallBlobPolicy, repository.RepositoryId,
                    WriterId.FromBytes(Enumerable.Repeat((byte)0xA5, 16).ToArray()),
                    repository.CurrentDataGeneration, repository.Keys, repository.Credential, store,
                    new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                    spool, FormatVersions.SealedDataPlane)
                .PublishAsync(Job(SparseSource(Holes, content)), CancellationToken.None);
        }

        using var openPassphrase = Passphrase.Create(PassphraseText);
        using var session = await RecoverySession.OpenAsync(openPassphrase, store, CancellationToken.None);
        await session.LoadBlobsAsync(CancellationToken.None);
        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));
        var output = Path.Combine(Root, "recovered");

        var report = await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, CancellationToken.None);

        Assert.AreEqual(1, report.Restored, string.Join(" | ", report.Notes));
        Assert.AreEqual(0, report.Failed, string.Join(" | ", report.Notes));
        AssertRestoredSparse(Path.Combine(output, "disk.img"), content, Holes);
    }

    [TestMethod]
    public async Task AFileThatIsOneHole_BacksUpAsOneExtent_AndRestoresAsAHole()
    {
        // The shape a freshly created disk image has: a length, and no data.
        var empty = new byte[Logical];
        SparseExtent[] wholeFile = [new(0, (ulong)Logical)];
        using var published = await PublishAsync(wholeFile, empty);

        Assert.IsEmpty(published.Manifest.SegmentReferences);
        var hole = Assert.ContainsSingle(published.Manifest.SparseExtents);
        Assert.AreEqual(wholeFile[0], hole);

        var path = Path.Combine(Root, "blank.img");
        RestoreResult result;
        await using (var destination = File.Create(path))
        {
            result = await new RestoreEngine(published.Reader).RestoreFileAsync(
                published.Manifest, destination, CancellationToken.None);
        }

        Assert.IsTrue(result.Success, result.FailureDetail);
        AssertRestoredSparse(path, empty, wholeFile);
    }

    [TestMethod]
    public async Task TheEngine_ADestinationThatCannotSeek_GetsTheHolesWrittenAsZeroes()
    {
        var content = Layout();
        using var published = await PublishAsync(Holes, content);
        using var destination = new ForwardOnlyStream();

        var result = await new RestoreEngine(published.Reader).RestoreFileAsync(
            published.Manifest, destination, CancellationToken.None);

        Assert.IsTrue(result.Success, result.FailureDetail);
        AssertSameBytes(content, destination.Written);
    }

    [TestMethod]
    public async Task TheEngine_ADestinationAlreadyHoldingBytesWhereTheFileGoes_HasTheHolesOverwrittenWithZeroes()
    {
        // A skipped range keeps whatever it held, so a hole may be left only
        // where nothing is: here, every hole would otherwise read 0xEE.
        var content = Layout();
        using var published = await PublishAsync(Holes, content);
        var held = new byte[Logical];
        held.AsSpan().Fill(0xEE);
        using var destination = new MemoryStream(held);

        var result = await new RestoreEngine(published.Reader).RestoreFileAsync(
            published.Manifest, destination, CancellationToken.None);

        Assert.IsTrue(result.Success, result.FailureDetail);
        AssertSameBytes(content, held);
    }

    private static void AssertRestoredSparse(string path, byte[] expected, SparseExtent[] holes)
    {
        using (var restored = File.OpenRead(path))
        {
            AssertSameBytes(expected, restored);
        }

        var allocated = AllocatedSize.Of(path);
        Assert.IsLessThan(
            AllocationBound(expected.LongLength, holes), allocated,
            $"{allocated} bytes allocated for a {expected.LongLength}-byte file holding {DataIn(expected.LongLength, holes)} bytes of data");
    }

    /// <summary>
    /// What a file of <paramref name="length"/> allocates at most once every
    /// one of its <paramref name="holes"/> was left: its data, and half its
    /// shortest hole for rounding. A single hole filled puts it over.
    /// </summary>
    private static long AllocationBound(long length, SparseExtent[] holes) =>
        DataIn(length, holes) + (long)holes.Min(hole => hole.Length) / 2;

    private static long DataIn(long length, SparseExtent[] holes) =>
        length - holes.Sum(hole => (long)hole.Length);

    private static void AssertSameBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        Assert.AreEqual(expected.Length, actual.Length, "the restored length");
        var same = expected.CommonPrefixLength(actual);
        Assert.AreEqual(expected.Length, same, $"the restored bytes first differ at offset {same}");
    }

    private static void AssertSameBytes(byte[] expected, Stream actual)
    {
        Assert.AreEqual(expected.LongLength, actual.Length, "the restored length");

        var buffer = new byte[MiB];
        var offset = 0;
        while (offset < expected.Length)
        {
            var read = actual.Read(buffer);
            Assert.IsGreaterThan(0, read, $"the restored file ended at offset {offset}");
            var same = expected.AsSpan(offset, read).CommonPrefixLength(buffer.AsSpan(0, read));
            Assert.AreEqual(read, same, $"the restored bytes first differ at offset {offset + same}");
            offset += read;
        }
    }

    private static byte[] Layout()
    {
        var content = new byte[Logical];
        new Random(7).NextBytes(content.AsSpan(32 * MiB, MiB));
        new Random(11).NextBytes(content.AsSpan(65 * MiB, MiB));
        return content;
    }

    private static FakeFileSystemSource SparseSource(IReadOnlyList<SparseExtent> holes, byte[] content)
    {
        var source = new FakeFileSystemSource();
        var node = source.AddFile("disk.img", content);
        source.AddNode(node with { SparseExtents = holes });
        return source;
    }

    private static SnapshotJob Job(FakeFileSystemSource source) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = SnapshotId,
        NowUnixMilliseconds = 1_722_600_000_001,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "sparse-restore-tests/1.0",
    };

    private async Task<Published> PublishAsync(IReadOnlyList<SparseExtent> holes, byte[] content)
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue.db"), Repo);

        var snapshot = await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
                SpoolDirectory, FormatVersions.SealedDataPlane, observer: null, catalogue)
            .PublishAsync(Job(SparseSource(holes, content)), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, SnapshotId, string.Empty, target);

        var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var read = await reader.ReadSegmentAsync(Assert.ContainsSingle(snapshot.Files).ObjectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        return new Published(keys, reader, FileVersionManifestCodec.Decode(read.Plaintext!), plan, target);
    }

    private sealed record Published(
        RepositoryKeySet Keys, RepositoryReader Reader, FileVersionManifest Manifest, RestorePlan Plan, RestoreTargetProfile Target)
        : IDisposable
    {
        public void Dispose()
        {
            Reader.Dispose();
            Keys.Dispose();
        }
    }

    /// <summary>
    /// A destination that, on the engine's first write to it, measures the
    /// one spool in <paramref name="spoolDirectory"/>.
    /// </summary>
    private sealed class SpoolMeasuringStream(string spoolDirectory) : MemoryStream
    {
        public long? SpoolAllocated { get; private set; }

        public ReadOnlySpan<byte> Written => GetBuffer().AsSpan(0, checked((int)Length));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Measure();
            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Measure();
            base.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Measure();
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Measure();
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        private void Measure() =>
            SpoolAllocated ??= AllocatedSize.Of(Assert.ContainsSingle(Directory.GetFiles(spoolDirectory)));
    }

    /// <summary>A destination that can only be written forwards, as a pipe can.</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _written = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public ReadOnlySpan<byte> Written => _written.GetBuffer().AsSpan(0, checked((int)_written.Length));

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => _written.Write(buffer);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _written.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
