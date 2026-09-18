using System.Security.Cryptography;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// A restore grant's capability to open sealed content keys, in the two
/// shapes the formats define: one key per blob carried in a format-2 data
/// envelope ([ADR-0042](../../docs/adr/0042-write-only-repositories.md) §2),
/// and one key per record carried in a format-3 data record's prefix
/// (specification 05 §2.2). A reader is handed this rather than a delegate
/// because the two questions share one scalar and one repository identity,
/// and because which one a blob asks is decided by its envelope and not by
/// the caller that opened it.
/// </summary>
/// <remarks>
/// The derived scalar is copied here and zeroed on <see cref="Dispose"/>;
/// the authority it came from stays the caller's to dispose. A
/// <see cref="BlobReader"/> keeps the reference and never disposes it — the
/// component that built the opener outlives its readers and owns the
/// lifetime.
/// </remarks>
public sealed class SealedContentKeyOpener : IDisposable
{
    private readonly byte[] _sealingPrivateKey;
    private readonly RepositoryId _repositoryId;

    /// <summary>Creates an opener over a copy of the grant's derived scalar.</summary>
    /// <param name="sealingPrivateKey">The X25519 scalar the passphrase derives.</param>
    /// <param name="repositoryId">The repository the shares are pinned to.</param>
    public SealedContentKeyOpener(ReadOnlySpan<byte> sealingPrivateKey, RepositoryId repositoryId)
    {
        _sealingPrivateKey = sealingPrivateKey.ToArray();
        _repositoryId = repositoryId;
    }

    /// <summary>Opens a format-2 data blob's per-blob content key from its envelope.</summary>
    /// <exception cref="SealedContentException">The share does not open.</exception>
    public byte[] OpenBlobKey(BlobEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return SealedContentKey.Open(_sealingPrivateKey, envelope.SealedContentKey, _repositoryId, envelope.BlobId);
    }

    /// <summary>Opens one format-3 data record's content key from the share its prefix carries.</summary>
    /// <param name="share">The record's 80-byte sealed key.</param>
    /// <param name="objectId">The record's object identifier — the share's associated data.</param>
    /// <exception cref="SealedContentException">
    /// The share does not open. That is this <em>record's</em> damage and
    /// never the blob's: a share transplanted onto another object refuses
    /// here while every other record in the blob still reads.
    /// </exception>
    public byte[] OpenRecordKey(ReadOnlySpan<byte> share, ObjectId objectId) =>
        SealedRecordKey.Open(_sealingPrivateKey, share, _repositoryId, objectId);

    /// <inheritdoc />
    public void Dispose() => CryptographicOperations.ZeroMemory(_sealingPrivateKey);
}
