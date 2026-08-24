using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Capture under adverse I/O: files that will not open, files that fail
/// part-way through a read, files deleted out from under the reader, and files
/// rewritten while they are being read (architecture 06 §1; ADR-0026
/// §Decisions 2–3; NFR-OPS-002).
/// </summary>
/// <remarks>
/// <para>
/// The governing rule is that an unreadable file is an entry in the error
/// manifest, not a failed backup. Everything here is a variation on proving
/// that: the rest of the tree still captures, still restores, and the snapshot
/// is still published — while the thing that went wrong is named rather than
/// swallowed.
/// </para>
/// <para>
/// These are the <em>mechanism</em> tests, and they use the fake source
/// deliberately. Attempt counts, diagnostic strings and failure reasons are
/// only assertable where the timing is ours; the real filesystem cannot be
/// asked to fail on the 8,192nd byte. Its counterpart,
/// <see cref="LocalTreeAdverseCaptureTests"/>, asserts the <em>outcome</em>
/// against a real <c>LocalFileSystemSource</c>, where the OS owns the timing
/// and only the bytes that come back can be checked.
/// </para>
/// </remarks>
[TestClass]
public sealed class AdverseCaptureTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    [TestMethod]
    public async Task AnOpenThatIsRefused_LandsTheRightReasonForEachKindOfRefusal()
    {
        // ClassifyFailure's whole truth table. Only the permission row had a
        // test, and a classifier with one covered row is a classifier that can
        // report a locked file as missing without anything noticing.
        var source = new FakeFileSystemSource();
        var wanted = Deterministic(5_000, 2);
        source.AddFile("good.bin", wanted);
        source.AddFile("denied.bin", Deterministic(5_000, 3)).OpenFailure =
            new UnauthorizedAccessException("denied");
        source.AddFile("locked.bin", Deterministic(5_000, 4)).OpenFailure =
            new IOException("the process cannot access the file because another process has locked it");
        source.AddFile("gone.bin", Deterministic(5_000, 5)).OpenFailure =
            new FileNotFoundException("no longer there");

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(
            Job(source), CancellationToken.None);

        Assert.HasCount(3, published.Failures);
        Assert.AreEqual(CaptureFailureReason.Permission, ReasonFor(published, "denied.bin"));
        Assert.AreEqual(CaptureFailureReason.IoError, ReasonFor(published, "locked.bin"));
        Assert.AreEqual(CaptureFailureReason.NotFound, ReasonFor(published, "gone.bin"));

        // The backup happened. That is the point of the error manifest.
        var captured = Assert.ContainsSingle(published.Files);
        Assert.AreEqual("good.bin", captured.RelativePath);
        Assert.IsNotNull(published.ErrorManifestObjectId);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        SequenceAssert.AreEqual(wanted, await RestoreAsync(reader, captured.ObjectId));
        Assert.AreEqual(2, await CaptureStatusAsync(reader, published));
    }

    [TestMethod]
    public async Task AReadThatNeverCompletesWhileTheFileChanges_IsChangedDuringRead()
    {
        // The one place reason 4 belongs (ADR-0026 §Decision 2): no read
        // completed, and revalidation says the object was changing while it
        // was read. There are no bytes to publish, so no version is emitted.
        var source = new FakeFileSystemSource();
        var restless = source.AddFile("restless.bin", Deterministic(200_000, 20));
        restless.FailReadAfterBytes = 8 * 1024;
        restless.RevalidationChangesRemaining = 5;

        var calmContent = Deterministic(5_000, 21);
        source.AddFile("calm.bin", calmContent);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(
            Job(source), CancellationToken.None);

        var failure = Assert.ContainsSingle(published.Failures);
        Assert.AreEqual(CaptureFailureReason.ChangedDuringRead, failure.Reason);
        Assert.AreEqual("restless.bin", Encoding.UTF8.GetString(failure.PathComponents[0].Span));

        Assert.DoesNotContain(file => file.RelativePath == "restless.bin", published.Files);

        // The attempt budget is for a file that CHANGED, not for one that will
        // not read: a second attempt would reference segment ids the first
        // attempt reserved but never wrote.
        Assert.AreEqual(1, source.OpenedPaths.Count(path => path == "restless.bin"));

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var calm = Assert.ContainsSingle(published.Files);
        SequenceAssert.AreEqual(calmContent, await RestoreAsync(reader, calm.ObjectId));
        Assert.AreEqual(2, await CaptureStatusAsync(reader, published));

        // Reason 4 has never before reached a stored error manifest, so this
        // also exercises the decode bound that used to stop at 7.
        var errors = await ReadErrorManifestAsync(reader, published);
        Assert.AreEqual(
            CaptureFailureReason.ChangedDuringRead, Assert.ContainsSingle(errors.Failures).Reason);
    }

    [TestMethod]
    public async Task AReadThatFailsAgainstAnUnchangedFile_StaysAnIoError()
    {
        // The guard on the rule above. A failing medium and a file being
        // rewritten under the reader raise the same exception type; only
        // revalidation separates them, and a bad sector must not be reported
        // as somebody editing the file.
        var source = new FakeFileSystemSource();
        source.AddFile("bad-sector.bin", Deterministic(200_000, 30)).FailReadAfterBytes = 8 * 1024;

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(
            Job(source), CancellationToken.None);

        var failure = Assert.ContainsSingle(published.Failures);
        Assert.AreEqual(CaptureFailureReason.IoError, failure.Reason);
        Assert.AreNotEqual(
            CaptureFailureReason.ChangedDuringRead,
            failure.Reason,
            "an unchanged file that will not read is a medium fault, not an edit");
    }

    [TestMethod]
    public async Task AFileThatFailsOnItsSecondRead_PublishesTheLastCompleteRead()
    {
        // ADR-0026 §Decision 2's literal clause. Attempt one reads the file
        // whole and revalidation rejects it; attempt two faults before a byte.
        // There IS a complete read, so it is published — with the diagnostic
        // saying so, and without making the backup partial.
        var source = new FakeFileSystemSource();
        var content = Deterministic(40_000, 40);
        var rotating = source.AddFile("rotating.log", content);
        rotating.RevalidationChangesRemaining = 5;
        rotating.FailReadAfterBytes = 0;
        rotating.FailFromOpen = 2;

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(
            Job(source), CancellationToken.None);

        Assert.IsEmpty(published.Failures);
        Assert.IsNull(published.ErrorManifestObjectId);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var file = Assert.ContainsSingle(published.Files);
        var manifest = await ReadFileVersionAsync(reader, file.ObjectId);
        Assert.Contains("captured-inconsistent: 2", manifest.CaptureDiagnostics);
        SequenceAssert.AreEqual(content, await RestoreAsync(reader, file.ObjectId));
        Assert.AreEqual(1, await CaptureStatusAsync(reader, published));
    }

    [TestMethod]
    public async Task AFileThatVanishesWhileItIsRead_IsPublishedWholeAndMarkedInconsistent()
    {
        // Revalidation cannot observe the object at all. The bytes are in hand
        // and they are complete, but nothing can confirm what they are the
        // content of — so the version is published and the diagnostic says the
        // capture could not be validated. It used to be recorded as clean.
        var source = new FakeFileSystemSource();
        var content = Deterministic(30_000, 50);
        var doomed = source.AddFile("doomed.bin", content);
        doomed.OnOpened = node => source.Remove(node.RelativePath);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(
            Job(source), CancellationToken.None);

        Assert.IsEmpty(published.Failures);
        Assert.IsNull(published.ErrorManifestObjectId);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var file = Assert.ContainsSingle(published.Files);
        var manifest = await ReadFileVersionAsync(reader, file.ObjectId);

        // One attempt, not two: re-reading a name that no longer resolves
        // would spend the budget on a lookup that has already failed, and
        // would discard the complete bytes attempt one already has.
        Assert.Contains("captured-inconsistent: 1", manifest.CaptureDiagnostics);
        Assert.AreEqual(1, source.OpenedPaths.Count(path => path == "doomed.bin"));
        SequenceAssert.AreEqual(content, await RestoreAsync(reader, file.ObjectId));
    }

    [TestMethod]
    public async Task AFileRewrittenUnderTheReader_IsCapturedWholeOnTheSecondAttempt_NeverTorn()
    {
        // The two versions use disjoint alphabets — every byte of the first is
        // below 0x80 and every byte of the second is at or above it — so "the
        // capture is not a mixture of the two" is a byte test rather than an
        // argument. Their lengths differ too, so the consistency check has a
        // second, clock-independent way to notice.
        var source = new FakeFileSystemSource();
        var before = LowBytes(256 * 1024, 60);
        var after = HighBytes(320 * 1024, 61);

        var ledger = source.AddFile("ledger.bin", before);
        ledger.MutateAfterBytes = 64 * 1024;
        ledger.MutatedContent = after;
        ledger.MutatedModifiedAt = (ledger.Metadata.ModifiedAt ?? 0) + 5_000;

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(
            Job(source), CancellationToken.None);

        Assert.IsEmpty(published.Failures);
        Assert.IsNull(published.ErrorManifestObjectId);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var file = Assert.ContainsSingle(published.Files);
        var restored = await RestoreAsync(reader, file.ObjectId);

        SequenceAssert.AreEqual(after, restored);
        Assert.IsFalse(
            restored.Any(value => value < 0x80),
            "a torn capture would carry bytes from both versions");

        // Two opens: the torn attempt was rejected and the file read again.
        Assert.AreEqual(2, source.OpenedPaths.Count(path => path == "ledger.bin"));
        var manifest = await ReadFileVersionAsync(reader, file.ObjectId);
        Assert.Contains("captured-inconsistent: 2", manifest.CaptureDiagnostics);
    }

    [TestMethod]
    public async Task AnErrorManifestNamingAFileWhoseNameIsNotRepresentable_ReadsBackFromTheRepository()
    {
        // Reason 8. The scanner emits it for a name with no faithful UTF-8
        // form; the decoder refused it as unassigned, so the one file an
        // operator most needed named was the one that made the list of names
        // unreadable.
        var source = new FakeFileSystemSource();
        var content = Deterministic(4_000, 70);
        source.AddFile("readable.bin", content);
        source.InjectedFailures.Add(new ScanFailure(
            // Escaped rather than written literally: this name exists precisely
            // because the host could not represent it, and a source file that
            // carried the character itself would be one checkout away from
            // being the bug it is testing.
            "photos/bad\uFFFDname.jpg",
            CaptureFailureReason.NameNotRepresentable,
            "the entry's name has no faithful representation in both the host's string form and UTF-8"));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(
            Job(source), CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var errors = await ReadErrorManifestAsync(reader, published);
        Assert.AreEqual(
            CaptureFailureReason.NameNotRepresentable, Assert.ContainsSingle(errors.Failures).Reason);

        var file = Assert.ContainsSingle(published.Files);
        SequenceAssert.AreEqual(content, await RestoreAsync(reader, file.ObjectId));
    }

    private static CaptureFailureReason ReasonFor(PublishedTreeSnapshot published, string name) =>
        published.Failures
            .Single(failure => Encoding.UTF8.GetString(failure.PathComponents[0].Span) == name)
            .Reason;

    private static async Task<ErrorManifest> ReadErrorManifestAsync(
        RepositoryReader reader, PublishedTreeSnapshot published)
    {
        Assert.IsNotNull(published.ErrorManifestObjectId);
        var read = await reader.ReadSegmentAsync(published.ErrorManifestObjectId!.Value, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        return ErrorManifestCodec.Decode(read.Plaintext!);
    }

    private static async Task<int> CaptureStatusAsync(RepositoryReader reader, PublishedTreeSnapshot published)
    {
        var read = await reader.ReadSegmentAsync(published.SnapshotObjectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        return SnapshotManifestCodec.Decode(read.Plaintext!).Manifest.CaptureStatus;
    }

    private static async Task<FileVersionManifest> ReadFileVersionAsync(RepositoryReader reader, ObjectId id)
    {
        var read = await reader.ReadSegmentAsync(id, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        return FileVersionManifestCodec.Decode(read.Plaintext!);
    }

    private static async Task<byte[]> RestoreAsync(RepositoryReader reader, ObjectId versionId)
    {
        var manifest = await ReadFileVersionAsync(reader, versionId);
        using var restored = new MemoryStream();
        var result = await reader.RestoreAsync(manifest.SegmentReferences, restored, CancellationToken.None);
        Assert.IsTrue(result.Success, result.FailureDetail);
        return restored.ToArray();
    }

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, KeyHierarchy hierarchy) =>
        new(
            SmallBlobPolicy,
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            hierarchy,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory,
            observer: null);

    private static SnapshotJob Job(FakeFileSystemSource source) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat((byte)0x11, 16).ToArray(),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "adverse-capture-tests/1.0",
    };

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + (i * 31));
        }

        return data;
    }

    /// <summary>Pseudo-random bytes with the high bit cleared — the "before" alphabet.</summary>
    private static byte[] LowBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        for (var i = 0; i < length; i++)
        {
            data[i] &= 0x7F;
        }

        return data;
    }

    /// <summary>Pseudo-random bytes with the high bit set — the "after" alphabet.</summary>
    private static byte[] HighBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        for (var i = 0; i < length; i++)
        {
            data[i] |= 0x80;
        }

        return data;
    }
}
