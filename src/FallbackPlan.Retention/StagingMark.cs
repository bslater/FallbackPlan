using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Retention;

/// <summary>One snapshot as the store holds it — the marker's front door.</summary>
/// <param name="Fact">The planner's view: identity, capture time, generation.</param>
/// <param name="Manifest">The decoded manifest, for the closure walk.</param>
/// <param name="StoreKey">Where the standalone snapshot object lives — the key an expiry deletes.</param>
/// <param name="ManifestObjectId">The manifest's derived object identity — what a tombstone for the snapshot names (spec 11 §3).</param>
public sealed record SurveyedSnapshot(
    SnapshotFact Fact, SnapshotManifest Manifest, ObjectKey StoreKey, ObjectId ManifestObjectId);

/// <summary>What the snapshot survey found, damage included.</summary>
/// <param name="Snapshots">Every snapshot that decoded, newest first.</param>
/// <param name="Undecodable">
/// Snapshot objects that would not parse or authenticate. Their existence
/// vetoes nothing by itself, but a collector MUST treat an undecodable
/// snapshot as protecting everything it might reference — which is to say a
/// pass with any of these performs no deletion at all.
/// </param>
public sealed record SnapshotSurvey(
    IReadOnlyList<SurveyedSnapshot> Snapshots, IReadOnlyList<string> Undecodable);

/// <summary>
/// The mark half of collection (architecture 07 §3 steps 2–3): enumerate the
/// snapshots the store itself holds — the catalogue is a cache and is never
/// the authority a deletion hangs off — and walk each protected snapshot's
/// full closure into a reachable-object set. The walk covers what the
/// forensic rebuilder's walk covers plus the pieces it never needed: the
/// policy manifest and the error manifest.
/// </summary>
public static class StagingMark
{
    /// <summary>Enumerates and decodes every standalone snapshot object in the store.</summary>
    /// <param name="store">The staging archive's object store.</param>
    /// <param name="repository">The opened archive, for keys and identity.</param>
    /// <param name="cancellationToken">Cancels the survey.</param>
    /// <returns>The survey, newest first.</returns>
    public static async ValueTask<SnapshotSurvey> SurveyAsync(
        IObjectStore store, OpenedRepository repository, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(repository);

        var snapshots = new List<SurveyedSnapshot>();
        var undecodable = new List<string>();
        var contentIdKey = repository.Hierarchy.DeriveContentIdKey();
        var deriver = new FallbackPlan.Repository.Crypto.ObjectIdDeriver(contentIdKey);

        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("snapshots/"), ListOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            byte[] bytes;
            using (var read = await store.OpenReadAsync(entry.Key, range: null, cancellationToken).ConfigureAwait(false))
            {
                if (read.Outcome != OpenReadOutcome.Found)
                {
                    continue;
                }

                using var memory = new MemoryStream();
                await read.Content!.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                bytes = memory.ToArray();
            }

            try
            {
                var record = StandaloneRecordFraming.Parse(bytes);
                var metadataKey = repository.Hierarchy.DeriveMetadataKey(record.KeyGeneration);
                try
                {
                    if (!StandaloneRecordCipher.TryOpen(record, repository.RepositoryId, metadataKey, out var plaintext))
                    {
                        undecodable.Add($"{entry.Key}: failed authentication");
                        continue;
                    }

                    var decoded = SnapshotManifestCodec.Decode(plaintext);
                    snapshots.Add(new SurveyedSnapshot(
                        new SnapshotFact(
                            Convert.ToHexStringLower(decoded.Manifest.SnapshotId.Span),
                            decoded.Manifest.CaptureCompletedAt,
                            decoded.Manifest.PublicationGeneration),
                        decoded.Manifest,
                        entry.Key,
                        deriver.Derive(
                            FallbackPlan.Domain.ObjectType.SnapshotManifest,
                            FallbackPlan.Repository.Crypto.ContentHasher.Hash(
                                SnapshotManifestCodec.Encode(decoded.Manifest, decoded.Signature.Span)))));
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(metadataKey);
                }
            }
            catch (FormatException exception)
            {
                undecodable.Add($"{entry.Key}: {exception.Message}");
            }
        }

        return new SnapshotSurvey(
            [.. snapshots
                .OrderByDescending(snapshot => snapshot.Fact.CapturedAtUnixMilliseconds)
                .ThenBy(snapshot => snapshot.Fact.SnapshotId, StringComparer.Ordinal)],
            undecodable);
    }

    /// <summary>
    /// Walks the protected snapshots' full closure into a reachable-object
    /// set: root tree → subtrees and continuations → file versions →
    /// segments and alternate streams, plus each
    /// snapshot's policy and error manifests.
    /// </summary>
    /// <param name="reader">The footer-truth reader, blobs already loaded.</param>
    /// <param name="protectedSnapshots">The keep-set's snapshots.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every reachable object identifier, or null in <c>Unwalkable</c> entries when a manifest would not read.</returns>
    public static async ValueTask<(HashSet<ObjectId> Reachable, IReadOnlyList<string> Unwalkable)> MarkAsync(
        RepositoryReader reader,
        IReadOnlyList<SurveyedSnapshot> protectedSnapshots,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(reader);
        ThrowHelper.ThrowIfNull(protectedSnapshots);

        var reachable = new HashSet<ObjectId>();
        var unwalkable = new List<string>();

        foreach (var snapshot in protectedSnapshots)
        {
            reachable.Add(snapshot.Manifest.PolicyManifest);
            if (snapshot.Manifest.ErrorManifest is { } error)
            {
                reachable.Add(error);
            }

            await WalkTreeAsync(reader, snapshot.Manifest.RootTree, reachable, unwalkable, cancellationToken)
                .ConfigureAwait(false);
        }

        return (reachable, unwalkable);
    }

    private static async ValueTask WalkTreeAsync(
        RepositoryReader reader,
        ObjectId treeId,
        HashSet<ObjectId> reachable,
        List<string> unwalkable,
        CancellationToken cancellationToken)
    {
        if (!reachable.Add(treeId))
        {
            return;
        }

        var tree = await ReadAsync(reader, treeId, TreeManifestCodec.Decode, unwalkable, cancellationToken)
            .ConfigureAwait(false);
        if (tree is null)
        {
            return;
        }

        foreach (var entry in tree.Entries)
        {
            if (entry.EntryKind == EntryKind.DirectoryPlaceholder)
            {
                await WalkTreeAsync(reader, entry.ObjectId, reachable, unwalkable, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            await WalkFileVersionAsync(reader, entry.ObjectId, reachable, unwalkable, cancellationToken)
                .ConfigureAwait(false);
        }

        if (tree.Continuation is { } continuation)
        {
            await WalkTreeAsync(reader, continuation, reachable, unwalkable, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask WalkFileVersionAsync(
        RepositoryReader reader,
        ObjectId versionId,
        HashSet<ObjectId> reachable,
        List<string> unwalkable,
        CancellationToken cancellationToken)
    {
        if (!reachable.Add(versionId))
        {
            return;
        }

        var manifest = await ReadAsync(reader, versionId, FileVersionManifestCodec.Decode, unwalkable, cancellationToken)
            .ConfigureAwait(false);
        if (manifest is null)
        {
            return;
        }

        foreach (var reference in manifest.SegmentReferences)
        {
            reachable.Add(reference.ObjectId);
        }

        foreach (var stream in manifest.Metadata.AlternateStreams)
        {
            reachable.Add(stream.ObjectId);
        }

        // ParentVersion is deliberately NOT walked. It is lineage, not a
        // restore dependency — nothing dereferences it to read content — and
        // following it would chain every kept version back through every
        // expired one, pinning the whole history forever and making
        // retention free nothing. A kept manifest may therefore name a
        // parent that no longer exists; that dangling lineage is accepted,
        // exactly as the snapshot's own ParentSnapshots may name expired
        // snapshots. Deleted-file history (architecture 07 §2's separately
        // configured duration) will bound-walk parents when it is built.
    }

    private static async ValueTask<T?> ReadAsync<T>(
        RepositoryReader reader,
        ObjectId objectId,
        Func<ReadOnlyMemory<byte>, T> decode,
        List<string> unwalkable,
        CancellationToken cancellationToken)
        where T : class
    {
        var result = await reader.ReadSegmentAsync(objectId, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != RecordReadOutcome.Ok || result.Plaintext is null)
        {
            unwalkable.Add($"{objectId}: {result.Detail ?? result.Outcome.ToString()}");
            return null;
        }

        try
        {
            return decode(result.Plaintext);
        }
        catch (FormatException exception)
        {
            unwalkable.Add($"{objectId}: {exception.Message}");
            return null;
        }
    }
}
