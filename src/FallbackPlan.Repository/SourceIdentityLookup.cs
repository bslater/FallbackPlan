using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Repository.Resources;

namespace FallbackPlan.Repository;

/// <summary>
/// The read side of the source-identity hints (specification 06 §11): given
/// one source file's keyed identity, which file version was captured from
/// it.
/// </summary>
/// <remarks>
/// <para>
/// This is the durable half of rename detection. The catalogue answers the
/// same question faster and is consulted first, but it is a disposable cache
/// — after a rebuild it holds no identities at all, and a rename captured in
/// that window would record no <c>parent_version</c>, severing the file's
/// history permanently. The hints cost one prefix listing and one fetch,
/// and only in that window.
/// </para>
/// <para>
/// It yields <b>ancestry only</b>, never the NFR-PERF-003 content
/// short-circuit: a hint carries no size and no modification time, and an
/// inode is reused after its file is deleted, so a match here establishes a
/// likely lineage and nothing about the bytes (06 §11.3).
/// </para>
/// </remarks>
public static class SourceIdentityLookup
{
    /// <summary>
    /// The version captured from <paramref name="sourceKey"/> most recently
    /// at or before <paramref name="capturedAtBound"/>, or
    /// <see langword="null"/> when no hint answers.
    /// </summary>
    /// <param name="store">The repository store.</param>
    /// <param name="repositoryId">The repository the records bind to.</param>
    /// <param name="keys">The repository key set.</param>
    /// <param name="sourceKey">The 16-byte keyed source identity.</param>
    /// <param name="capturedAtBound">
    /// The prior snapshot's capture time. Entries after it describe versions
    /// the prior snapshot did not contain, so they are not its ancestor.
    /// </param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <remarks>
    /// Absence is ordinary — a writer that published no hint, a store that
    /// refused the put, an implementation that writes none — and so is an
    /// object that fails to authenticate, fails to parse, or disagrees with
    /// the key it was found under. A hint is never evidence, so nothing here
    /// is a damage finding; the caller falls back to matching by path.
    /// </remarks>
    public static async ValueTask<ObjectId?> FindAsync(
        IObjectStore store,
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        ReadOnlyMemory<byte> sourceKey,
        ulong capturedAtBound,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(keys);

        var prefix = MetadataStoreKeys.SourceIdentityPrefix(sourceKey.Span);
        var bound = MetadataStoreKeys.Decimal16(capturedAtBound);

        // Keys under one source key sort chronologically by construction, so
        // the last one at or before the bound is the answer and the scan can
        // stop as soon as it passes the bound.
        ObjectKey? best = null;
        await foreach (var entry in store.ListAsync(prefix, ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            if (CapturedAtComponent(entry.Key, prefix.Value) is not { } capturedAt)
            {
                continue;
            }

            if (string.CompareOrdinal(capturedAt, bound) > 0)
            {
                break;
            }

            best = entry.Key;
        }

        return best is { } key
            ? await ReadAsync(store, repositoryId, keys, key, sourceKey, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// The <c>&lt;captured-at&gt;</c> component of a hint key, or
    /// <see langword="null"/> when the key is not shaped like one.
    /// </summary>
    private static string? CapturedAtComponent(ObjectKey key, string prefix)
    {
        var remainder = key.Value.AsSpan(prefix.Length);
        var separator = remainder.IndexOf('/');

        return separator == MetadataStoreKeys.Decimal16Length
            ? remainder[..separator].ToString()
            : null;
    }

    private static async ValueTask<ObjectId?> ReadAsync(
        IObjectStore store,
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        ObjectKey key,
        ReadOnlyMemory<byte> expectedSourceKey,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        using (var read = await store.OpenReadAsync(key, range: null, cancellationToken).ConfigureAwait(false))
        {
            if (read.Outcome != OpenReadOutcome.Found)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            await read.Content!.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        try
        {
            var record = StandaloneRecordFraming.Parse(bytes);
            if (record.Header.ObjectType != ObjectType.SourceIdentityHint)
            {
                return null;
            }

            var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, record.KeyGeneration);
            SourceIdentityHint hint;
            try
            {
                if (!StandaloneRecordCipher.TryOpen(record, repositoryId, metadataKey, out var plaintext))
                {
                    return null;
                }

                hint = SourceIdentityHintCodec.Decode(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(metadataKey);
            }

            // The store key is not covered by the AEAD, so what the key
            // promised and what the object says are two claims and only the
            // second is authenticated. They must agree.
            return hint.SourceKey.Span.SequenceEqual(expectedSourceKey.Span)
                && string.Equals(
                    MetadataStoreKeys.SourceIdentityHint(
                        hint.SourceKey.Span, hint.CapturedAt, hint.SnapshotId.Span).Value,
                    key.Value,
                    StringComparison.Ordinal)
                ? hint.ObjectId
                : null;
        }
        catch (Exception exception) when (exception is FormatException or ManifestValidationException)
        {
            return null;
        }
    }
}

/// <summary>
/// A 16-byte source key as a hashable value, so keying a dictionary on one
/// costs no allocation.
/// </summary>
internal readonly record struct SourceKey(ulong High, ulong Low)
{
    internal static SourceKey From(ReadOnlySpan<byte> value) =>
        value.Length == SourceIdentityHint.SourceKeyLength
            ? new SourceKey(
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(value),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(value[8..]))
            : throw new ArgumentException(Strings.Formatstruct_SourceKeyExactlyBytes(SourceIdentityHint.SourceKeyLength), nameof(value));
}

/// <summary>
/// One publication's view of the source-identity hints: the store, the keys,
/// the capture time to bound the answer at, and a memo so two files that
/// somehow ask the same question pay for one round trip.
/// </summary>
/// <remarks>
/// Constructed only when the catalogue cannot answer identity questions for
/// the prior snapshot, which is the window a rebuild opens. On the warm path
/// nothing here is built and nothing here is called.
/// </remarks>
internal sealed class HintSource(
    IObjectStore store, RepositoryId repositoryId, RepositoryKeySet keys, ulong capturedAtBound)
{
    private readonly Dictionary<SourceKey, ObjectId?> _answered = [];

    public async ValueTask<ObjectId?> FindAsync(
        ReadOnlyMemory<byte> sourceKey, CancellationToken cancellationToken)
    {
        var memo = SourceKey.From(sourceKey.Span);
        if (_answered.TryGetValue(memo, out var cached))
        {
            return cached;
        }

        var found = await SourceIdentityLookup
            .FindAsync(store, repositoryId, keys, sourceKey, capturedAtBound, cancellationToken)
            .ConfigureAwait(false);

        _answered[memo] = found;
        return found;
    }
}
