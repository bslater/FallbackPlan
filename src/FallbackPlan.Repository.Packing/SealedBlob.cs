using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// A sealed, immutable blob awaiting upload (specification 05 §5, §7):
/// its identity, digest, record table, and a re-openable content factory —
/// the shape <c>IObjectStore.PutAsync</c> requires, because a retry must be
/// able to produce the bytes again. Uploading is not publication.
/// </summary>
public sealed class SealedBlob : IAsyncDisposable
{
    private readonly string _spoolPath;

    internal SealedBlob(
        string spoolPath,
        BlobId blobId,
        BlobClass blobClass,
        long length,
        byte[] digest,
        IReadOnlyList<RecordTableEntry> recordTable)
    {
        _spoolPath = spoolPath;
        BlobId = blobId;
        BlobClass = blobClass;
        Length = length;
        Digest = digest;
        RecordTable = recordTable;
    }

    /// <summary>The writer-allocated blob identifier.</summary>
    public BlobId BlobId { get; }

    /// <summary>The blob's class.</summary>
    public BlobClass BlobClass { get; }

    /// <summary>The sealed blob's total length in bytes.</summary>
    public long Length { get; }

    /// <summary>
    /// The SHA-256 digest over every sealed byte before the locator
    /// (specification 05 §5; see <see cref="FooterLocator"/> for the
    /// preimage note).
    /// </summary>
    public IReadOnlyList<byte> Digest { get; }

    /// <summary>The record table the footer carries.</summary>
    public IReadOnlyList<RecordTableEntry> RecordTable { get; }

    /// <summary>
    /// Opens the sealed bytes for upload. May be invoked as many times as a
    /// provider's retry policy requires — each call re-opens the spool file.
    /// </summary>
    public ValueTask<Stream> OpenContentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<Stream>(new FileStream(
            _spoolPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true));
    }

    /// <summary>Deletes the spool file.</summary>
    public ValueTask DisposeAsync()
    {
        if (File.Exists(_spoolPath))
        {
            File.Delete(_spoolPath);
        }

        return ValueTask.CompletedTask;
    }
}
