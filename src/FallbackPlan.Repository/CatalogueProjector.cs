using Bodu;
using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>
/// Rebuilds the catalogue's manifest plane — snapshots, tree paths, file
/// versions, and what each version and snapshot is made of — from the
/// repository itself, so <c>ls</c> works after any rebuild (E1; FR-MAN-006)
/// and damage can still be traced to what needs it (FR-VER-005). Identity
/// columns stay null: they are scan-time local facts the repository never
/// stores (02 §2), which disables the NFR-PERF-003 short-circuit until the
/// next backup relearns them — conservative, never wrong.
/// </summary>
public sealed class CatalogueProjector
{
    /// <summary>What one projection pass recorded.</summary>
    /// <param name="Snapshots">Snapshots projected.</param>
    /// <param name="TreeEntries">Paths projected.</param>
    /// <param name="FileVersions">File versions projected.</param>
    /// <param name="Missing">
    /// Records the walk needed that no blob the reader could open carries — a
    /// direct-ship set's destination away, not damage. A projection with any
    /// left out what it could not see, and one run once they can be read
    /// fills them in; damage, which another run would only meet again, is a
    /// finding instead.
    /// </param>
    public sealed record ProjectionReport(int Snapshots, int TreeEntries, int FileVersions, int Missing = 0);

    /// <summary>
    /// Walks every discoverable snapshot through <paramref name="reader"/>
    /// (already loaded) into <paramref name="target"/>.
    /// </summary>
    public static async ValueTask<ProjectionReport> ProjectAsync(
        Catalogue.Catalogue target,
        RepositoryReader reader,
        IObjectStore store,
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        RepositoryWriteCredential credential,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(target);
        ThrowHelper.ThrowIfNull(reader);
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(keys);
        ThrowHelper.ThrowIfNull(credential);

        using var objectIdDeriver = new ObjectIdDeriver(keys.ContentIdKey);
        var snapshots = 0;
        var treeEntries = 0;
        var fileVersions = 0;
        var missing = 0;

        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("snapshots/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
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

            var record = StandaloneRecordFraming.Parse(bytes);
            var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, record.KeyGeneration);
            DecodedSnapshot decoded;
            try
            {
                if (!StandaloneRecordCipher.TryOpen(record, repositoryId, metadataKey, out var plaintext))
                {
                    target.RecordFinding(new DamageFinding(
                        DamageKind.CorruptRecord, $"Snapshot object '{entry.Key}' failed authentication."));
                    continue;
                }

                decoded = SnapshotManifestCodec.Decode(plaintext);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(metadataKey);
            }

            int signatureState;
            using (var signer = RepositorySigner.Create(
                credential, new KeyGeneration((uint)decoded.Manifest.PublicationGeneration)))
            {
                signatureState = signer.Verify(decoded.SignedBytes.Span, decoded.Signature.Span) ? 1 : 2;
            }

            var manifest = decoded.Manifest;
            target.RecordSnapshot(
                manifest.SnapshotId.Span,
                manifest.DeviceId.Span,
                manifest.BackupSetId.Span,
                record.Header.ObjectId,
                manifest.RootTree,
                manifest.PublicationGeneration,
                manifest.CaptureStatus,
                signatureState,
                manifest.CaptureCompletedAt,
                manifest.ConsistencyMethod);
            snapshots++;

            // What the snapshot is made of besides its files, for damage to be
            // traced back through (FR-VER-005): its manifest, its policy and
            // error manifests, and every tree the walk meets, read or not.
            List<ObjectId> structure = [record.Header.ObjectId, manifest.PolicyManifest];
            if (manifest.ErrorManifest is { } errors)
            {
                structure.Add(errors);
            }

            var (entries, versions, unseen) = await ProjectTreeAsync(
                target, reader, manifest.SnapshotId, manifest.RootTree, prefix: string.Empty, structure, cancellationToken)
                .ConfigureAwait(false);
            target.RecordSnapshotStructure(manifest.SnapshotId.Span, structure);
            treeEntries += entries;
            fileVersions += versions;
            missing += unseen;
        }

        return new ProjectionReport(snapshots, treeEntries, fileVersions, missing);
    }

    private static async ValueTask<(int Entries, int Versions, int Missing)> ProjectTreeAsync(
        Catalogue.Catalogue target,
        RepositoryReader reader,
        ReadOnlyMemory<byte> snapshotId,
        ObjectId treeId,
        string prefix,
        List<ObjectId> structure,
        CancellationToken cancellationToken)
    {
        var entries = 0;
        var versions = 0;
        var missing = 0;

        ObjectId? next = treeId;
        while (next is { } id)
        {
            structure.Add(id);
            var read = await reader.ReadSegmentAsync(id, cancellationToken).ConfigureAwait(false);
            if (read.Outcome != RecordReadOutcome.Ok)
            {
                target.RecordFinding(new DamageFinding(
                    DamageKind.MissingIndexObject, $"Tree manifest {id} did not read: {read.Outcome}."), id);
                return (entries, versions, missing + Unseen(reader, id));
            }

            var tree = TreeManifestCodec.Decode(read.Plaintext!);

            foreach (var child in tree.Entries)
            {
                var name = Encoding.UTF8.GetString(child.Name.Span);
                var path = prefix.Length == 0 ? name : prefix + "/" + name;

                target.RecordTreeEntry(snapshotId.Span, path, child.EntryKind, child.ObjectId);
                entries++;

                if (child.EntryKind == EntryKind.DirectoryPlaceholder)
                {
                    var (childEntries, childVersions, childMissing) = await ProjectTreeAsync(
                        target, reader, snapshotId, child.ObjectId, path, structure, cancellationToken).ConfigureAwait(false);
                    entries += childEntries;
                    versions += childVersions;
                    missing += childMissing;
                    continue;
                }

                var manifestRead = await reader.ReadSegmentAsync(child.ObjectId, cancellationToken).ConfigureAwait(false);
                if (manifestRead.Outcome != RecordReadOutcome.Ok)
                {
                    target.RecordFinding(new DamageFinding(
                        DamageKind.MissingIndexObject,
                        $"File version {child.ObjectId} did not read: {manifestRead.Outcome}."), child.ObjectId);
                    missing += Unseen(reader, child.ObjectId);
                    continue;
                }

                var version = FileVersionManifestCodec.Decode(manifestRead.Plaintext!);
                target.RecordFileVersion(
                    child.ObjectId,
                    version.Name.Span,
                    version.EntryKind,
                    version.LogicalLength,
                    version.WholeFileHash.Span,
                    version.ParentVersion,
                    version.SegmentReferences.Count,
                    version.Metadata.ModifiedAt,
                    hasAlternateStreams: version.Metadata.AlternateStreams.Count > 0,
                    metadataDigest: FileVersionManifestCodec.MetadataDigest(version.Metadata));
                target.RecordVersionContents(child.ObjectId, version.ContentObjects());
                versions++;
            }

            next = tree.Continuation;
        }

        return (entries, versions, missing);
    }

    /// <summary>One when no blob the reader opened carries the record, zero when one does and it failed there.</summary>
    private static int Unseen(RepositoryReader reader, ObjectId objectId) =>
        reader.TryLocateRecord(objectId, out _, out _) ? 0 : 1;
}
