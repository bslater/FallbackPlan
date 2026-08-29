using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// The manifest codecs (specification 06; FR-MAN-003, FR-ARCH-010,
/// NFR-PORT-003): round-trips are exact, unknown keys are rejected so
/// physical location has nowhere to hide (exit criteria 2 and 9), the
/// 06 §3.2 coverage obligation is enforced, tree chains obey 06 §9, and the
/// snapshot signature survives the two-pass construction.
/// </summary>
[TestClass]
public sealed class ManifestCodecTests
{
    private static ObjectId TestObjectId(byte seed)
    {
        var bytes = new byte[32];
        Array.Fill(bytes, seed);
        return ObjectId.FromBytes(bytes);
    }

    private static FileVersionManifest SampleFileVersion() => new()
    {
        EntryKind = EntryKind.File,
        Name = "report.docx"u8.ToArray(),
        NameNormalisation = NameNormalisation.Nfc,
        LogicalLength = 300_000,
        SegmentReferences =
        [
            new SegmentReference(0, 131_072, TestObjectId(1)),
            new SegmentReference(131_072, 131_072, TestObjectId(2)),
            new SegmentReference(262_144, 37_856, TestObjectId(3)),
        ],
        WholeFileHash = SHA256.HashData("whole file"u8),
        SegmentationProfile = 0x0001,
        Metadata = new EntryMetadata
        {
            ModifiedAt = 1_722_600_000_000,
            PosixMode = 0x1A4, // 0644
            OwnerName = "ben",
            ExtendedAttributes = [new ExtendedAttributeEntry("user.origin"u8.ToArray(), "download"u8.ToArray())],
        },
    };

    [TestMethod]
    public void FileVersionManifest_EncodedAndDecoded_RoundTripsByteIdentically()
    {
        var encoded = FileVersionManifestCodec.Encode(SampleFileVersion());
        var decoded = FileVersionManifestCodec.Decode(encoded);

        SequenceAssert.AreEqual(encoded, FileVersionManifestCodec.Encode(decoded));
        Assert.AreEqual(3, decoded.SegmentReferences.Count);
        Assert.AreEqual("ben", decoded.Metadata.OwnerName);
        Assert.AreEqual((ulong)1_722_600_000_000, decoded.Metadata.ModifiedAt);
    }

    [TestMethod]
    public void FileVersionManifest_CarriesAnUnknownKey_IsRejectedRatherThanSkipped()
    {
        // Hand-build a manifest carrying key 14 — exactly where a physical
        // hint would smuggle in (06 §3.1; exit criterion 2's teeth).
        var writer = new FallbackPlan.Repository.Format.Cbor.CanonicalCborWriter();
        writer.WriteStartMap(1);
        writer.WriteKey(14);
        writer.WriteUnsignedInteger(1);
        writer.WriteEndMap();

        var exception = Assert.ThrowsExactly<ManifestValidationException>(() =>
            FileVersionManifestCodec.Decode(writer.Encode()));
        Assert.Contains("unknown key", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void FileVersionManifest_SegmentReferenceHasFourElements_IsRejected()
    {
        // A fourth array position is the other place a blob id could hide
        // (06 §3: "an array of exactly three elements").
        var writer = new FallbackPlan.Repository.Format.Cbor.CanonicalCborWriter();
        writer.WriteStartMap(1);
        writer.WriteKey(5);
        writer.WriteStartArray(1);
        writer.WriteStartArray(4);
        writer.WriteUnsignedInteger(0);
        writer.WriteUnsignedInteger(100);
        writer.WriteByteString(TestObjectId(1).ToArray());
        writer.WriteUnsignedInteger(7); // the smuggled physical hint
        writer.WriteEndArray();
        writer.WriteEndArray();
        writer.WriteEndMap();

        var exception = Assert.ThrowsExactly<ManifestValidationException>(() =>
            FileVersionManifestCodec.Decode(writer.Encode()));
        Assert.Contains("three elements", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void FileVersionManifest_SegmentReferencesLeaveAGap_IsRejected()
    {
        var manifest = SampleFileVersion() with
        {
            SegmentReferences =
            [
                new SegmentReference(0, 131_072, TestObjectId(1)),
                // gap: [131072, 262144) uncovered
                new SegmentReference(262_144, 37_856, TestObjectId(3)),
            ],
        };

        var exception = Assert.ThrowsExactly<ManifestValidationException>(() => FileVersionManifestCodec.Encode(manifest));
        Assert.Contains("Coverage breaks", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void FileVersionManifest_SegmentReferencesOverlap_IsRejected()
    {
        var manifest = SampleFileVersion() with
        {
            LogicalLength = 200_000,
            SegmentReferences =
            [
                new SegmentReference(0, 131_072, TestObjectId(1)),
                new SegmentReference(100_000, 100_000, TestObjectId(2)),
            ],
        };

        Assert.ThrowsExactly<ManifestValidationException>(() => FileVersionManifestCodec.Encode(manifest));
    }

    [TestMethod]
    public void FileVersionManifest_SparseExtentsFillTheGaps_IsAccepted()
    {
        var manifest = SampleFileVersion() with
        {
            LogicalLength = 400_000,
            SegmentReferences =
            [
                new SegmentReference(0, 131_072, TestObjectId(1)),
                new SegmentReference(300_000, 100_000, TestObjectId(2)),
            ],
            SparseExtents = [new SparseExtent(131_072, 168_928)],
        };

        var decoded = FileVersionManifestCodec.Decode(FileVersionManifestCodec.Encode(manifest));

        Assert.ContainsSingle(decoded.SparseExtents);
        Assert.AreEqual((ulong)168_928, decoded.SparseExtents[0].Length);
    }

    [TestMethod]
    public void FileVersionManifest_AZeroLengthFile_HasNoReferencesAndTheEmptyHash()
    {
        var manifest = SampleFileVersion() with
        {
            LogicalLength = 0,
            SegmentReferences = [],
            WholeFileHash = SHA256.HashData([]),
        };

        var decoded = FileVersionManifestCodec.Decode(FileVersionManifestCodec.Encode(manifest));

        Assert.IsEmpty(decoded.SegmentReferences);
        SequenceAssert.AreEqual(SHA256.HashData([]), decoded.WholeFileHash.ToArray());
    }

    [TestMethod]
    public void TreeManifest_EntriesAreOutOfByteOrder_IsRejected()
    {
        var tree = new TreeManifest
        {
            Entries =
            [
                new TreeEntry("beta"u8.ToArray(), TestObjectId(1), EntryKind.File),
                new TreeEntry("alpha"u8.ToArray(), TestObjectId(2), EntryKind.File),
            ],
            Name = "docs"u8.ToArray(),
            NameNormalisation = NameNormalisation.Nfc,
            Metadata = EntryMetadata.Empty,
        };

        Assert.ThrowsExactly<ManifestValidationException>(() => TreeManifestCodec.Encode(tree));
    }

    [TestMethod]
    public void TreeManifest_DuplicateNames_AreRejectedWhileCaseVariantsAreAccepted()
    {
        var duplicate = new TreeManifest
        {
            Entries =
            [
                new TreeEntry("same"u8.ToArray(), TestObjectId(1), EntryKind.File),
                new TreeEntry("same"u8.ToArray(), TestObjectId(2), EntryKind.File),
            ],
            Name = "docs"u8.ToArray(),
            NameNormalisation = NameNormalisation.Nfc,
        };

        Assert.ThrowsExactly<ManifestValidationException>(() => TreeManifestCodec.Encode(duplicate));

        // Case variants differ in raw bytes — a legitimate state on a
        // case-sensitive source (06 §5).
        var caseVariants = duplicate with
        {
            Entries =
            [
                new TreeEntry("Same"u8.ToArray(), TestObjectId(1), EntryKind.File),
                new TreeEntry("same"u8.ToArray(), TestObjectId(2), EntryKind.File),
            ],
        };

        var decoded = TreeManifestCodec.Decode(TreeManifestCodec.Encode(caseVariants));
        Assert.AreEqual(2, decoded.Entries.Count);
    }

    [TestMethod]
    public void TreeChain_AValidChain_ValidatesAndFlattensInChainOrder()
    {
        var head = new TreeManifest
        {
            Entries = [new TreeEntry("aaa"u8.ToArray(), TestObjectId(1), EntryKind.File)],
            Name = "big-directory"u8.ToArray(),
            NameNormalisation = NameNormalisation.Nfc,
            Metadata = EntryMetadata.Empty,
            Continuation = TestObjectId(9),
        };
        var tail = new TreeManifest
        {
            Entries = [new TreeEntry("bbb"u8.ToArray(), TestObjectId(2), EntryKind.File)],
        };

        var flattened = TreeChain.ValidateAndFlatten([head, tail]);

        Assert.AreEqual(2, flattened.Count);
        SequenceAssert.AreEqual("aaa"u8.ToArray(), flattened[0].Name.ToArray());
    }

    [TestMethod]
    public void TreeChain_ShardsInterleave_IsADamageFinding()
    {
        var head = new TreeManifest
        {
            Entries = [new TreeEntry("zzz"u8.ToArray(), TestObjectId(1), EntryKind.File)],
            Name = "dir"u8.ToArray(),
            NameNormalisation = NameNormalisation.Nfc,
            Continuation = TestObjectId(9),
        };
        var tail = new TreeManifest
        {
            Entries = [new TreeEntry("aaa"u8.ToArray(), TestObjectId(2), EntryKind.File)],
        };

        // Every entry must sort strictly after every predecessor entry (06 §9).
        Assert.ThrowsExactly<ManifestValidationException>(() => TreeChain.ValidateAndFlatten([head, tail]));
    }

    [TestMethod]
    public void TreeChain_AContinuationCarriesHeadFields_IsRejected()
    {
        var head = new TreeManifest
        {
            Entries = [new TreeEntry("aaa"u8.ToArray(), TestObjectId(1), EntryKind.File)],
            Name = "dir"u8.ToArray(),
            NameNormalisation = NameNormalisation.Nfc,
            Continuation = TestObjectId(9),
        };
        var tail = new TreeManifest
        {
            Entries = [new TreeEntry("bbb"u8.ToArray(), TestObjectId(2), EntryKind.File)],
            Name = "dir-again"u8.ToArray(),
            NameNormalisation = NameNormalisation.Nfc,
        };

        Assert.ThrowsExactly<ManifestValidationException>(() => TreeChain.ValidateAndFlatten([head, tail]));
    }

    private static SnapshotManifest SampleSnapshot() => new()
    {
        SnapshotId = Enumerable.Repeat((byte)0x11, 16).ToArray(),
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        CaptureStartedAt = 1_722_600_000_000,
        CaptureCompletedAt = 1_722_600_100_000,
        RootTree = TestObjectId(6),
        ParentSnapshots = [Enumerable.Repeat((byte)0x44, 16).ToArray()],
        PolicyManifest = TestObjectId(8),
        ConsistencyMethod = 1,
        CaptureStatus = 1,
        SourceFilesystem = new SourceFilesystem(CaseSensitive: true, SupportsSparse: true, Name: "ext4"),
        PublicationGeneration = 0,
        ObservedClockSkewMs = -125,
        ClientVersion = "fallbackplan-tests/1.0",
        Tags = ["nightly"],
    };

    [TestMethod]
    public void SnapshotManifest_SignedAndRoundTripped_VerifiesThroughTheTwoPassConstruction()
    {
        using var hierarchy = new KeyHierarchy([.. Enumerable.Range(0, 32).Select(value => (byte)value)]);
        using var signer = RepositorySigner.Create(hierarchy, KeyGeneration.Zero);

        var manifest = SampleSnapshot();
        var signedBytes = SnapshotManifestCodec.EncodeForSigning(manifest);
        var stored = SnapshotManifestCodec.Encode(manifest, signer.Sign(signedBytes));

        var decoded = SnapshotManifestCodec.Decode(stored);

        // The verifier re-encodes keys 1-16 — it can never strip a suffix,
        // because adding key 17 changed the map header's count (06 §6.1).
        SequenceAssert.AreEqual(signedBytes, decoded.SignedBytes.ToArray());
        Assert.IsTrue(signer.Verify(decoded.SignedBytes.Span, decoded.Signature.Span));
        Assert.AreEqual(manifest.RootTree, decoded.Manifest.RootTree);
        Assert.AreEqual(-125, decoded.Manifest.ObservedClockSkewMs);
    }

    [TestMethod]
    public void SnapshotManifest_AFieldIsTampered_FailsVerificationAsASecurityFinding()
    {
        using var hierarchy = new KeyHierarchy([.. Enumerable.Range(0, 32).Select(value => (byte)value)]);
        using var signer = RepositorySigner.Create(hierarchy, KeyGeneration.Zero);

        var manifest = SampleSnapshot();
        var stored = SnapshotManifestCodec.Encode(manifest, signer.Sign(SnapshotManifestCodec.EncodeForSigning(manifest)));

        // Re-encode with a substituted root tree but the original signature:
        // the substitution attack the signature exists to catch.
        var forged = SnapshotManifestCodec.Encode(
            manifest with { RootTree = TestObjectId(0xEE) },
            SnapshotManifestCodec.Decode(stored).Signature.Span);

        var decoded = SnapshotManifestCodec.Decode(forged);
        Assert.IsFalse(signer.Verify(decoded.SignedBytes.Span, decoded.Signature.Span));
    }

    [TestMethod]
    public void SnapshotManifest_OptionalKeysAreAbsent_AreOmittedRatherThanNulled()
    {
        var manifest = SampleSnapshot() with { ErrorManifest = null, ObservedClockSkewMs = null };

        var decoded = SnapshotManifestCodec.Decode(
            SnapshotManifestCodec.Encode(manifest, new byte[64]));

        Assert.IsNull(decoded.Manifest.ErrorManifest);
        Assert.IsNull(decoded.Manifest.ObservedClockSkewMs);
    }

    [TestMethod]
    public void PolicyManifest_UnderBothSegmentationProfiles_RoundTrips()
    {
        var fixedPolicy = new PolicyManifest
        {
            SegmentationProfile = 0x0001,
            SegmentSizeOrTarget = 1024 * 1024,
            CompressionProfile = 0x0001,
            CompressionThresholdPermille = 50,
            EncryptionProfile = 0x0001,
            BlobTargetSize = 64 * 1024 * 1024,
            BlobMaxSize = 256 * 1024 * 1024,
            BlobMaxRecordCount = 65_536,
            DedupTrustDomain = 2,
            ExcludeRules = ["**/.cache/**"],
        };

        // Compare via re-encoding: record equality would compare the list
        // properties by reference, and the bytes are the actual contract.
        var fixedBytes = PolicyManifestCodec.Encode(fixedPolicy);
        var decodedFixed = PolicyManifestCodec.Decode(fixedBytes);
        SequenceAssert.AreEqual(fixedBytes, PolicyManifestCodec.Encode(decodedFixed));
        SequenceAssert.AreEqual(["**/.cache/**"], decodedFixed.ExcludeRules);
        Assert.IsNull(decodedFixed.CdcMinSize);

        var cdcPolicy = fixedPolicy with
        {
            SegmentationProfile = 0x0002,
            SegmentSizeOrTarget = 65_536,
            CdcMinSize = 8_192,
            CdcMaxSize = 524_288,
            CdcWindowSize = 64,
        };

        var cdcBytes = PolicyManifestCodec.Encode(cdcPolicy);
        var decodedCdc = PolicyManifestCodec.Decode(cdcBytes);
        SequenceAssert.AreEqual(cdcBytes, PolicyManifestCodec.Encode(decodedCdc));
        Assert.AreEqual((ulong)8_192, decodedCdc.CdcMinSize);
        Assert.AreEqual((byte)64, decodedCdc.CdcWindowSize);
    }

    [TestMethod]
    public void ErrorManifest_EncodedAndDecoded_RoundTrips()
    {
        var manifest = new ErrorManifest(
        [
            new CaptureFailure(
                ["home"u8.ToArray(), "locked.db"u8.ToArray()],
                CaptureFailureReason.ChangedDuringRead,
                "file changed twice during read"),
        ]);

        var decoded = ErrorManifestCodec.Decode(ErrorManifestCodec.Encode(manifest));

        Assert.ContainsSingle(decoded.Failures);
        Assert.AreEqual(CaptureFailureReason.ChangedDuringRead, decoded.Failures[0].Reason);
        Assert.AreEqual(2, decoded.Failures[0].PathComponents.Count);
    }

    /// <summary>
    /// Specification 06 assigns reason 8 — a filename with no faithful
    /// repository encoding is refused, never substituted — and the encoder
    /// writes it unchecked. A decoder that rejects 8 as unassigned makes
    /// every snapshot carrying such a refusal unreadable at exactly the
    /// moment somebody asks what failed.
    /// </summary>
    [TestMethod]
    public void ErrorManifest_ReasonNameNotRepresentable_RoundTrips()
    {
        var manifest = new ErrorManifest(
        [
            new CaptureFailure(
                ["home"u8.ToArray(), new byte[] { 0xEF, 0xBF, 0xBD }],
                CaptureFailureReason.NameNotRepresentable,
                "the name does not survive conversion in both directions"),
        ]);

        var decoded = ErrorManifestCodec.Decode(ErrorManifestCodec.Encode(manifest));

        Assert.AreEqual(CaptureFailureReason.NameNotRepresentable, Assert.ContainsSingle(decoded.Failures).Reason);
    }
}
