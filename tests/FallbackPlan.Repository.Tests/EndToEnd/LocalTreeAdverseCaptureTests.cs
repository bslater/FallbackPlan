using System.Runtime.Versioning;
using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Capture under adverse I/O against a real <see cref="LocalFileSystemSource"/>
/// and real files on disk: a file another process holds, a file with no read
/// permission, a file deleted under the reader, and a file rewritten while it
/// is being read (architecture 06 §1; ADR-0026 §Decisions 2–3).
/// </summary>
/// <remarks>
/// <para>
/// These are the <em>outcome</em> tests. The OS owns the timing here, so what
/// can be asserted is what came back — coherent bytes, the right failure
/// reason, the rest of the tree intact. The attempt counts and diagnostic
/// strings belong to <see cref="AdverseCaptureTests"/>, which drives the fake
/// source and can say exactly when a read fails.
/// </para>
/// <para>
/// Several tests come in platform pairs, and the pairing is the subject rather
/// than an accident. On POSIX the walk opens a file's content with
/// <c>openat</c> during traversal and <c>OpenRead</c> hands out a duplicate of
/// that descriptor, so a lock or an unlink applied to the <em>name</em> cannot
/// reach the read in flight. On Windows there is no such handle — the whole
/// capture path is by name — so the same interference lands squarely on it.
/// One test with a platform-conditional assertion inside would hide that; two
/// named tests state it.
/// </para>
/// </remarks>
[TestClass]
public sealed class LocalTreeAdverseCaptureTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private readonly string _sourceRoot =
        Path.Combine(Path.GetTempPath(), "fbp-adverse-capture-tests", Guid.NewGuid().ToString("n"));

    [TestMethod]
    [PlatformCondition(
        TestPlatforms.Windows,
        "a FileShare.None hold is enforced against another opener only here; the POSIX capture path "
        + "reads a descriptor the walk already opened, which no advisory lock on the name can reach")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task AFileHeldWithoutSharing_IsAnErrorManifestEntryAndTheRestOfTheTreeStillRestores()
    {
        var keepContent = Write("keep.bin", 40_000, seed: 11);
        var lockedPath = Path.Combine(_sourceRoot, "locked.bin");
        Write("locked.bin", 40_000, seed: 12);

        PublishedTreeSnapshot published;
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Held for exactly as long as the publication runs. The using is
            // not tidiness: an undisposed handle here fails the temp-root
            // delete on Windows and leaves the mess for the next class.
            published = await PublishAsync(new LocalFileSystemSource(), 0x11);
        }

        var failure = Assert.ContainsSingle(published.Failures);
        Assert.AreEqual("locked.bin", NameOf(failure));

        // The reason, never the message: Windows localises sharing-violation
        // text, and the hostile-locale job would be the one to find out.
        Assert.AreEqual(CaptureFailureReason.IoError, failure.Reason);

        await AssertRestoresAsync(published, "keep.bin", keepContent);
        Assert.AreEqual(2, await CaptureStatusAsync(published));
    }

    [TestMethod]
    [PlatformCondition(
        TestPlatforms.Posix,
        "the walk opens content with openat during traversal and OpenRead duplicates that descriptor, "
        + "so a lock taken through the name never reaches the read — the asymmetry with Windows is the subject")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task AFileHeldWithoutSharing_IsStillCapturedWhereTheReadComesFromTheWalksOwnDescriptor()
    {
        var content = Write("held.bin", 40_000, seed: 13);
        var heldPath = Path.Combine(_sourceRoot, "held.bin");

        PublishedTreeSnapshot published;
        using (new FileStream(heldPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            published = await PublishAsync(new LocalFileSystemSource(), 0x12);
        }

        // Nothing failed, and that is correct rather than lucky: the bytes
        // came from a descriptor the walk already held.
        Assert.IsEmpty(published.Failures);
        Assert.AreEqual(1, await CaptureStatusAsync(published));
        await AssertRestoresAsync(published, "held.bin", content);
    }

    [TestMethod]
    [UnprivilegedPlatformCondition(
        TestPlatforms.Posix,
        "denial is expressed with chmod, and a privileged process reads a mode-000 file regardless")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task AFileWithNoReadPermission_IsAnErrorManifestEntryNamingPermission()
    {
        var keepContent = Write("keep.bin", 20_000, seed: 21);
        var deniedPath = Path.Combine(_sourceRoot, "denied.bin");
        Write("denied.bin", 20_000, seed: 22);

        PublishedTreeSnapshot published;
        File.SetUnixFileMode(deniedPath, UnixFileMode.None);
        try
        {
            published = await PublishAsync(new LocalFileSystemSource(), 0x21);
        }
        finally
        {
            // Restored before Dispose, or the recursive delete of the temp
            // root fails and the next class inherits it.
            File.SetUnixFileMode(deniedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var failure = Assert.ContainsSingle(published.Failures);
        Assert.AreEqual("denied.bin", NameOf(failure));
        Assert.AreEqual(CaptureFailureReason.Permission, failure.Reason);

        // Raised by the scanner, not by the walker's catch: openat fails
        // during traversal, so this pins the scan-to-publication joint that
        // LocalScanTests and PartialBackupHonestyTests each see one side of.
        await AssertRestoresAsync(published, "keep.bin", keepContent);
        Assert.AreEqual(2, await CaptureStatusAsync(published));
    }

    [TestMethod]
    public async Task AFileRewrittenUnderTheReader_IsCapturedWholeAndNeverTorn()
    {
        var before = LowBytes(512 * 1024, 31);
        var after = HighBytes(640 * 1024, 32);
        WriteBytes("ledger.bin", before);

        var source = new InterferingSource(
            new LocalFileSystemSource(),
            Path.Combine(_sourceRoot, "ledger.bin"),
            afterBytes: 64 * 1024,
            () => Rewrite("ledger.bin", after));

        var published = await PublishAsync(source, 0x31);

        Assert.IsTrue(source.Interfered, "the rewrite never reached the reader, so nothing was proved");
        Assert.IsEmpty(published.Failures);
        await AssertNotTornAsync(published, "ledger.bin", after);
    }

    [TestMethod]
    public async Task BothVersionsSurviveAnUpdateMidRead_AcrossTwoSnapshots()
    {
        // The guarantee in full: the version that was there when the first
        // backup ran comes back from the first snapshot, the version the
        // rewrite left comes back from the second, and neither is a mixture.
        //
        // The two versions use disjoint alphabets — every byte of the first is
        // below 0x80, every byte of the second at or above it — so "not torn"
        // is a byte test. Their lengths differ too, giving the consistency
        // check a way to notice that does not depend on any clock.
        var before = LowBytes(512 * 1024, 41);
        var after = HighBytes(640 * 1024, 42);
        var bystander = LowBytes(8_000, 43);
        WriteBytes("ledger.bin", before);
        WriteBytes("notes.txt", bystander);

        var first = await PublishAsync(new LocalFileSystemSource(), 0xA1);
        Assert.IsEmpty(first.Failures);

        var source = new InterferingSource(
            new LocalFileSystemSource(),
            Path.Combine(_sourceRoot, "ledger.bin"),
            afterBytes: 64 * 1024,
            () => Rewrite("ledger.bin", after));

        var second = await PublishAsync(source, 0xB2);

        Assert.IsTrue(source.Interfered, "the rewrite never reached the reader, so nothing was proved");
        Assert.AreEqual(2, source.Opens, "a torn read should have been rejected and the file read again");
        Assert.IsEmpty(second.Failures);

        // The second snapshot holds the update, whole.
        await AssertNotTornAsync(second, "ledger.bin", after);

        // And the first still holds what was there before it — asserted after
        // the second exists, because the claim is about both snapshots
        // standing together, not about either one in isolation.
        var restoredFirst = await RestoreAsync(first, "ledger.bin");
        SequenceAssert.AreEqual(before, restoredFirst);
        Assert.IsFalse(
            restoredFirst.Any(value => value >= 0x80),
            "the first snapshot must not have acquired bytes from a rewrite that happened after it");

        Assert.AreNotEqual(
            VersionOf(first, "ledger.bin").ObjectId,
            VersionOf(second, "ledger.bin").ObjectId,
            "two different versions of one file are two different objects");

        SequenceAssert.AreEqual(bystander, await RestoreAsync(first, "notes.txt"));
        SequenceAssert.AreEqual(bystander, await RestoreAsync(second, "notes.txt"));
    }

    [TestMethod]
    [PlatformCondition(
        TestPlatforms.Posix,
        "the walk holds the content descriptor, so unlink takes the name and not the bytes, and "
        + "revalidation fstats that same descriptor")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task AFileUnlinkedUnderTheReader_IsCapturedCleanlyBecauseTheDescriptorStillNamesIt()
    {
        var content = LowBytes(64 * 1024, 51);
        WriteBytes("doomed.bin", content);

        var source = new InterferingSource(
            new LocalFileSystemSource(),
            Path.Combine(_sourceRoot, "doomed.bin"),
            afterBytes: null,
            () => File.Delete(Path.Combine(_sourceRoot, "doomed.bin")));

        var published = await PublishAsync(source, 0x51);

        Assert.IsTrue(source.Interfered, "the deletion never reached the reader, so nothing was proved");
        Assert.IsEmpty(published.Failures);
        Assert.AreEqual(1, await CaptureStatusAsync(published));

        var manifest = await ReadFileVersionAsync(VersionOf(published, "doomed.bin").ObjectId);
        Assert.DoesNotContain(
            diagnostic => diagnostic.StartsWith("captured-inconsistent", StringComparison.Ordinal),
            manifest.CaptureDiagnostics);
        SequenceAssert.AreEqual(content, await RestoreAsync(published, "doomed.bin"));
    }

    [TestMethod]
    [PlatformCondition(
        TestPlatforms.Windows,
        "capture opens by name here — the walk takes no content handle — so revalidation stats a name "
        + "that is gone and the probe comes back null")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task AFileDeletedUnderTheReader_IsPublishedWholeAndMarkedInconsistent()
    {
        var content = LowBytes(64 * 1024, 52);
        WriteBytes("doomed.bin", content);

        var source = new InterferingSource(
            new LocalFileSystemSource(),
            Path.Combine(_sourceRoot, "doomed.bin"),
            afterBytes: null,
            () => File.Delete(Path.Combine(_sourceRoot, "doomed.bin")));

        var published = await PublishAsync(source, 0x52);

        Assert.IsTrue(source.Interfered, "the deletion never reached the reader, so nothing was proved");

        // Published whole and diagnosed, not failed: the bytes are complete,
        // but nothing can confirm what they are the content of.
        Assert.IsEmpty(published.Failures);
        Assert.AreEqual(1, await CaptureStatusAsync(published));

        var manifest = await ReadFileVersionAsync(VersionOf(published, "doomed.bin").ObjectId);
        Assert.Contains("captured-inconsistent: 1", manifest.CaptureDiagnostics);
        SequenceAssert.AreEqual(content, await RestoreAsync(published, "doomed.bin"));
    }

    /// <summary>
    /// A real source with one file's read interfered with, once — the window
    /// in which another process rewrites or deletes a file a backup is
    /// part-way through. Everything but <see cref="OpenRead"/> delegates
    /// verbatim, so the scan still takes its own content handle where the
    /// platform offers one and the handle path is genuinely exercised.
    /// </summary>
    private sealed class InterferingSource(
        IFileSystemSource inner, string interferingWith, int? afterBytes, Action interference) : IFileSystemSource
    {
        /// <summary>How many times the target's content has been opened.</summary>
        public int Opens { get; private set; }

        /// <summary>Whether the interference actually ran — a test that silently missed it proves nothing.</summary>
        public bool Interfered { get; private set; }

        public SourceFilesystemInfo Probe(string rootPath) => inner.Probe(rootPath);

        public RevalidationProbe? Revalidate(ScanEntry entry) => inner.Revalidate(entry);

        public IAsyncEnumerable<ScanEvent> ScanAsync(
            string rootPath, ScanOptions options, CancellationToken cancellationToken) =>
            inner.ScanAsync(rootPath, options, cancellationToken);

        public Stream OpenAlternateStream(ScanEntry entry, string streamName) =>
            inner.OpenAlternateStream(entry, streamName);

        public Stream OpenRead(ScanEntry entry)
        {
            var stream = inner.OpenRead(entry);
            if (!string.Equals(entry.FullPath, interferingWith, StringComparison.Ordinal))
            {
                return stream;
            }

            Opens++;
            return new InterferingStream(stream, afterBytes, Interfere);
        }

        private void Interfere()
        {
            if (Interfered)
            {
                return;
            }

            Interfered = true;
            interference();
        }

        /// <summary>
        /// Runs the interference once — at a byte offset when one is given, or
        /// at close when it is not.
        /// </summary>
        /// <remarks>
        /// Close, for a deletion, is deliberate. Deleting part-way through
        /// leaves the file delete-pending on Windows, where the name still
        /// answers a stat until the last handle goes, so revalidation would
        /// sometimes see it and sometimes not.
        /// </remarks>
        private sealed class InterferingStream(Stream inner, int? afterBytes, Action interference) : Stream
        {
            private long _read;
            private bool _fired;

            public override bool CanRead => inner.CanRead;

            public override bool CanSeek => inner.CanSeek;

            public override bool CanWrite => false;

            public override long Length => inner.Length;

            public override long Position
            {
                get => inner.Position;
                set => inner.Position = value;
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                var taken = inner.Read(buffer);
                _read += taken;
                Fire();
                return taken;
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var taken = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                _read += taken;
                Fire();
                return taken;
            }

            public override Task<int> ReadAsync(
                byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

            public override void Flush() => inner.Flush();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask DisposeAsync()
            {
                // Overridden rather than left to the base, which routes through
                // the synchronous Dispose: the publisher closes the read with
                // `await using`, so this is the path that actually runs and the
                // interference has to be reachable from it.
                await inner.DisposeAsync().ConfigureAwait(false);
                FireOnClose();
                await base.DisposeAsync().ConfigureAwait(false);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                    FireOnClose();
                }

                base.Dispose(disposing);
            }

            /// <summary>The close-time trigger, for an interference with no byte offset.</summary>
            /// <remarks>
            /// Distinct from <see cref="Fire"/> rather than folded into it: that
            /// one refuses when no threshold is set, which is right for a read
            /// and made this case unreachable when Dispose called it.
            /// </remarks>
            private void FireOnClose()
            {
                if (_fired || afterBytes is not null)
                {
                    return;
                }

                _fired = true;
                interference();
            }

            private void Fire()
            {
                if (_fired || afterBytes is not { } threshold || _read < threshold)
                {
                    return;
                }

                _fired = true;
                interference();
            }
        }
    }

    private async Task<PublishedTreeSnapshot> PublishAsync(IFileSystemSource source, byte snapshotSeed)
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var orchestrator = new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory);

        // No catalogue and no prior snapshot, deliberately. With them, the
        // NFR-PERF-003 short-circuit would re-emit the earlier version of an
        // apparently unchanged file without opening it — correct behaviour
        // that would make every test here prove nothing.
        return await orchestrator.PublishAsync(
            new SnapshotJob
            {
                Source = source,
                Roots = [new ScanRoot(_sourceRoot)],
                DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                SnapshotId = Enumerable.Repeat(snapshotSeed, 16).ToArray(),
                NowUnixMilliseconds = 1_722_600_000_000,
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = 5,
                ClientVersion = "local-tree-adverse-capture-tests/1.0",
            },
            CancellationToken.None);
    }

    private async Task AssertNotTornAsync(PublishedTreeSnapshot published, string name, byte[] expected)
    {
        var restored = await RestoreAsync(published, name);
        SequenceAssert.AreEqual(expected, restored);
        Assert.IsFalse(
            restored.Any(value => value < 0x80),
            "a torn capture would carry bytes from both versions");
    }

    private async Task AssertRestoresAsync(PublishedTreeSnapshot published, string name, byte[] expected) =>
        SequenceAssert.AreEqual(expected, await RestoreAsync(published, name));

    private static PublishedFileVersion VersionOf(PublishedTreeSnapshot published, string name) =>
        published.Files.Single(file => file.RelativePath == name);

    private async Task<byte[]> RestoreAsync(PublishedTreeSnapshot published, string name)
    {
        var manifest = await ReadFileVersionAsync(VersionOf(published, name).ObjectId);
        using var keys = CreateKeys();
        using var reader = new RepositoryReader(Repo, keys, CreateStore());
        await reader.LoadBlobsAsync(CancellationToken.None);

        using var restored = new MemoryStream();

        // Through the restore engine rather than the reader's raw reassembly,
        // so the whole-file hash is verified (FR-RST-002) and a reassembly
        // that merely produced the right length would still fail here.
        var result = await new RestoreEngine(reader).RestoreFileAsync(
            manifest, restored, CancellationToken.None);
        Assert.IsTrue(result.Success, result.FailureDetail);
        return restored.ToArray();
    }

    private async Task<FileVersionManifest> ReadFileVersionAsync(ObjectId id)
    {
        using var keys = CreateKeys();
        using var reader = new RepositoryReader(Repo, keys, CreateStore());
        await reader.LoadBlobsAsync(CancellationToken.None);
        var read = await reader.ReadSegmentAsync(id, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        return FileVersionManifestCodec.Decode(read.Plaintext!);
    }

    private async Task<int> CaptureStatusAsync(PublishedTreeSnapshot published)
    {
        using var keys = CreateKeys();
        using var reader = new RepositoryReader(Repo, keys, CreateStore());
        await reader.LoadBlobsAsync(CancellationToken.None);
        var read = await reader.ReadSegmentAsync(published.SnapshotObjectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        return SnapshotManifestCodec.Decode(read.Plaintext!).Manifest.CaptureStatus;
    }

    private static string NameOf(CaptureFailure failure) =>
        Encoding.UTF8.GetString(failure.PathComponents[^1].Span);

    private byte[] Write(string relativePath, int length, int seed)
    {
        var content = LowBytes(length, seed);
        WriteBytes(relativePath, content);
        return content;
    }

    private void WriteBytes(string relativePath, byte[] content)
    {
        var full = Path.Combine(_sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    /// <summary>
    /// Replaces a file's content in place and moves its modification time
    /// forward explicitly.
    /// </summary>
    /// <remarks>
    /// The stamp is not decoration. A rewrite landing in the same millisecond
    /// as the scan's own stat — entirely plausible for one small file on a
    /// fast runner — would let the modification-time half of the consistency
    /// check agree, and a torn read could then be declared clean. The
    /// differing length already covers it; this is the second belt.
    /// </remarks>
    private void Rewrite(string relativePath, byte[] content)
    {
        var full = Path.Combine(_sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(full, content);
        File.SetLastWriteTimeUtc(full, File.GetLastWriteTimeUtc(full).AddSeconds(5));
    }

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
                // A file this suite deliberately made unreadable or held open
                // is a stale temp directory, never a failed assertion.
            }
        }

        base.Dispose(disposing);
    }
}
