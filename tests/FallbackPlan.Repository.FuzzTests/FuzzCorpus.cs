using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Format.RecoveryKit;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Repository.FuzzTests;

/// <summary>One mutation-fuzz target: a valid encoding and the parser that owns it.</summary>
public sealed record CorpusSeed(string Name, byte[] Bytes, Action<byte[]> Parse);

/// <summary>
/// The seed corpus for structure-mutating fuzzing (F3): one known-valid
/// encoding per parser, built from the same construction paths the round-trip
/// tests prove, so a mutation lands inside real structure instead of dying at
/// the first magic check.
/// </summary>
internal static class FuzzCorpus
{
    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    public static IReadOnlyList<CorpusSeed> Seeds { get; } = BuildSeeds();

    private static ObjectId Object32(byte seed)
    {
        var bytes = new byte[32];
        Array.Fill(bytes, seed);
        return ObjectId.FromBytes(bytes);
    }

    private static BlobId Blob16(byte seed)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, seed);
        return BlobId.FromBytes(bytes);
    }

    private static byte[] Fill16(byte value) => [.. Enumerable.Repeat(value, 16)];

    private static IndexEntry Entry(byte objectSeed, byte blobSeed) => new(
        Object32(objectSeed),
        Blob16(blobSeed),
        PhysicalOffset: 88,
        StoredLength: 4096,
        CompressionProfileValue: 0x0001,
        EncryptionProfileValue: 0x0001,
        IndexEntryType.Insertion);

    private static List<CorpusSeed> BuildSeeds()
    {
        var seeds = new List<CorpusSeed>
        {
            new("file-version",
                FileVersionManifestCodec.Encode(new FileVersionManifest
                {
                    EntryKind = EntryKind.File,
                    Name = "report.docx"u8.ToArray(),
                    NameNormalisation = NameNormalisation.Nfc,
                    LogicalLength = 300_000,
                    SegmentReferences =
                    [
                        new SegmentReference(0, 131_072, Object32(1)),
                        new SegmentReference(131_072, 131_072, Object32(2)),
                        new SegmentReference(262_144, 37_856, Object32(3)),
                    ],
                    WholeFileHash = SHA256.HashData("whole file"u8),
                    SegmentationProfile = 0x0001,
                    Metadata = new EntryMetadata
                    {
                        ModifiedAt = 1_722_600_000_000,
                        PosixMode = 0x1A4,
                        OwnerName = "ben",
                    },
                }),
                bytes => FileVersionManifestCodec.Decode(bytes)),

            new("tree",
                TreeManifestCodec.Encode(new TreeManifest
                {
                    Entries =
                    [
                        new TreeEntry("a.txt"u8.ToArray(), Object32(1), EntryKind.File),
                        new TreeEntry("b.txt"u8.ToArray(), Object32(2), EntryKind.File),
                    ],
                    Name = "docs"u8.ToArray(),
                    NameNormalisation = NameNormalisation.Nfc,
                }),
                bytes => TreeManifestCodec.Decode(bytes)),

            new("snapshot",
                SnapshotManifestCodec.Encode(new SnapshotManifest
                {
                    SnapshotId = Fill16(0x11),
                    DeviceId = Fill16(0x22),
                    BackupSetId = Fill16(0x33),
                    CaptureStartedAt = 1,
                    CaptureCompletedAt = 2,
                    RootTree = Object32(3),
                    PolicyManifest = Object32(9),
                    ConsistencyMethod = 1,
                    CaptureStatus = 1,
                    SourceFilesystem = new SourceFilesystem(true, true, "ext4"),
                    PublicationGeneration = 0,
                    ClientVersion = "fuzz/1.0",
                }, new byte[64]),
                bytes => SnapshotManifestCodec.Decode(bytes)),

            new("policy",
                PolicyManifestCodec.Encode(new PolicyManifest
                {
                    SegmentationProfile = 0x0002,
                    SegmentSizeOrTarget = 65_536,
                    CdcMinSize = 8_192,
                    CdcMaxSize = 524_288,
                    CdcWindowSize = 64,
                    CompressionProfile = 0x0001,
                    CompressionThresholdPermille = 50,
                    EncryptionProfile = 0x0001,
                    BlobTargetSize = 64 * 1024 * 1024,
                    BlobMaxSize = 256 * 1024 * 1024,
                    BlobMaxRecordCount = 65_536,
                    DedupTrustDomain = 2,
                    ExcludeRules = ["**/.cache/**"],
                }),
                bytes => PolicyManifestCodec.Decode(bytes)),

            new("error-manifest",
                ErrorManifestCodec.Encode(new ErrorManifest(
                [
                    new CaptureFailure(
                        ["home"u8.ToArray(), "locked.db"u8.ToArray()],
                        CaptureFailureReason.ChangedDuringRead,
                        "file changed twice during read"),
                ])),
                bytes => ErrorManifestCodec.Decode(bytes)),

            new("index-delta",
                IndexDeltaCodec.Encode(new IndexDelta
                {
                    WriterId = Writer,
                    Sequence = 7,
                    PredecessorDeltaId = DeltaId.FromBytes(Fill16(5)),
                    Generation = 0,
                    CoveredBlobIds = [Blob16(6)],
                    Entries = [Entry(1, 6), Entry(2, 6)],
                }, new byte[64]),
                bytes => IndexDeltaCodec.Decode(bytes)),

            new("checkpoint", BuildCheckpoint(), bytes => CheckpointCodec.Decode(bytes)),

            new("journal-record",
                JournalRecordCodec.Encode(
                    new JournalRecord(JournalRecordKind.WriteIntent, Writer, 1, 1000,
                        new JournalPayload.WriteIntent(
                            Fill16(0x33), [Blob16(1), Blob16(2)], 60_000, 5, IntentPurpose.Backup)),
                    new byte[64]),
                bytes => JournalRecordCodec.Decode(bytes)),

            new("standalone-record", BuildStandaloneRecord(),
                bytes => StandaloneRecordFraming.Parse(bytes)),

            new("key-bundle", BuildKeyBundle(), bytes => Format.Keys.KeyBundleCodec.Decode(bytes).Dispose()),

            // The write-only (format v2) parsers (ADR-0042): the 168-byte
            // sealed envelope, the sidecar that carries a content key, and
            // both recovery-kit body shapes.
            new("sealed-blob-envelope", BuildSealedEnvelope(), bytes => BlobEnvelope.Parse(bytes)),
            new("recovery-kit-v1", BuildRecoveryKit(writeOnly: false), bytes => RecoveryKitCodec.Parse(bytes)),
            new("recovery-kit-v2", BuildRecoveryKit(writeOnly: true), bytes => RecoveryKitCodec.Parse(bytes)),
        };

        return seeds;
    }

    private static byte[] BuildSealedEnvelope()
    {
        var envelope = new BlobEnvelope(
            FormatLimits.SealedFormatVersion,
            BlobClass.Data,
            new KeyGeneration(0),
            Blob16(0x2C),
            [.. Enumerable.Repeat((byte)0x5A, 32)],
            42,
            Writer,
            [.. Enumerable.Range(0x10, 80).Select(value => (byte)value)]);
        var bytes = new byte[BlobEnvelope.MaxLength];
        envelope.WriteTo(bytes);
        return bytes;
    }

    private static byte[] BuildRecoveryKit(bool writeOnly) => RecoveryKitCodec.Serialize(new RecoveryKit
    {
        KitFormatVersion = 1,
        MinimumToolVersion = "0.1.0",
        RepositoryId = Repo,
        RepositoryFormatVersion = writeOnly ? FormatLimits.SealedFormatVersion : FormatLimits.FormatVersion,
        KeyObject = writeOnly ? ReadOnlyMemory<byte>.Empty : "FBPKKEYS-fuzz-corpus-wrapped-key"u8.ToArray(),
        KdfMemoryKiB = 8 * 1024,
        KdfIterations = 1,
        KdfParallelism = 1,
        KdfSalt = Fill16(0x21),
        Destinations = [new KitDestination("local-path", "file:///fuzz", "", "")],
        IssuingDeviceId = Fill16(0x55),
        IssuedAt = 1_722_600_000_000,
        Instructions = "fuzz-corpus instructions",
        SealingPublicKey = writeOnly ? Enumerable.Repeat((byte)0x9C, 32).ToArray() : ReadOnlyMemory<byte>.Empty,
    });

    /// <summary>
    /// A committed v2 spool sidecar, content key included — written through
    /// the public sealed writer into a throwaway spool so the internal
    /// serialiser stays internal.
    /// </summary>
    public static byte[] SealedSpoolCheckpointSeed { get; } = BuildSealedSpoolCheckpoint();

    private static byte[] BuildSealedSpoolCheckpoint()
    {
        var spool = Path.Combine(Path.GetTempPath(), "fbp-fuzz-corpus", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(spool);
        try
        {
            var sealingPublic = ContentSealing.PublicKeyOf([.. Enumerable.Repeat((byte)0x61, 32)]);
            var writer = BlobWriter.CreateSealed(
                Repo, Writer, new KeyGeneration(0), new byte[32], sealingPublic, blobCounter: 3,
                EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, spool,
                pinned: new SpoolPinnedConfiguration(
                    1, 65_536, 0, 0, CompressionProfile.None.Value, "none",
                    EncryptionProfile.Aes256GcmV1.Value));
            var payload = Enumerable.Repeat((byte)0x41, 256).ToArray();
            writer.AppendRecordAsync(
                    ObjectType.SegmentRecord, Object32(0x2D), CompressionProfile.None,
                    (ulong)payload.Length, payload, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            writer.AbandonAsync().AsTask().GetAwaiter().GetResult();
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return File.ReadAllBytes(Directory.GetFiles(spool, "*.checkpoint").Single());
        }
        finally
        {
            Directory.Delete(spool, recursive: true);
        }
    }

    private static byte[] BuildKeyBundle()
    {
        using var bundle = new Format.Keys.KeyBundle(
            [.. Enumerable.Range(0, 32).Select(value => (byte)value)],
            currentDataGeneration: 0,
            currentMetadataGeneration: 0,
            createdAt: 1_722_600_000_000);
        return Format.Keys.KeyBundleCodec.Encode(bundle);
    }

    private static byte[] BuildCheckpoint()
    {
        var entries = new List<IndexEntry> { Entry(0x11, 1), Entry(0x22, 1) };
        var (shardSet, hashes) = ShardHashes.Compute(entries);

        return CheckpointCodec.Encode(new Checkpoint
        {
            Generation = 0,
            WriterId = Writer,
            SubsumedDeltaIds = [DeltaId.FromBytes(Fill16(5))],
            WriterWatermarks = [new WriterWatermark(Writer, 9)],
            ShardSet = shardSet,
            ShardHashesList = [.. hashes.Select(hash => (ReadOnlyMemory<byte>)hash)],
            Entries = entries,
        }, new byte[64]);
    }

    private static byte[] BuildStandaloneRecord()
    {
        using var keys = RepositoryKeySet.FromMasterKey([.. Enumerable.Range(0, 32).Select(value => (byte)value)]);
        var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, KeyGeneration.Zero);
        var salt = new byte[32];
        Array.Fill(salt, (byte)0x5A);

        return StandaloneRecordCipher.Seal(
            Repo, metadataKey, KeyGeneration.Zero, Writer, counter: 42,
            ObjectType.IndexDelta, Object32(7), "fuzz-corpus payload"u8, salt);
    }

    /// <summary>A valid serialized repository descriptor — the never-throws target.</summary>
    public static byte[] DescriptorSeed { get; } = RepositoryDescriptorCodec.Serialize(new RepositoryDescriptor(
        Repo,
        FormatVersion: 1,
        RequiredFeatures: [],
        OptionalFeatures: [7],
        new Argon2Parameters { MemoryKiB = 65536, Iterations = 3, Parallelism = 4 },
        KdfSalt: Fill16(0x0F),
        CreatedAt: 1_722_600_000_000,
        CreatedBy: "fallbackplan-fuzz/1.0",
        UnstableFormat: true));

    /// <summary>The v2 descriptor — key 9 and the required sealed-data-plane feature (ADR-0042).</summary>
    public static byte[] DescriptorV2Seed { get; } = RepositoryDescriptorCodec.Serialize(new RepositoryDescriptor(
        Repo,
        FormatLimits.SealedFormatVersion,
        RequiredFeatures: [RepositoryDescriptorCodec.FeatureSealedDataPlane],
        OptionalFeatures: [],
        new Argon2Parameters { MemoryKiB = 65536, Iterations = 3, Parallelism = 4 },
        KdfSalt: Fill16(0x0F),
        CreatedAt: 1_722_600_000_000,
        CreatedBy: "fallbackplan-fuzz/1.0",
        UnstableFormat: true,
        Enumerable.Repeat((byte)0x9C, 32).ToArray()));
}
