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
/// versions — from the repository itself, so <c>ls</c> works after any
/// rebuild (E1; FR-MAN-006). Identity columns stay null: they are
/// scan-time local facts the repository never stores (02 §2), which
/// disables the NFR-PERF-003 short-circuit until the next backup relearns
/// them — conservative, never wrong.
/// </summary>
public sealed class CatalogueProjector
{
    /// <summary>What one projection pass recorded.</summary>
    public sealed record ProjectionReport(int Snapshots, int TreeEntries, int FileVersions);

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
        KeyHierarchy hierarchy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(hierarchy);

        using var objectIdDeriver = new ObjectIdDeriver(keys.ContentIdKey);
        var snapshots = 0;
        var treeEntries = 0;
        var fileVersions = 0;

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
                hierarchy, new KeyGeneration((uint)decoded.Manifest.PublicationGeneration)))
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
                manifest.CaptureCompletedAt);
            snapshots++;

            var (entries, versions) = await ProjectTreeAsync(
                target, reader, manifest.SnapshotId, manifest.RootTree, prefix: string.Empty, cancellationToken)
                .ConfigureAwait(false);
            treeEntries += entries;
            fileVersions += versions;
        }

        return new ProjectionReport(snapshots, treeEntries, fileVersions);
    }

    private static async ValueTask<(int Entries, int Versions)> ProjectTreeAsync(
        Catalogue.Catalogue target,
        RepositoryReader reader,
        ReadOnlyMemory<byte> snapshotId,
        ObjectId treeId,
        string prefix,
        CancellationToken cancellationToken)
    {
        var entries = 0;
        var versions = 0;

        ObjectId? next = treeId;
        while (next is { } id)
        {
            var read = await reader.ReadSegmentAsync(id, cancellationToken).ConfigureAwait(false);
            if (read.Outcome != RecordReadOutcome.Ok)
            {
                target.RecordFinding(new DamageFinding(
                    DamageKind.MissingIndexObject, $"Tree manifest {id} did not read: {read.Outcome}."), id);
                return (entries, versions);
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
                    var (childEntries, childVersions) = await ProjectTreeAsync(
                        target, reader, snapshotId, child.ObjectId, path, cancellationToken).ConfigureAwait(false);
                    entries += childEntries;
                    versions += childVersions;
                    continue;
                }

                var manifestRead = await reader.ReadSegmentAsync(child.ObjectId, cancellationToken).ConfigureAwait(false);
                if (manifestRead.Outcome != RecordReadOutcome.Ok)
                {
                    target.RecordFinding(new DamageFinding(
                        DamageKind.MissingIndexObject,
                        $"File version {child.ObjectId} did not read: {manifestRead.Outcome}."), child.ObjectId);
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
                    version.Metadata.ModifiedAt);
                versions++;
            }

            next = tree.Continuation;
        }

        return (entries, versions);
    }
}
