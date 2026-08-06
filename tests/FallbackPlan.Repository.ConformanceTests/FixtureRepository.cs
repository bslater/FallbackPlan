using System.Buffers.Binary;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Repository.Format.Keys;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Generates <c>fixture-repository-v1</c> (wave F7; NFR-COMP-004): a
/// complete, tiny, fully deterministic repository — descriptor, key object,
/// one data blob, one metadata blob, a standalone snapshot, an index delta,
/// and a two-record journal — built from the conformance constants with
/// every normally-random input (salts, identifiers, nonces, timestamps)
/// fixed. The committed copy under
/// <c>specifications/repository-format/conformance/fixtures/</c> freezes the
/// bytes; regeneration must reproduce them exactly, and any diff is a format
/// change that must be deliberate.
/// </summary>
/// <remarks>
/// Synthetic content only, and a fixture-only passphrase with deliberately
/// small KDF parameters (below creation minimums, allowed on open with a
/// warning) so the suite stays fast. Nothing here is a template for
/// production use.
/// </remarks>
public static class FixtureRepository
{
    public const string Passphrase = "fallbackplan-fixture-passphrase";

    public static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    public static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    public static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    public static readonly byte[] DeviceId = [.. Enumerable.Repeat((byte)0x22, 16)];
    public static readonly byte[] BackupSetId = [.. Enumerable.Repeat((byte)0x33, 16)];
    public static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0x44, 16)];

    private static readonly Argon2Parameters FixtureKdf = new() { MemoryKiB = 8 * 1024, Iterations = 1, Parallelism = 1 };
    private static readonly byte[] KdfSalt = [.. Enumerable.Range(0x10, 16).Select(value => (byte)value)];
    private static readonly KeyId FixtureKeyId = KeyId.FromBytes(Enumerable.Repeat((byte)0xEE, 16).ToArray());
    private static readonly byte[] WrapNonce = [.. Enumerable.Repeat((byte)0x77, 12)];
    private const ulong CreatedAt = 1_722_600_000_000;

    private static byte[] Salt(byte fill) => [.. Enumerable.Repeat(fill, 32)];

    /// <summary>The 200 000-byte deterministic file: concatenated SHA-256(BE64(i)).</summary>
    public static byte[] FileContent()
    {
        var content = new byte[200_000];
        var counter = new byte[8];
        var offset = 0;
        for (ulong index = 0; offset < content.Length; index++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(counter, index);
            var digest = SHA256.HashData(counter);
            var take = Math.Min(digest.Length, content.Length - offset);
            digest.AsSpan(0, take).CopyTo(content.AsSpan(offset));
            offset += take;
        }

        return content;
    }

    /// <summary>Generates the complete fixture store under <paramref name="rootDirectory"/>.</summary>
    public static async Task GenerateAsync(string rootDirectory, CancellationToken cancellationToken)
    {
        var store = new LocalFileSystemObjectStore(rootDirectory);
        using var keys = RepositoryKeySet.FromMasterKey(MasterKey);
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var objectIds = new ObjectIdDeriver(hierarchy.DeriveContentIdKey());
        using var storeKeys = new StoreBlobKeyDeriver(hierarchy.DeriveKeyIdKey());
        var spool = Path.Combine(Path.GetTempPath(), "fbp-fixture-spool", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(spool);

        try
        {
            // --- key object, then descriptor (ADR-0022 §Decision 3) -------
            await PutAsync(store, ObjectKey.Parse($"keys/{Base32.Encode(FixtureKeyId.ToArray())}"),
                BuildKeyObject(), cancellationToken).ConfigureAwait(false);

            await PutAsync(store, ObjectKey.Parse("repository-format"),
                RepositoryDescriptorCodec.Serialize(new RepositoryDescriptor(
                    Repo, FormatLimits.FormatVersion, RequiredFeatures: [], OptionalFeatures: [],
                    FixtureKdf, KdfSalt, CreatedAt, "fallbackplan-fixture/1.0", UnstableFormat: true)),
                cancellationToken).ConfigureAwait(false);

            // Shared-sequence layout (08 §2), mirroring a real publication:
            // 1 intent · 2 data blob · 3 metadata blob · 4 standalone
            // snapshot · 5 delta · 6 retirement.
            var dataBlobId = BlobId.FromWriterCounter(Writer, 2);
            var metaBlobId = BlobId.FromWriterCounter(Writer, 3);

            await PublishJournalAsync(store, hierarchy, objectIds, sequence: 1,
                new JournalPayload.WriteIntent(BackupSetId, [dataBlobId, metaBlobId], 3_600_000, 5, IntentPurpose.Backup),
                Salt(0xD5), cancellationToken).ConfigureAwait(false);

            // --- data blob (counter 2) ------------------------------------
            var content = FileContent();
            var references = new List<SegmentReference>();
            var entries = new List<IndexEntry>();

            var dataKey = keys.DeriveClassKey(BlobClass.Data, KeyGeneration.Zero);
            var dataWriter = BlobWriter.Create(
                Repo, Writer, KeyGeneration.Zero, BlobClass.Data, dataKey, blobCounter: 2,
                EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, spool, Salt(0xD1));

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

            // --- metadata blob (counter 3) --------------------------------
            var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, KeyGeneration.Zero);
            var metaWriter = BlobWriter.Create(
                Repo, Writer, KeyGeneration.Zero, BlobClass.Metadata, metadataKey, blobCounter: 3,
                EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, spool, Salt(0xD2));

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
                    ObjectType.SnapshotManifest, snapshotObjectId, encodedSnapshot, Salt(0xD3)),
                cancellationToken).ConfigureAwait(false);

            // --- index delta (sequence 5) ---------------------------------
            var deltaId = DeltaId.FromBytes(Enumerable.Repeat((byte)0xDA, 16).ToArray());
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
                    storedDelta, Salt(0xD4)),
                cancellationToken).ConfigureAwait(false);

            // --- intent retirement (sequence 6) ---------------------------
            await PublishJournalAsync(store, hierarchy, objectIds, sequence: 6,
                new JournalPayload.IntentRetirement(1, IntentOutcome.Completed),
                Salt(0xD6), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(spool))
            {
                Directory.Delete(spool, recursive: true);
            }
        }
    }

    /// <summary>
    /// The deterministic recovery kit for this fixture repository
    /// (specifications/recovery-kit; FR-KIT-001): fixed fields, the same
    /// verbatim key object the store carries, committed alongside the
    /// fixture and regenerated byte-identically.
    /// </summary>
    public static byte[] BuildKitFramed() => Format.RecoveryKit.RecoveryKitCodec.Serialize(
        new Format.RecoveryKit.RecoveryKit
        {
            KitFormatVersion = 1,
            MinimumToolVersion = "0.1.0",
            RepositoryId = Repo,
            RepositoryFormatVersion = FormatLimits.FormatVersion,
            KeyObject = BuildKeyObject(),
            KdfMemoryKiB = FixtureKdf.MemoryKiB,
            KdfIterations = FixtureKdf.Iterations,
            KdfParallelism = FixtureKdf.Parallelism,
            KdfSalt = KdfSalt,
            Destinations =
            [
                new Format.RecoveryKit.KitDestination(
                    "local-path", "file:///fixtures/fixture-repository-v1", "", ""),
            ],
            IssuingDeviceId = DeviceId,
            IssuedAt = CreatedAt,
            Instructions =
                "1. Install the FallbackPlan recovery tool. "
                + "2. Run: recover --kit <this file> --repo <store location>. "
                + "3. Enter the repository passphrase when prompted. "
                + "The kit is one factor; without the passphrase it opens nothing.",
        });

    /// <summary>The canonical text form of the fixture kit.</summary>
    public static string BuildKitText() => Format.RecoveryKit.RecoveryKitText.Render(
        BuildKitFramed(),
        "Keep this page with your passphrase manager, not with your passphrase.");

    private static byte[] BuildKeyObject()
    {
        using var passphrase = CreatePassphrase();
        using var derivation = KekDerivation.Derive(passphrase, FixtureKdf, KdfSalt, KdfValidationMode.OpenRepository);
        using var bundle = new KeyBundle(MasterKey, currentDataGeneration: 0, currentMetadataGeneration: 0, CreatedAt);

        var bundleCbor = KeyBundleCodec.Encode(bundle);
        var aad = KeyObjectFraming.BuildAad(FormatLimits.FormatVersion, KeyObjectFraming.KekProfileAes256GcmV1, FixtureKeyId);
        var ciphertext = new byte[bundleCbor.Length];
        var tag = new byte[KeyWrapping.TagLength];
        KeyWrapping.Wrap(derivation.Kek, WrapNonce, aad, bundleCbor, ciphertext, tag);

        return KeyObjectFraming.Serialize(FormatLimits.FormatVersion, FixtureKeyId, WrapNonce, ciphertext, tag);
    }

    /// <summary>The fixture passphrase as a disposable value.</summary>
    public static Passphrase CreatePassphrase() => Crypto.Passphrase.Create(Passphrase);

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
