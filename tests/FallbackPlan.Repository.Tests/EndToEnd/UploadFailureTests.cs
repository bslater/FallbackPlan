using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A store that stops taking blobs part way through a capture fails the run
/// at once, in the store's words, and no file is reported as failed for it
/// (FR-DEST-015; ADR-0046 Amendment 3). A direct-ship run whose last
/// destination drops is such a store, and so is a staging archive whose disk
/// fills. Before, the failure surfaced inside whichever file's read sealed
/// the next blob, was recorded as that file's own, and the run read the rest
/// of the source before failing.
/// </summary>
[TestClass]
public sealed class UploadFailureTests : IDisposable
{
    private const int Files = 100;
    private const string Refusal = "Incorrect function. : 'blobs/data'";
    private const string PassphraseText = "upload-failure-passphrase!!";

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-upload-failure", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public async Task Capture_WhoseStoreStopsTakingBlobs_FailsAtOnce_InTheStoresWords_AndBlamesNoFile()
    {
        var source = Source(changing: false);
        var logger = new RecordingLogger();

        var failure = await Assert.ThrowsAsync<IOException>(() => PublishAsync(source, logger));

        Assert.Contains(Refusal, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(record => record.EventId == FileFailedEvent, logger.Records);
        Assert.IsLessThan(Files / 2, source.OpenedPaths.Count, "the run stops reading once a blob will not land");
    }

    [TestMethod]
    public async Task Capture_WhoseStoreStopsTakingBlobs_DoesNotTakeTheFailureForAFileChangingUnderTheReader()
    {
        // A read that fails while revalidation says the file is changing is
        // that file's, recorded as changed during the read. A blob the store
        // will not take is not, whatever the file is doing.
        var source = Source(changing: true);
        var logger = new RecordingLogger();

        var failure = await Assert.ThrowsAsync<IOException>(() => PublishAsync(source, logger));

        Assert.Contains(Refusal, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(record => record.EventId == FileFailedEvent, logger.Records);
        Assert.IsLessThan(Files / 2, source.OpenedPaths.Count);
    }

    [TestMethod]
    public async Task Capture_WhoseStoreStopsTakingMetadataBlobs_FailsAtOnce_AndBlamesNoFile()
    {
        // A file's manifest goes into a metadata blob, sealed and put while
        // the file is captured.
        var source = Source(changing: false);
        var logger = new RecordingLogger();

        var failure = await Assert.ThrowsAsync<IOException>(() => PublishAsync(source, logger, refused: "blobs/meta/"));

        Assert.Contains(Refusal, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(record => record.EventId == FileFailedEvent, logger.Records);
        Assert.IsLessThan(Files / 2, source.OpenedPaths.Count);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale temp directory, never a failed assertion.
        }
    }

    /// <summary>The event a file's capture failure is logged under ("Could not capture …").</summary>
    private const int FileFailedEvent = 2012;

    /// <summary>
    /// Files of distinct content, each its own blob under the policy below,
    /// so the store is asked for a blob per file.
    /// </summary>
    private static FakeFileSystemSource Source(bool changing)
    {
        var source = new FakeFileSystemSource();
        for (var index = 0; index < Files; index++)
        {
            var content = new byte[2_000];
            new Random(index).NextBytes(content);
            var node = source.AddFile($"photos/{index:D3}.jpg", content);
            node.Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000 };
            if (changing)
            {
                node.RevalidationChangesRemaining = int.MaxValue;
            }
        }

        return source;
    }

    private async Task PublishAsync(FakeFileSystemSource source, RecordingLogger logger, string refused = "blobs/data/")
    {
        var inner = new LocalFileSystemObjectStore(Path.Combine(_root, "repo"));
        using var passphrase = Passphrase.Create(PassphraseText);
        var (repository, authority) = await RepositoryLifecycle.CreateFromPassphraseAsync(
            inner, passphrase, RepositoryCreationSettings.Default,
            createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None);
        using var _repository = repository;
        using var _authority = authority;

        var spool = Directory.CreateDirectory(Path.Combine(_root, "spool")).FullName;
        await new PublicationOrchestrator(
                CapturePolicy.Default with
                {
                    SegmentSize = SegmentSize.Create(64 * 1024),
                    BlobWriteProfile = BlobWriteProfile.LocalDefault with { MaximumRecordCount = 1 },
                },
                repository.RepositoryId,
                WriterId.FromBytes(Enumerable.Repeat((byte)0xA1, 16).ToArray()),
                repository.CurrentDataGeneration,
                repository.Keys,
                repository.Credential,
                new BlobRefusingStore(inner, refused, takeFirst: 2),
                new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                spool,
                repository.EffectiveFormatVersion,
                logger: logger)
            .PublishAsync(
                new SnapshotJob
                {
                    Source = source,
                    Roots = [new ScanRoot("/")],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat((byte)0x6A, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_001,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "upload-failure/1.0",
                },
                CancellationToken.None);
    }

    /// <summary>
    /// Takes the first <paramref name="takeFirst"/> blobs under
    /// <paramref name="refused"/>, then refuses every one after as a FAT32
    /// drive refused them: an I/O failure naming the folder. Everything else
    /// passes through.
    /// </summary>
    private sealed class BlobRefusingStore(IObjectStore inner, string refused, int takeFirst) : IObjectStore
    {
        private int _blobs;

        public StoreCapabilities Capabilities => inner.Capabilities;

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, range, cancellationToken);

        public ValueTask<PutResult> PutAsync(
            ObjectKey key, Func<CancellationToken, ValueTask<Stream>> openContent, PutConditions conditions, CancellationToken cancellationToken) =>
            key.Value.StartsWith(refused, StringComparison.Ordinal) && Interlocked.Increment(ref _blobs) > takeFirst
                ? throw new IOException(Refusal)
                : inner.PutAsync(key, openContent, conditions, cancellationToken);

        public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
    }
}
