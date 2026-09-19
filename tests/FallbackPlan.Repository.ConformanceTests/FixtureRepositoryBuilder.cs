using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Writes a complete, tiny repository of a given format version: descriptor
/// with the sealing public key and its required features, <b>no key object
/// anywhere</b> (FR-WOR-001), one sealed data blob, one metadata blob, a
/// standalone snapshot, an index delta, and a two-record journal — the
/// shared-sequence layout of specification 08 §2.
/// </summary>
/// <remarks>
/// One generator, one identity per fixture. It exists parameterised rather
/// than copied because the only thing a format-3 fixture changes is the
/// version it asks for — and a copy would let the two drift while both
/// claimed to describe the same six-step sequence.
/// <para>
/// Nothing here is byte-reproducible and nothing is meant to be: a sealed
/// blob draws a fresh content key and a fresh ephemeral share per seal
/// (ADR-0042 §2), and a format-3 record draws a fresh nonce and share each
/// (ADR-0052 Amendment 1). What a committed fixture freezes is the READ
/// contract — those bytes were written once and every future reader must
/// keep opening them.
/// </para>
/// </remarks>
internal static class FixtureRepositoryBuilder
{
    /// <summary>What distinguishes one fixture from another; everything else is shared.</summary>
    /// <param name="Passphrase">The fixture passphrase, published in the specification.</param>
    /// <param name="Repo">The repository identifier.</param>
    /// <param name="Writer">The writer identifier.</param>
    /// <param name="DeviceId">The snapshot's device identifier.</param>
    /// <param name="BackupSetId">The snapshot's backup-set identifier.</param>
    /// <param name="SnapshotId">The snapshot identifier.</param>
    /// <param name="Kdf">The deliberately cheap fixture KDF cost.</param>
    /// <param name="KdfSalt">The descriptor's public salt.</param>
    /// <param name="FormatVersion">The repository format the descriptor declares and the blobs are written for.</param>
    /// <param name="SpoolName">A directory name under the temp root, so two fixtures never share a spool.</param>
    internal sealed record Identity(
        string Passphrase,
        RepositoryId Repo,
        WriterId Writer,
        byte[] DeviceId,
        byte[] BackupSetId,
        byte[] SnapshotId,
        Argon2Parameters Kdf,
        byte[] KdfSalt,
        ushort FormatVersion,
        string SpoolName);

    /// <summary>The 200 000-byte deterministic file: concatenated SHA-256(BE64(i)).</summary>
    internal static byte[] FileContent()
    {
        var content = new byte[200_000];
        var counter = new byte[8];
        var offset = 0;
        for (ulong index = 0; offset < content.Length; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(counter, index);
            var digest = SHA256.HashData(counter);
            var take = Math.Min(digest.Length, content.Length - offset);
            digest.AsSpan(0, take).CopyTo(content.AsSpan(offset));
            offset += take;
        }

        return content;
    }

    internal const ulong CreatedAt = 1_722_600_000_000;

    internal static byte[] Salt(byte fill) => [.. Enumerable.Repeat(fill, 32)];

    /// <summary>The full authority a fixture's passphrase derives.</summary>
    internal static RepositoryReadAuthority DeriveAuthority(Identity identity)
    {
        using var passphrase = Crypto.Passphrase.Create(identity.Passphrase);
        return WriteOnlyDerivation.Derive(passphrase, identity.Kdf, identity.KdfSalt, KdfValidationMode.OpenRepository);
    }

    /// <summary>The required features a descriptor of this version declares.</summary>
    private static ushort[] RequiredFeaturesFor(ushort formatVersion) =>
        FormatVersions.HasRelocatableRecords(formatVersion)
            ?
            [
                RepositoryDescriptorCodec.FeatureSealedDataPlane,
                RepositoryDescriptorCodec.FeatureReclaimAuthority,
                RepositoryDescriptorCodec.FeatureRelocatableRecords,
            ]
            : [RepositoryDescriptorCodec.FeatureSealedDataPlane];

    /// <summary>Generates the complete fixture store under <paramref name="rootDirectory"/>.</summary>
    internal static async Task GenerateAsync(Identity identity, string rootDirectory, CancellationToken cancellationToken)
    {
        var store = new LocalFileSystemObjectStore(rootDirectory);
        using var authority = DeriveAuthority(identity);
        using var keys = RepositoryKeySet.FromWriteCredential(authority.Credential);
        using var credential = authority.Credential.Clone();
        using var objectIds = new ObjectIdDeriver(credential.ContentIdKey.ToArray());
        using var storeKeys = new StoreBlobKeyDeriver(credential.KeyIdKey.ToArray());
        var spool = Path.Combine(Path.GetTempPath(), identity.SpoolName, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(spool);

        try
        {
            // Descriptor only — a write-only repository stores no key object
            // (FR-WOR-001): the sealing public key in the descriptor is the
            // verifier, and the salt and parameters are what a restore
            // re-derives from.
            await PutAsync(store, ObjectKey.Parse("repository-format"),
                RepositoryDescriptorCodec.Serialize(new RepositoryDescriptor(
                    identity.Repo, identity.FormatVersion,
                    RequiredFeatures: RequiredFeaturesFor(identity.FormatVersion), OptionalFeatures: [],
                    identity.Kdf, identity.KdfSalt, CreatedAt, "fallbackplan-fixture/1.0", UnstableFormat: true,
                    authority.Credential.SealingPublicKey.ToArray())),
                cancellationToken).ConfigureAwait(false);

            // Shared-sequence layout (08 §2): 1 intent · 2 sealed data blob ·
            // 3 metadata blob · 4 standalone snapshot · 5 delta · 6 retirement.
            var dataBlobId = BlobId.FromWriterCounter(identity.Writer, 2);
            var metaBlobId = BlobId.FromWriterCounter(identity.Writer, 3);

            await PublishJournalAsync(store, identity, credential, objectIds, sequence: 1,
                new JournalPayload.WriteIntent(
                    identity.BackupSetId, [dataBlobId, metaBlobId], 3_600_000, 5, IntentPurpose.Backup),
                Salt(0xE5), cancellationToken).ConfigureAwait(false);

            // --- sealed data blob (counter 2): content sealed to the
            // repository public key — one key for the blob in format 2, one
            // per record in format 3 — with the footer, the structure plane,
            // under the METADATA class key in both (ADR-0042 §2).
            var content = FileContent();
            var references = new List<SegmentReference>();
            var entries = new List<IndexEntry>();

            var structureKey = keys.DeriveClassKey(BlobClass.Metadata, KeyGeneration.Zero);
            var dataWriter = BlobWriter.CreateSealed(
                identity.Repo, identity.Writer, KeyGeneration.Zero, structureKey, keys.SealingPublicKey, blobCounter: 2,
                EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, spool, Salt(0xE1),
                formatVersion: FormatVersions.ContainerVersion(identity.FormatVersion, dataClass: true));

            for (var offset = 0; offset < content.Length; offset += 64 * 1024)
            {
                var length = Math.Min(64 * 1024, content.Length - offset);
                var plaintext = content.AsMemory(offset, length);
                var objectId = objectIds.Derive(ObjectType.SegmentRecord, ContentHasher.Hash(plaintext.Span));

                await dataWriter.AppendRecordAsync(
                    ObjectType.SegmentRecord, objectId, CompressionProfile.None, (ulong)length, plaintext, cancellationToken)
                    .ConfigureAwait(false);
                references.Add(new SegmentReference(offset, length, objectId));
            }

            await SealAndUploadAsync(store, storeKeys, dataWriter, entries, cancellationToken).ConfigureAwait(false);

            // --- metadata blob (counter 3): the structure plane, which is
            // what keeps the hub able to browse and plan without content.
            var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, KeyGeneration.Zero);
            var metaWriter = BlobWriter.Create(
                identity.Repo, identity.Writer, KeyGeneration.Zero, BlobClass.Metadata, metadataKey, blobCounter: 3,
                EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, spool, Salt(0xE2),
                formatVersion: FormatVersions.ContainerVersion(identity.FormatVersion, dataClass: false));

            var fileVersion = new FileVersionManifest
            {
                EntryKind = EntryKind.File,
                Name = "fixture.bin"u8.ToArray(),
                NameNormalisation = NameNormalisation.Nfc,
                LogicalLength = (ulong)content.Length,
                SegmentReferences = references,
                WholeFileHash = SHA256.HashData(content),
                SegmentationProfile = SegmentationProfile.FixedV1.Value,
                Metadata = new EntryMetadata { ModifiedAt = CreatedAt },
            };
            var fileVersionId = await AppendManifestAsync(
                metaWriter, objectIds, ObjectType.FileVersionManifest,
                FileVersionManifestCodec.Encode(fileVersion), cancellationToken).ConfigureAwait(false);

            var tree = new TreeManifest
            {
                Entries = [new TreeEntry("fixture.bin"u8.ToArray(), fileVersionId, EntryKind.File)],
                Name = "/"u8.ToArray(),
                NameNormalisation = NameNormalisation.Nfc,
                Metadata = EntryMetadata.Empty,
            };
            var treeId = await AppendManifestAsync(
                metaWriter, objectIds, ObjectType.TreeManifest,
                TreeManifestCodec.Encode(tree), cancellationToken).ConfigureAwait(false);

            var policy = new PolicyManifest
            {
                SegmentationProfile = SegmentationProfile.FixedV1.Value,
                SegmentSizeOrTarget = 64 * 1024,
                CompressionProfile = CompressionProfile.None.Value,
                CompressionThresholdPermille = 0,
                EncryptionProfile = EncryptionProfile.Aes256GcmV1.Value,
                BlobTargetSize = (ulong)BlobWriteProfile.LocalDefault.TargetSizeBytes,
                BlobMaxSize = (ulong)BlobWriteProfile.LocalDefault.MaximumSizeBytes,
                BlobMaxRecordCount = (uint)BlobWriteProfile.LocalDefault.MaximumRecordCount,
                DedupTrustDomain = 1,
            };
            var policyId = await AppendManifestAsync(
                metaWriter, objectIds, ObjectType.PolicyManifest,
                PolicyManifestCodec.Encode(policy), cancellationToken).ConfigureAwait(false);

            var snapshot = new SnapshotManifest
            {
                SnapshotId = identity.SnapshotId,
                DeviceId = identity.DeviceId,
                BackupSetId = identity.BackupSetId,
                CaptureStartedAt = CreatedAt,
                CaptureCompletedAt = CreatedAt + 1_000,
                RootTree = treeId,
                PolicyManifest = policyId,
                ConsistencyMethod = 1,
                CaptureStatus = 1,
                SourceFilesystem = new SourceFilesystem(true, true, "fixture"),
                PublicationGeneration = 0,
                ClientVersion = "fallbackplan-fixture/1.0",
            };
            byte[] encodedSnapshot;
            using (var signer = RepositorySigner.Create(credential, KeyGeneration.Zero))
            {
                encodedSnapshot = SnapshotManifestCodec.Encode(
                    snapshot, signer.Sign(SnapshotManifestCodec.EncodeForSigning(snapshot)));
            }

            var snapshotObjectId = await AppendManifestAsync(
                metaWriter, objectIds, ObjectType.SnapshotManifest, encodedSnapshot, cancellationToken)
                .ConfigureAwait(false);

            await SealAndUploadAsync(store, storeKeys, metaWriter, entries, cancellationToken).ConfigureAwait(false);

            // --- standalone snapshot (counter 4). Format 1 in every
            // repository, format 3 included: a standalone record is never in
            // a blob and never relocated (ADR-0052 Amendment 1).
            await PutAsync(store, MetadataStoreKeys.Snapshot(identity.DeviceId, identity.BackupSetId, identity.SnapshotId),
                StandaloneRecordCipher.Seal(
                    identity.Repo, metadataKey, KeyGeneration.Zero, identity.Writer, counter: 4,
                    ObjectType.SnapshotManifest, snapshotObjectId, encodedSnapshot, Salt(0xE3)),
                cancellationToken).ConfigureAwait(false);

            // --- index delta (sequence 5) ---------------------------------
            var deltaId = DeltaId.FromBytes(Enumerable.Repeat((byte)0xDB, 16).ToArray());
            var delta = new IndexDelta
            {
                WriterId = identity.Writer,
                Sequence = 5,
                Generation = 0,
                CoveredBlobIds = [dataBlobId, metaBlobId],
                Entries = entries,
            };
            byte[] storedDelta;
            using (var signer = RepositorySigner.Create(credential, KeyGeneration.Zero))
            {
                storedDelta = IndexDeltaCodec.Encode(delta, signer.Sign(IndexDeltaCodec.EncodeForSigning(delta)));
            }

            await PutAsync(store, MetadataStoreKeys.IndexDelta(0, deltaId),
                StandaloneRecordCipher.Seal(
                    identity.Repo, metadataKey, KeyGeneration.Zero, identity.Writer, counter: 5,
                    ObjectType.IndexDelta, objectIds.Derive(ObjectType.IndexDelta, ContentHasher.Hash(storedDelta)),
                    storedDelta, Salt(0xE4)),
                cancellationToken).ConfigureAwait(false);

            // --- intent retirement (sequence 6) ---------------------------
            await PublishJournalAsync(store, identity, credential, objectIds, sequence: 6,
                new JournalPayload.IntentRetirement(1, IntentOutcome.Completed),
                Salt(0xE6), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(spool))
            {
                Directory.Delete(spool, recursive: true);
            }
        }
    }

    private static async Task PublishJournalAsync(
        LocalFileSystemObjectStore store,
        Identity identity,
        RepositoryWriteCredential credential,
        ObjectIdDeriver objectIds,
        ulong sequence,
        JournalPayload payload,
        byte[] blobSalt,
        CancellationToken cancellationToken)
    {
        var kind = payload switch
        {
            JournalPayload.WriteIntent => JournalRecordKind.WriteIntent,
            JournalPayload.IntentExtension => JournalRecordKind.IntentExtension,
            JournalPayload.IntentRetirement => JournalRecordKind.IntentRetirement,
            _ => JournalRecordKind.Audit,
        };
        var record = new JournalRecord(kind, identity.Writer, sequence, CreatedAt, payload);

        byte[] stored;
        using (var signer = RepositorySigner.Create(credential, KeyGeneration.Zero))
        {
            stored = JournalRecordCodec.Encode(record, signer.Sign(JournalRecordCodec.EncodeForSigning(record)));
        }

        var metadataKey = credential.DeriveMetadataKey(KeyGeneration.Zero);
        try
        {
            await PutAsync(store, MetadataStoreKeys.Journal(identity.Writer, sequence),
                StandaloneRecordCipher.Seal(
                    identity.Repo, metadataKey, KeyGeneration.Zero, identity.Writer, sequence,
                    ObjectType.JournalRecord, objectIds.Derive(ObjectType.JournalRecord, ContentHasher.Hash(stored)),
                    stored, blobSalt),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(metadataKey);
        }
    }

    private static async ValueTask<ObjectId> AppendManifestAsync(
        BlobWriter writer,
        ObjectIdDeriver objectIds,
        ObjectType objectType,
        byte[] encoded,
        CancellationToken cancellationToken)
    {
        var objectId = objectIds.Derive(objectType, ContentHasher.Hash(encoded));
        await writer.AppendRecordAsync(objectType, objectId, CompressionProfile.None, (ulong)encoded.Length, encoded, cancellationToken)
            .ConfigureAwait(false);
        return objectId;
    }

    private static async Task SealAndUploadAsync(
        LocalFileSystemObjectStore store,
        StoreBlobKeyDeriver storeKeys,
        BlobWriter writer,
        List<IndexEntry> entries,
        CancellationToken cancellationToken)
    {
        var sealedBlob = await writer.SealAsync(cancellationToken).ConfigureAwait(false);
        await using (sealedBlob.ConfigureAwait(false))
        {
            foreach (var entry in sealedBlob.RecordTable)
            {
                entries.Add(new IndexEntry(
                    entry.ObjectId, sealedBlob.BlobId, entry.PhysicalOffset, entry.StoredLength,
                    entry.CompressionProfileValue, entry.EncryptionProfileValue, IndexEntryType.Insertion));
            }

            var storeKey = BlobStoreKeys.ForBlob(sealedBlob.BlobClass, storeKeys.Derive(sealedBlob.BlobId));
            var result = await store.PutAsync(
                storeKey, sealedBlob.OpenContentAsync, PutConditions.IfNotExists, cancellationToken).ConfigureAwait(false);

            if (result.Outcome != PutOutcome.Created)
            {
                throw new InvalidOperationException($"Fixture blob put returned {result.Outcome}.");
            }
        }
    }

    private static async Task PutAsync(
        LocalFileSystemObjectStore store, ObjectKey key, byte[] bytes, CancellationToken cancellationToken)
    {
        var result = await store.PutAsync(
            key,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes)),
            PutConditions.IfNotExists,
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome != PutOutcome.Created)
        {
            throw new InvalidOperationException($"Fixture put of {key.Value} returned {result.Outcome}.");
        }
    }
}
