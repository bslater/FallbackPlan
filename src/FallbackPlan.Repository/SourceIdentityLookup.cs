using System.Buffers.Binary;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>
/// The read side of the source-identity map (specification 06 §11): given a
/// prior snapshot, which published file version came from a given file
/// identity.
/// </summary>
/// <remarks>
/// This is the durable half of rename detection. The catalogue answers the
/// same question faster and is consulted first, but it is a disposable cache
/// — after a rebuild it holds no identities at all, and a rename captured in
/// that window would record no <c>parent_version</c>, severing the file's
/// history permanently. The map costs one small object fetch per
/// publication and removes that failure mode.
///
/// It is used for <b>ancestry only</b>, never for the NFR-PERF-003 content
/// short-circuit: the map carries no size and no modification time, and an
/// inode is reused after its file is deleted, so a match here establishes a
/// likely lineage and nothing about the bytes (06 §11.3).
/// </remarks>
public sealed class SourceIdentityLookup
{
    private readonly Dictionary<SourceKey, ObjectId> _versions;

    private SourceIdentityLookup(Dictionary<SourceKey, ObjectId> versions) => _versions = versions;

    /// <summary>The number of identities the map carried.</summary>
    public int Count => _versions.Count;

    /// <summary>
    /// Loads the map published for <paramref name="snapshotId"/>, or returns
    /// <see langword="null"/> when there is none.
    /// </summary>
    /// <remarks>
    /// Absence is ordinary — a writer that never published one, a store that
    /// refused the put, a snapshot from an implementation that does not write
    /// hints — and so is an object that fails to authenticate or to parse. A
    /// hint is never evidence, so nothing here is a damage finding; the
    /// caller falls back to matching by path and misses renames.
    /// </remarks>
    public static async ValueTask<SourceIdentityLookup?> TryLoadAsync(
        IObjectStore store,
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        ReadOnlyMemory<byte> snapshotId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keys);

        byte[] bytes;
        using (var read = await store
            .OpenReadAsync(MetadataStoreKeys.SourceIdentity(snapshotId.Span), range: null, cancellationToken)
            .ConfigureAwait(false))
        {
            if (read.Outcome != OpenReadOutcome.Found)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            await read.Content!.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        SourceIdentityMap map;
        try
        {
            var record = StandaloneRecordFraming.Parse(bytes);
            if (record.Header.ObjectType != ObjectType.SourceIdentityMap)
            {
                return null;
            }

            var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, record.KeyGeneration);
            try
            {
                if (!StandaloneRecordCipher.TryOpen(record, repositoryId, metadataKey, out var plaintext))
                {
                    return null;
                }

                map = SourceIdentityMapCodec.Decode(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(metadataKey);
            }
        }
        catch (Exception exception) when (exception is FormatException or ManifestValidationException)
        {
            return null;
        }

        if (!map.SnapshotId.Span.SequenceEqual(snapshotId.Span))
        {
            // The object under this key names a different snapshot. Nothing
            // in the format prevents that — the key is not covered by the
            // AEAD — so it is checked here rather than assumed.
            return null;
        }

        // Object identifiers are unique by the codec's own check, but two of
        // them may share a source key — a file captured, deleted, and
        // recreated at the same inode within one snapshot, or two paths of a
        // hardlink group. Keeping the first is arbitrary, and arbitrary is
        // the wrong answer for ancestry that a manifest records forever, so
        // an ambiguous key answers nothing at all.
        var resolved = new Dictionary<SourceKey, ObjectId>(map.Identities.Count);
        var seen = new HashSet<SourceKey>(map.Identities.Count);
        foreach (var identity in map.Identities)
        {
            var key = SourceKey.From(identity.SourceKey.Span);
            if (seen.Add(key))
            {
                resolved[key] = identity.ObjectId;
            }
            else
            {
                resolved.Remove(key);
            }
        }

        return new SourceIdentityLookup(resolved);
    }

    /// <summary>
    /// The file version captured from <paramref name="sourceKey"/>, or
    /// <see langword="null"/> when the map does not name it unambiguously.
    /// </summary>
    public ObjectId? Find(ReadOnlySpan<byte> sourceKey) =>
        _versions.TryGetValue(SourceKey.From(sourceKey), out var objectId) ? objectId : null;

    /// <summary>
    /// A 16-byte source key as a hashable value, so the lookup costs no
    /// allocation per file.
    /// </summary>
    private readonly record struct SourceKey(ulong High, ulong Low)
    {
        internal static SourceKey From(ReadOnlySpan<byte> value) => value.Length == SourceIdentityMapCodec.SourceKeyLength
            ? new SourceKey(
                BinaryPrimitives.ReadUInt64BigEndian(value),
                BinaryPrimitives.ReadUInt64BigEndian(value[8..]))
            : throw new ArgumentException(
                $"A source key is exactly {SourceIdentityMapCodec.SourceKeyLength} bytes.", nameof(value));
    }
}
