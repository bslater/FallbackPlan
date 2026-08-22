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
/// Generates <c>fixture-repository-v2</c> (ADR-0042; NFR-COMP-004): a
/// complete, tiny write-only repository — descriptor with the sealing public
/// key and the required <c>sealed-data-plane</c> feature, <b>no key object
/// anywhere</b>, one sealed data blob, one metadata blob, a standalone
/// snapshot, an index delta, and a two-record journal — built from the same
/// deterministic content as the v1 fixture.
/// </summary>
/// <remarks>
/// Unlike the v1 fixture, the committed copy is <b>not</b> byte-compared
/// against a regeneration: a sealed data blob takes a fresh random content
/// key and a fresh ephemeral X25519 share per seal, which is the design
/// (ADR-0042 §2), so no two generations share bytes. What the committed
/// fixture freezes is the READ contract — the bytes were written once and
/// every future reader must keep opening them: structure with the derived
/// write bundle alone, content only with the derived authority, and the
/// wrong passphrase refused by derive-and-compare.
/// </remarks>
public static class FixtureRepositoryV2
{
    public const string Passphrase = "fallbackplan-fixture-v2-passphrase";

    public static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("1112131415161718191a1b1c1d1e1f20"));

    public static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("b0b1b2b3b4b5b6b7b8b9babbbcbdbebf"));

    public static readonly byte[] DeviceId = [.. Enumerable.Repeat((byte)0x55, 16)];
    public static readonly byte[] BackupSetId = [.. Enumerable.Repeat((byte)0x66, 16)];
    public static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0x77, 16)];

    public static readonly Argon2Parameters FixtureKdf = new() { MemoryKiB = 8 * 1024, Iterations = 1, Parallelism = 1 };
    public static readonly byte[] KdfSalt = [.. Enumerable.Range(0x20, 16).Select(value => (byte)value)];
    private const ulong CreatedAt = 1_722_600_000_000;

    private static byte[] Salt(byte fill) => [.. Enumerable.Repeat(fill, 32)];

    /// <summary>The fixture passphrase as a disposable value.</summary>
    public static Passphrase CreatePassphrase() => Crypto.Passphrase.Create(Passphrase);

    /// <summary>The full authority the fixture passphrase derives.</summary>
    public static RepositoryReadAuthority DeriveAuthority()
    {
        using var passphrase = CreatePassphrase();
        return WriteOnlyDerivation.Derive(passphrase, FixtureKdf, KdfSalt, KdfValidationMode.OpenRepository);
    }

    /// <summary>Generates the complete fixture store under <paramref name="rootDirectory"/>.</summary>
    public static async Task GenerateAsync(string rootDirectory, CancellationToken cancellationToken)
    {
        var store = new LocalFileSystemObjectStore(rootDirectory);
        using var authority = DeriveAuthority();
        using var keys = RepositoryKeySet.FromWriteCredential(authority.Credential);
        using var hierarchy = KeyHierarchy.ForWriteOnly(authority.Credential);
        using var objectIds = new ObjectIdDeriver(hierarchy.DeriveContentIdKey());
        using var storeKeys = new StoreBlobKeyDeriver(hierarchy.DeriveKeyIdKey());
        var spool = Path.Combine(Path.GetTempPath(), "fbp-fixture-v2-spool", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(spool);

        try
        {
            // Descriptor only — a write-only repository stores no key object
            // (FR-WOR-001): the sealing public key in the descriptor is the
            // verifier, and the salt and parameters are what a restore
            // re-derives from.
            await PutAsync(store, ObjectKey.Parse("repository-format"),
                RepositoryDescriptorCodec.Serialize(new RepositoryDescriptor(
                    Repo, FormatLimits.SealedFormatVersion,
                    RequiredFeatures: [RepositoryDescriptorCodec.FeatureSealedDataPlane], OptionalFeatures: [],
                    FixtureKdf, KdfSalt, CreatedAt, "fallbackplan-fixture/1.0", UnstableFormat: true,
                    authority.Credential.SealingPublicKey.ToArray())),
                cancellationToken).ConfigureAwait(false);

            // Shared-sequence layout (08 §2), mirroring the v1 fixture:
            // 1 intent · 2 sealed data blob · 3 metadata blob · 4 standalone
            // snapshot · 5 delta · 6 retirement.
            var dataBlobId = BlobId.FromWriterCounter(Writer, 2);
            var metaBlobId = BlobId.FromWriterCounter(Writer, 3);

            await PublishJournalAsync(store, hierarchy, objectIds, sequence: 1,
                new JournalPayload.WriteIntent(BackupSetId, [dataBlobId, metaBlobId], 3_600_000, 5, IntentPurpose.Backup),
                Salt(0xE5), cancellationToken).ConfigureAwait(false);

            // --- sealed data blob (counter 2): records under a fresh random
            // content key sealed to the repository public key; the footer —
            // the structure plane — under the METADATA class key (ADR-0042 §2).
            var content = FixtureRepository.FileContent();
            var references = new List<SegmentReference>();
            var entries = new List<IndexEntry>();

            var structureKey = keys.DeriveClassKey(BlobClass.Metadata, KeyGeneration.Zero);
            var dataWriter = BlobWriter.CreateSealed(
                Repo, Writer, KeyGeneration.Zero, structureKey, keys.SealingPublicKey, blobCounter: 2,
                EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, spool, Salt(0xE1));

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

            // --- metadata blob (counter 3): the structure plane, unchanged
            // from v1 — which is what keeps the hub able to browse and plan.
            var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, KeyGeneration.Zero);
            var metaWriter = BlobWriter.Create(
                Repo, Writer, KeyGeneration.Zero, BlobClass.Metadata, metadataKey, blobCounter: 3,
                EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, spool, Salt(0xE2));

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
                SnapshotId = SnapshotId,
                DeviceId = DeviceId,
                BackupSetId = BackupSetId,
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
            using (var signer = RepositorySigner.Create(hierarchy, KeyGeneration.Zero))
            {
                encodedSnapshot = SnapshotManifestCodec.Encode(
                    snapshot, signer.Sign(SnapshotManifestCodec.EncodeForSigning(snapshot)));
            }

            var snapshotObjectId = await AppendManifestAsync(
                metaWriter, objectIds, ObjectType.SnapshotManifest, encodedSnapshot, cancellationToken)
                .ConfigureAwait(false);

            await SealAndUploadAsync(store, storeKeys, metaWriter, entries, cancellationToken).ConfigureAwait(false);

            // --- standalone snapshot (counter 4) --------------------------
            await PutAsync(store, MetadataStoreKeys.Snapshot(DeviceId, BackupSetId, SnapshotId),
                StandaloneRecordCipher.Seal(
                    Repo, metadataKey, KeyGeneration.Zero, Writer, counter: 4,
                    ObjectType.SnapshotManifest, snapshotObjectId, encodedSnapshot, Salt(0xE3)),
                cancellationToken).ConfigureAwait(false);

            // --- index delta (sequence 5) ---------------------------------
            var deltaId = DeltaId.FromBytes(Enumerable.Repeat((byte)0xDB, 16).ToArray());
            var delta = new IndexDelta
            {
                WriterId = Writer,
                Sequence = 5,
                Generation = 0,
                CoveredBlobIds = [dataBlobId, metaBlobId],
                Entries = entries,
            };
            byte[] storedDelta;
            using (var signer = RepositorySigner.Create(hierarchy, KeyGeneration.Zero))
            {
                storedDelta = IndexDeltaCodec.Encode(delta, signer.Sign(IndexDeltaCodec.EncodeForSigning(delta)));
            }

            await PutAsync(store, MetadataStoreKeys.IndexDelta(0, deltaId),
                StandaloneRecordCipher.Seal(
                    Repo, metadataKey, KeyGeneration.Zero, Writer, counter: 5,
                    ObjectType.IndexDelta, objectIds.Derive(ObjectType.IndexDelta, ContentHasher.Hash(storedDelta)),
                    storedDelta, Salt(0xE4)),
                cancellationToken).ConfigureAwait(false);

            // --- intent retirement (sequence 6) ---------------------------
            await PublishJournalAsync(store, hierarchy, objectIds, sequence: 6,
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
        KeyHierarchy hierarchy,
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
        var record = new JournalRecord(kind, Writer, sequence, CreatedAt, payload);

        byte[] stored;
        using (var signer = RepositorySigner.Create(hierarchy, KeyGeneration.Zero))
        {
            stored = JournalRecordCodec.Encode(record, signer.Sign(JournalRecordCodec.EncodeForSigning(record)));
        }

        var metadataKey = hierarchy.DeriveMetadataKey(KeyGeneration.Zero);
        try
        {
            await PutAsync(store, MetadataStoreKeys.Journal(Writer, sequence),
                StandaloneRecordCipher.Seal(
                    Repo, metadataKey, KeyGeneration.Zero, Writer, sequence,
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
