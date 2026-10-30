using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The recovery tool writes back the alternate data streams captured with a
/// file it restores, by the executor's rule (FR-RST-004, FR-RST-005;
/// ADR-0174 Amendment 1): onto the file once its content has landed and
/// before its times, each read and verified like a segment, and each name
/// judged before it is joined to the file's path. A stream that does not land
/// is noted, saying why, and counted not applied; the file still restores.
/// </summary>
/// <remarks>
/// Only Windows keeps alternate streams, and off Windows the tool asks the
/// platform for none, counting them not applied as before. The cases that
/// judge what it asks for run everywhere against calls that hold streams in
/// memory; one runs the platform's calls end to end on Windows.
/// </remarks>
[TestClass]
public sealed class RecoveryAlternateStreamsTests : IDisposable
{
    private const string PassphraseText = "recovery-streams-passphrase!!";
    private const string CarrierPath = "data/streams.bin";
    private const ulong CapturedModifiedAt = 1_614_834_367_890;

    private static readonly byte[] Zone = "[ZoneTransfer]\nZoneId=3"u8.ToArray();
    private static readonly byte[] Notes = "kept beside the file"u8.ToArray();

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-recovery-streams-tests", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public async Task RecoveryRestore_AFilesStreams_AreWrittenBackOntoIt_AndNotCountedNotApplied()
    {
        var content = Deterministic(4_096, 1);
        var held = new HeldStreams();

        var (report, output) = await RestoreAsync(Carrier(content, ("Zone.Identifier", Zone), ("notes", Notes)), held);

        var landed = Path.Combine(output, "data", "streams.bin");
        SequenceAssert.AreEqual(content, File.ReadAllBytes(landed));
        SequenceAssert.AreEqual(Zone, held.Streams[(landed, "Zone.Identifier")]);
        SequenceAssert.AreEqual(Notes, held.Streams[(landed, "notes")]);
        Assert.HasCount(2, held.Streams);
        Assert.AreEqual(1, report.Restored);
        Assert.AreEqual(0, NotApplied(report));
        Assert.DoesNotContain(note => note.Kind == RecoveryNoteKind.StreamNotWritten, report.Notes);
    }

    [TestMethod]
    public async Task RecoveryRestore_AStreamWrittenBack_LeavesTheFileTheModifiedTimeItWasCapturedWith()
    {
        // Writing a stream moves its file's modification time on NTFS. These
        // calls do the same, so streams written after the times would leave
        // the file with the time of the recovery.
        var held = new HeldStreams { OnWrite = file => File.SetLastWriteTimeUtc(file, DateTime.UtcNow) };

        var (_, output) = await RestoreAsync(Carrier(Deterministic(4_096, 2), ("Zone.Identifier", Zone)), held);

        Assert.AreEqual(1, held.Writes);
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds((long)CapturedModifiedAt).UtcDateTime,
            File.GetLastWriteTimeUtc(Path.Combine(output, "data", "streams.bin")));
    }

    [TestMethod]
    [DataRow(@"..\..\escape")]
    [DataRow("..")]
    [DataRow("other:$DATA")]
    public async Task RecoveryRestore_AStreamWhoseNameNoStreamCanHave_IsNotWritten_AndIsNoted(string name)
    {
        var content = Deterministic(4_096, 3);
        var held = new HeldStreams();

        var (report, output) = await RestoreAsync(Carrier(content, (name, Zone), ("notes", Notes)), held);

        var landed = Path.Combine(output, "data", "streams.bin");
        SequenceAssert.AreEqual(content, File.ReadAllBytes(landed));
        Assert.AreEqual((landed, "notes"), Assert.ContainsSingle(held.Streams.Keys));
        Assert.AreEqual(1, held.Writes);
        Assert.AreEqual(1, report.Restored);
        Assert.AreEqual(1, NotApplied(report));
        var note = Assert.ContainsSingle(report.Notes.Where(candidate => candidate.Kind == RecoveryNoteKind.StreamNotWritten));
        Assert.AreEqual(CarrierPath, note.Path);
        Assert.Contains(name, note.Reason!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RecoveryRestore_AStreamTheOutputRefuses_IsNoted_SayingWhy()
    {
        var held = new HeldStreams { Refusal = "the volume keeps no streams" };

        var (report, _) = await RestoreAsync(Carrier(Deterministic(4_096, 4), ("Zone.Identifier", Zone)), held);

        Assert.AreEqual(1, report.Restored);
        Assert.AreEqual(1, NotApplied(report));
        var note = Assert.ContainsSingle(report.Notes.Where(candidate => candidate.Kind == RecoveryNoteKind.StreamNotWritten));
        Assert.Contains("Zone.Identifier", note.Reason!, StringComparison.Ordinal);
        Assert.Contains("the volume keeps no streams", note.Reason!, StringComparison.Ordinal);
        Assert.Contains("alternate stream", note.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RecoveryRestore_AStreamWhoseRecordWillNotRead_IsNotWritten_AndTheFileStillRestores()
    {
        var content = Deterministic(4_096, 5);
        var held = new HeldStreams();

        var (report, output) = await RestoreAsync(
            Carrier(content, ("Zone.Identifier", Zone), ("notes", Notes)), held, damage: "Zone.Identifier");

        var landed = Path.Combine(output, "data", "streams.bin");
        SequenceAssert.AreEqual(content, File.ReadAllBytes(landed));
        Assert.AreEqual((landed, "notes"), Assert.ContainsSingle(held.Streams.Keys));
        Assert.AreEqual(1, report.Restored);
        var note = Assert.ContainsSingle(report.Notes.Where(candidate => candidate.Kind == RecoveryNoteKind.StreamNotWritten));
        Assert.Contains(nameof(RecordReadOutcome.AuthenticationFailed), note.Reason!, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "POSIX keeps no alternate data streams, and a colon is a name's own character there")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task RecoveryRestore_OnPosix_AsksThePlatformForNoStream_AndCountsThemNotApplied()
    {
        var (report, output) = await RestoreAsync(Carrier(Deterministic(4_096, 6), ("Zone.Identifier", Zone)), streams: null);

        Assert.AreEqual("streams.bin", Path.GetFileName(Assert.ContainsSingle(Directory.GetFiles(Path.Combine(output, "data")))));
        Assert.AreEqual(1, NotApplied(report));
        Assert.DoesNotContain(note => note.Kind == RecoveryNoteKind.StreamNotWritten, report.Notes);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps alternate data streams")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task RecoveryRestore_OnWindows_AFileCarryingStreams_RoundTripsWithThem()
    {
        var content = Deterministic(70_000, 7);
        var tree = Directory.CreateDirectory(Path.Combine(_root, "tree", "data")).FullName;
        var file = Path.Combine(tree, "streams.bin");
        File.WriteAllBytes(file, content);
        File.WriteAllBytes(file + ":Zone.Identifier", Zone);
        File.WriteAllBytes(file + ":notes", Notes);
        var modified = new DateTime(2024, 7, 26, 9, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, modified);

        var (report, output) = await RestoreAsync(
            new LocalFileSystemSource(), streams: null, root: Path.Combine(_root, "tree"));

        var landed = Path.Combine(output, "data", "streams.bin");
        Assert.AreEqual(1, report.Restored);
        Assert.AreEqual(0, NotApplied(report));
        SequenceAssert.AreEqual(content, File.ReadAllBytes(landed));
        SequenceAssert.AreEqual(Zone, File.ReadAllBytes(landed + ":Zone.Identifier"));
        SequenceAssert.AreEqual(Notes, File.ReadAllBytes(landed + ":notes"));
        Assert.AreEqual(modified, File.GetLastWriteTimeUtc(landed));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    private static int NotApplied(RecoveryRestoreReport report) =>
        report.NotApplied.SingleOrDefault(entry => entry.Attribute == "alternate_streams")?.Items ?? 0;

    private static FakeFileSystemSource Carrier(byte[] content, params (string Name, byte[] Content)[] streams)
    {
        var source = new FakeFileSystemSource();
        var carrier = source.AddFile(CarrierPath, content, fileId: 9_301);
        carrier.Metadata = new EntryMetadata { ModifiedAt = CapturedModifiedAt };
        foreach (var (name, stream) in streams)
        {
            carrier.AlternateStreams[name] = stream;
        }

        return source;
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

    /// <summary>
    /// Backs <paramref name="source"/> up into a repository made from the
    /// passphrase, damaging the record of the carrier's stream
    /// <paramref name="damage"/> where one is named, then restores the one
    /// snapshot with the recovery session alone.
    /// </summary>
    private async Task<(RecoveryRestoreReport Report, string Output)> RestoreAsync(
        IFileSystemSource source, IAlternateStreams? streams, string? damage = null, string root = "/")
    {
        var storeRoot = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("n"));
        var store = new LocalFileSystemObjectStore(storeRoot);
        using (var passphrase = Passphrase.Create(PassphraseText))
        {
            var (repository, authority) = await RepositoryLifecycle.CreateFromPassphraseAsync(
                store, passphrase, RepositoryCreationSettings.Default,
                createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None);
            using var _repository = repository;
            using var _authority = authority;

            var spool = Directory.CreateDirectory(Path.Combine(_root, "spool-" + Guid.NewGuid().ToString("n"))).FullName;
            var published = await new PublicationOrchestrator(
                    CapturePolicy.Default with { SegmentSize = SegmentSize.Create(64 * 1024) },
                    repository.RepositoryId,
                    WriterId.FromBytes(Enumerable.Repeat((byte)0xA0, 16).ToArray()),
                    repository.CurrentDataGeneration,
                    repository.Keys,
                    repository.Credential,
                    store,
                    new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                    spool,
                    FormatVersions.SealedDataPlane)
                .PublishAsync(
                    new SnapshotJob
                    {
                        Source = source,
                        Roots = [new ScanRoot(root)],
                        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                        SnapshotId = Enumerable.Repeat((byte)0x67, 16).ToArray(),
                        NowUnixMilliseconds = 1_722_600_000_001,
                        DeclaredMaxDurationMs = 3_600_000,
                        ExpiryGeneration = 5,
                        ClientVersion = "recovery-streams/1.0",
                    },
                    CancellationToken.None);
            Assert.IsEmpty(published.Failures);

            if (damage is not null)
            {
                using var probe = new RepositoryReader(repository.RepositoryId, repository.Keys, store, authority);
                await probe.LoadBlobsAsync(CancellationToken.None);
                var carrier = published.Files.Single(candidate => candidate.RelativePath == CarrierPath);
                var manifest = FileVersionManifestCodec.Decode(
                    (await probe.ReadSegmentAsync(carrier.ObjectId, CancellationToken.None)).Plaintext!);
                var stream = manifest.Metadata.AlternateStreams.Single(
                    candidate => System.Text.Encoding.UTF8.GetString(candidate.Name.Span) == damage);
                Assert.IsTrue(probe.TryLocateRecord(stream.ObjectId, out var blobKey, out var entry));
                var blob = Path.Combine(storeRoot, blobKey.Value.Replace('/', Path.DirectorySeparatorChar));
                var stored = await File.ReadAllBytesAsync(blob, CancellationToken.None);
                stored[(long)entry.PhysicalOffset + 54 + 3] ^= 0x01;
                await File.WriteAllBytesAsync(blob, stored, CancellationToken.None);
            }
        }

        using var open = Passphrase.Create(PassphraseText);
        using var session = await RecoverySession.OpenAsync(open, store, CancellationToken.None);
        await session.LoadBlobsAsync(CancellationToken.None);
        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));

        var output = Path.Combine(_root, "restored-" + Guid.NewGuid().ToString("n"));
        var report = await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, makeLink: null, streams, CancellationToken.None);
        return (report, output);
    }
}
