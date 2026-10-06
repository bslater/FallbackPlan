using Bodu;
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
/// The source-identity packs one device published at or before a bound, read
/// into memory (specification 06 §11.5): which file version each source file
/// last had as of a prior snapshot.
/// </summary>
/// <remarks>
/// <para>
/// A pack names only the versions its publication created, so the version a
/// file had at a bound can be in any pack at or before it — a file untouched
/// since the first backup is named in the first pack and nowhere else. A
/// reader therefore takes every pack up to the bound, in capture order, and a
/// later entry for a source key replaces an earlier one: one listing and one
/// read a pack, however many files ask.
/// </para>
/// <para>
/// Read only in the window a catalogue rebuild opens, where the catalogue has
/// paths but no identities (02 §2). A pack that cannot be read, fails to
/// authenticate, fails to parse or disagrees with the key it was found under
/// is passed over: like the per-file hint, a pack is never evidence, and its
/// absence costs a rename's ancestry, never a backup.
/// </para>
/// </remarks>
public sealed class SourceIdentityPackIndex
{
    private readonly Dictionary<SourceKey, ObjectId> _versions;

    private SourceIdentityPackIndex(Dictionary<SourceKey, ObjectId> versions, int packsRead)
    {
        _versions = versions;
        PacksRead = packsRead;
    }

    /// <summary>How many packs were read and believed.</summary>
    public int PacksRead { get; }

    /// <summary>How many source keys the packs answer for.</summary>
    public int Count => _versions.Count;

    /// <summary>
    /// The version captured from <paramref name="sourceKey"/> most recently
    /// at or before the bound the index was loaded at, or
    /// <see langword="null"/> when no pack names it.
    /// </summary>
    public ObjectId? Find(ReadOnlySpan<byte> sourceKey) =>
        sourceKey.Length == SourceIdentityHint.SourceKeyLength
        && _versions.TryGetValue(SourceKey.From(sourceKey), out var version)
            ? version
            : null;

    /// <summary>
    /// Reads every pack <paramref name="deviceId"/> published at or before
    /// <paramref name="capturedAtBound"/>.
    /// </summary>
    /// <param name="store">The repository store.</param>
    /// <param name="repositoryId">The repository the records bind to.</param>
    /// <param name="keys">The repository key set.</param>
    /// <param name="deviceId">The device whose source keys are being asked about, 16 bytes.</param>
    /// <param name="capturedAtBound">
    /// The prior snapshot's capture time. A pack after it describes versions
    /// that snapshot did not contain, so it is not read.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async ValueTask<SourceIdentityPackIndex> LoadAsync(
        IObjectStore store,
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        ReadOnlyMemory<byte> deviceId,
        ulong capturedAtBound,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(keys);

        var prefix = MetadataStoreKeys.SourceIdentityPackPrefix(deviceId.Span);
        var bound = MetadataStoreKeys.Decimal16(capturedAtBound);
        var versions = new Dictionary<SourceKey, ObjectId>();
        var packsRead = 0;

        // Keys under one device sort by capture time, so the packs arrive in
        // the order their entries must be applied and the listing can stop at
        // the first one past the bound.
        await foreach (var entry in store.ListAsync(prefix, ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            var remainder = entry.Key.Value.AsSpan(prefix.Value.Length);
            if (remainder.Length <= MetadataStoreKeys.Decimal16Length
                || remainder[MetadataStoreKeys.Decimal16Length] != '/')
            {
                continue;
            }

            if (remainder[..MetadataStoreKeys.Decimal16Length].CompareTo(bound, StringComparison.Ordinal) > 0)
            {
                break;
            }

            if (await ReadAsync(store, repositoryId, keys, entry.Key, deviceId, cancellationToken)
                    .ConfigureAwait(false) is not { } pack)
            {
                continue;
            }

            packsRead++;
            foreach (var packed in pack.Entries)
            {
                versions[SourceKey.From(packed.SourceKey.Span)] = packed.ObjectId;
            }
        }

        return new SourceIdentityPackIndex(versions, packsRead);
    }

    private static async ValueTask<SourceIdentityPack?> ReadAsync(
        IObjectStore store,
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        ObjectKey key,
        ReadOnlyMemory<byte> deviceId,
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
            if (record.Header.ObjectType != ObjectType.SourceIdentityPack)
            {
                return null;
            }

            var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, record.KeyGeneration);
            SourceIdentityPack pack;
            try
            {
                if (!StandaloneRecordCipher.TryOpen(record, repositoryId, metadataKey, out var plaintext))
                {
                    return null;
                }

                pack = SourceIdentityPackCodec.Decode(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(metadataKey);
            }

            // The store key is not covered by the AEAD, so the device, time,
            // snapshot and part it promises are claims only the body
            // authenticates. They must agree, or a pack moved to an earlier
            // time would answer a bound its own body postdates.
            return pack.DeviceId.Span.SequenceEqual(deviceId.Span)
                && string.Equals(
                    MetadataStoreKeys.SourceIdentityPack(
                        pack.DeviceId.Span, pack.CapturedAt, pack.SnapshotId.Span, pack.Part).Value,
                    key.Value,
                    StringComparison.Ordinal)
                ? pack
                : null;
        }
        catch (Exception exception) when (exception is FormatException or ManifestValidationException)
        {
            return null;
        }
    }
}
