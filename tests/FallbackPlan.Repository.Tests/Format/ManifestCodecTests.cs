using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// The manifest codecs (specification 06; FR-MAN-003, FR-MAN-018, FR-DR-006,
/// FR-ARCH-010, NFR-PORT-003): round-trips are exact, unknown keys are rejected so
/// physical location has nowhere to hide (exit criteria 2 and 9), the
/// 06 §3.2 coverage obligation is enforced, tree chains obey 06 §9, and the
/// snapshot signature survives the two-pass construction. Format 6's two keys
/// round-trip and are held to one encoding: a file's or a folder's flags in
/// metadata key 11, present only when some flag is set and refused with a
/// reserved bit, and a link's form in key 14, carried only by a link that is
/// a mount point (FR-MAN-003; ADR-0184).
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
        // Hand-build a manifest carrying key 15 — exactly where a physical
        // hint would smuggle in (06 §3.1; exit criterion 2's teeth).
        var writer = new FallbackPlan.Repository.Format.Cbor.CanonicalCborWriter();
        writer.WriteStartMap(1);
        writer.WriteKey(15);
        writer.WriteUnsignedInteger(1);
        writer.WriteEndMap();

        var exception = Assert.ThrowsExactly<ManifestValidationException>(() =>
            FileVersionManifestCodec.Decode(writer.Encode()));
        Assert.Contains("unknown key", exception.Message, StringComparison.Ordinal);
    }

    private static FileVersionManifest Flagged(FileFlagBits flags) =>
        SampleFileVersion() with { Metadata = SampleFileVersion().Metadata with { FileFlags = flags } };

    [TestMethod]
    public void FileVersionManifest_AFilesFlags_AreKeptInMetadataKeyEleven_AndRoundTrip()
    {
        var encoded = FileVersionManifestCodec.Encode(Flagged(FileFlagBits.Immutable | FileFlagBits.NoDump));
        var decoded = FileVersionManifestCodec.Decode(encoded);

        Assert.AreEqual(FileFlagBits.Immutable | FileFlagBits.NoDump, decoded.Metadata.FileFlags);
        SequenceAssert.AreEqual(encoded, FileVersionManifestCodec.Encode(decoded));

        // The metadata map is the manifest's last key, and key 11 the map's
        // last, so the flags are the manifest's last two bytes.
        SequenceAssert.AreEqual(new byte[] { 0x0B, 0x05 }, encoded[^2..]);

        // A file with no flags carries no key, so its manifest is the one an
        // earlier format wrote.
        var plain = FileVersionManifestCodec.Encode(SampleFileVersion());
        Assert.AreEqual(FileFlagBits.None, FileVersionManifestCodec.Decode(plain).Metadata.FileFlags);
        Assert.HasCount(encoded.Length - 2, plain);
    }

    [TestMethod]
    public void FileVersionManifest_FlagsOfNoneOrAReservedBit_AreRefused()
    {
        var encoded = FileVersionManifestCodec.Encode(Flagged(FileFlagBits.NoDump));

        // No flag is said by leaving the key out; a zero is a second encoding.
        var zero = encoded.ToArray();
        zero[^1] = 0x00;
        Assert.ThrowsExactly<ManifestValidationException>(() => FileVersionManifestCodec.Decode(zero));

        // Bit 6 and above are reserved: a flag this format does not name is
        // one no reader can give back.
        byte[] reserved = [.. encoded[..^1], 0x18, 0x40];
        Assert.ThrowsExactly<ManifestValidationException>(() => FileVersionManifestCodec.Decode(reserved));
        Assert.ThrowsExactly<ManifestValidationException>(
            () => FileVersionManifestCodec.Encode(Flagged((FileFlagBits)(1 << 6))));

        // Every named flag at once is the largest value a reader takes.
        var all = Flagged(
            FileFlagBits.Immutable | FileFlagBits.AppendOnly | FileFlagBits.NoDump
            | FileFlagBits.Compressed | FileFlagBits.Encrypted | FileFlagBits.Hidden);
        Assert.AreEqual(
            all.Metadata.FileFlags, FileVersionManifestCodec.Decode(FileVersionManifestCodec.Encode(all)).Metadata.FileFlags);
    }

    [TestMethod]
    public void TreeManifest_AFoldersFlags_RoundTripInTheSameKey()
    {
        var tree = new TreeManifest
        {
            Entries = [new TreeEntry("a.txt"u8.ToArray(), TestObjectId(4), EntryKind.File)],
            Name = "docs"u8.ToArray(),
            NameNormalisation = NameNormalisation.Nfc,
            Metadata = new EntryMetadata { ModifiedAt = 1_722_600_000_000, FileFlags = FileFlagBits.Hidden },
        };

        var encoded = TreeManifestCodec.Encode(tree);

        var decoded = TreeManifestCodec.Decode(encoded);
        Assert.IsNotNull(decoded.Metadata);
        Assert.AreEqual(FileFlagBits.Hidden, decoded.Metadata.FileFlags);
        SequenceAssert.AreEqual(encoded, TreeManifestCodec.Encode(decoded));
    }

    private static FileVersionManifest Link(LinkForm form) => new()
    {
        EntryKind = EntryKind.Symlink,
        Name = "shared"u8.ToArray(),
        NameNormalisation = NameNormalisation.Nfc,
        LogicalLength = 0,
        SegmentReferences = [],
        WholeFileHash = SHA256.HashData([]),
        SegmentationProfile = 0x0001,
        LinkTarget = "C:\\Shared"u8.ToArray(),
        LinkForm = form,
    };

    [TestMethod]
    public void FileVersionManifest_ALinkThatIsAMountPoint_SaysSoInKeyFourteen_AndRoundTrips()
    {
        var encoded = FileVersionManifestCodec.Encode(Link(LinkForm.MountPoint));
        var decoded = FileVersionManifestCodec.Decode(encoded);

        Assert.AreEqual(LinkForm.MountPoint, decoded.LinkForm);
        SequenceAssert.AreEqual(encoded, FileVersionManifestCodec.Encode(decoded));

        // Key 14 is the manifest's last key.
        SequenceAssert.AreEqual(new byte[] { 0x0E, 0x01 }, encoded[^2..]);

        // A symbolic link says nothing of its form, so its manifest is the
        // one an earlier format wrote.
        var symbolic = FileVersionManifestCodec.Encode(Link(LinkForm.SymbolicLink));
        Assert.AreEqual(LinkForm.SymbolicLink, FileVersionManifestCodec.Decode(symbolic).LinkForm);
        Assert.HasCount(encoded.Length - 2, symbolic);
    }

    [TestMethod]
    public void FileVersionManifest_ALinkFormNoFormatAssigns_OrOneOnAnEntryThatIsNoLink_IsRefused()
    {
        var encoded = FileVersionManifestCodec.Encode(Link(LinkForm.MountPoint));

        // 1 is the only form assigned: 0 would be a second encoding of a
        // symbolic link, and 2 names nothing.
        foreach (var unassigned in new byte[] { 0x00, 0x02 })
        {
            var patched = encoded.ToArray();
            patched[^1] = unassigned;
            Assert.ThrowsExactly<ManifestValidationException>(
                () => FileVersionManifestCodec.Decode(patched), $"link form {unassigned}");
        }

        // A writer is held to the same single form.
        Assert.ThrowsExactly<ManifestValidationException>(
            () => FileVersionManifestCodec.Encode(Link((LinkForm)2)));

        // A mount point is a link to a folder: a file, even one that carries
        // a target, or a link without a target, cannot be one.
        Assert.ThrowsExactly<ManifestValidationException>(
            () => FileVersionManifestCodec.Encode(SampleFileVersion() with { LinkForm = LinkForm.MountPoint }));
        Assert.ThrowsExactly<ManifestValidationException>(
            () => FileVersionManifestCodec.Encode(
                SampleFileVersion() with { LinkTarget = "C:\\Shared"u8.ToArray(), LinkForm = LinkForm.MountPoint }));
        Assert.ThrowsExactly<ManifestValidationException>(
            () => FileVersionManifestCodec.Encode(Link(LinkForm.MountPoint) with { LinkTarget = null }));
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
        using var credential = TestAuthority.Shared.Credential.Clone();
        using var signer = RepositorySigner.Create(credential, KeyGeneration.Zero);

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
        using var credential = TestAuthority.Shared.Credential.Clone();
        using var signer = RepositorySigner.Create(credential, KeyGeneration.Zero);

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
    public void PolicyManifest_WithRecordedShape_RoundTripsCanonically()
    {
        // ADR-0061: the set's shape travels in the archive so a rebuilt
        // machine can re-declare the set from it. Keys 10-12 are optional;
        // a label is absent for a single root and present for several.
        var policy = NinePolicyKeys() with
        {
            Roots =
            [
                new RecordedRoot("/home/ben/Documents", "Documents"),
                new RecordedRoot("/mnt/photos", "Photos"),
            ],
            SetName = "docs",
            Schedule = "every 1h",
        };

        var bytes = PolicyManifestCodec.Encode(policy);
        var decoded = PolicyManifestCodec.Decode(bytes);

        SequenceAssert.AreEqual(bytes, PolicyManifestCodec.Encode(decoded));
        Assert.HasCount(2, decoded.Roots);
        Assert.AreEqual("/home/ben/Documents", decoded.Roots[0].Path);
        Assert.AreEqual("Documents", decoded.Roots[0].Label);
        Assert.AreEqual("docs", decoded.SetName);
        Assert.AreEqual("every 1h", decoded.Schedule);

        var unlabelled = NinePolicyKeys() with { Roots = [new RecordedRoot("/srv/data", null)] };
        var decodedUnlabelled = PolicyManifestCodec.Decode(PolicyManifestCodec.Encode(unlabelled));
        Assert.IsNull(Assert.ContainsSingle(decodedUnlabelled.Roots).Label);
        Assert.IsNull(decodedUnlabelled.SetName);
        Assert.IsNull(decodedUnlabelled.Schedule);
    }

    [TestMethod]
    public void PolicyManifest_WithoutRecordedShape_EncodesTheNineKeyMapUnchanged()
    {
        // Every archive written before ADR-0061 carries the nine-key map, and
        // a manifest that records no shape must still produce exactly it: the
        // optional keys are absent, not empty. 0xA9 is a definite-length CBOR
        // map of nine entries.
        var bytes = PolicyManifestCodec.Encode(NinePolicyKeys());

        Assert.AreEqual((byte)0xA9, bytes[0]);
        var decoded = PolicyManifestCodec.Decode(bytes);
        Assert.IsEmpty(decoded.Roots);
        Assert.IsNull(decoded.SetName);
        Assert.IsNull(decoded.Schedule);
    }

    [TestMethod]
    public void PolicyManifest_ARootCarryingAnUnknownKey_IsRejected()
    {
        // The recorded root's inner map is pinned to keys 1 (label) and
        // 2 (path); a third key is refused as every other unknown key is.
        var writer = new FallbackPlan.Repository.Format.Cbor.CanonicalCborWriter();
        writer.WriteStartMap(10);
        WriteNinePolicyKeys(writer);
        writer.WriteKey(10);
        writer.WriteStartArray(1);
        writer.WriteStartMap(2);
        writer.WriteKey(2);
        writer.WriteTextString("/srv/data");
        writer.WriteKey(3);
        writer.WriteTextString("smuggled");
        writer.WriteEndMap();
        writer.WriteEndArray();
        writer.WriteEndMap();

        var exception = Assert.ThrowsExactly<ManifestValidationException>(() =>
            PolicyManifestCodec.Decode(writer.Encode()));
        Assert.Contains("root", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void PolicyManifest_WithRecordedRetention_RoundTripsCanonically()
    {
        // FR-DR-006: the set's own retention travels with its shape, so a set
        // re-declared from its archive deletes by the policy it ran under.
        // Every rule is optional, and only the ones the set declared are
        // written.
        var policy = NinePolicyKeys() with
        {
            SetName = "docs",
            Retention = new RecordedRetention
            {
                KeepDaily = 7, KeepWeekly = 4, KeepMonthly = 12, MinGenerations = 3, DeferralDays = 30,
            },
        };

        var bytes = PolicyManifestCodec.Encode(policy);
        var decoded = PolicyManifestCodec.Decode(bytes);

        SequenceAssert.AreEqual(bytes, PolicyManifestCodec.Encode(decoded));
        Assert.AreEqual(policy.Retention, decoded.Retention);

        var dailyOnly = NinePolicyKeys() with { Retention = new RecordedRetention { KeepDaily = 14 } };
        Assert.AreEqual(
            new RecordedRetention { KeepDaily = 14 },
            PolicyManifestCodec.Decode(PolicyManifestCodec.Encode(dailyOnly)).Retention);

        // A set that defers retention records nothing, and reads back as
        // deferring it rather than as an empty policy.
        Assert.IsNull(PolicyManifestCodec.Decode(PolicyManifestCodec.Encode(NinePolicyKeys())).Retention);
    }

    [TestMethod]
    public void PolicyManifest_TheDeletedFileDuration_IsRetentionKeySix()
    {
        // FR-GC-014: the deleted-file duration travels with the rest of the
        // set's own policy, so a set re-declared from its archive keeps
        // deleted files for as long as it did. A policy that declares none
        // writes exactly what it wrote before the key was assigned.
        var policy = NinePolicyKeys() with
        {
            Retention = new RecordedRetention { KeepDaily = 7, KeepDeletedDays = 90 },
        };

        var bytes = PolicyManifestCodec.Encode(policy);
        var decoded = PolicyManifestCodec.Decode(bytes);

        SequenceAssert.AreEqual(bytes, PolicyManifestCodec.Encode(decoded));
        Assert.AreEqual(policy.Retention, decoded.Retention);
        Assert.AreEqual(90u, decoded.Retention!.KeepDeletedDays);

        var writer = new FallbackPlan.Repository.Format.Cbor.CanonicalCborWriter();
        writer.WriteStartMap(10);
        WriteNinePolicyKeys(writer);
        writer.WriteKey(13);
        writer.WriteStartMap(1);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(7);
        writer.WriteEndMap();
        writer.WriteEndMap();
        SequenceAssert.AreEqual(
            writer.Encode(),
            PolicyManifestCodec.Encode(
                NinePolicyKeys() with { ExcludeRules = [], Retention = new RecordedRetention { KeepDaily = 7 } }));
    }

    [TestMethod]
    public void PolicyManifest_ARetentionCarryingAnUnknownKey_IsRejected()
    {
        // The retention map is pinned to keys 1-6; a seventh is refused as
        // every other unknown key is, rather than read past.
        var writer = new FallbackPlan.Repository.Format.Cbor.CanonicalCborWriter();
        writer.WriteStartMap(10);
        WriteNinePolicyKeys(writer);
        writer.WriteKey(13);
        writer.WriteStartMap(2);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(7);
        writer.WriteKey(7);
        writer.WriteUnsignedInteger(1);
        writer.WriteEndMap();
        writer.WriteEndMap();

        var exception = Assert.ThrowsExactly<ManifestValidationException>(() =>
            PolicyManifestCodec.Decode(writer.Encode()));
        Assert.Contains("retention", exception.Message, StringComparison.Ordinal);
    }

    private static PolicyManifest NinePolicyKeys() => new()
    {
        SegmentationProfile = 0x0001,
        SegmentSizeOrTarget = 1024 * 1024,
        CompressionProfile = 0x0001,
        CompressionThresholdPermille = 50,
        EncryptionProfile = 0x0001,
        BlobTargetSize = 64 * 1024 * 1024,
        BlobMaxSize = 256 * 1024 * 1024,
        BlobMaxRecordCount = 65_536,
        DedupTrustDomain = 1,
        ExcludeRules = ["**/.cache/**"],
    };

    private static void WriteNinePolicyKeys(FallbackPlan.Repository.Format.Cbor.CanonicalCborWriter writer)
    {
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(1);
        writer.WriteKey(2);
        writer.WriteStartMap(1);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(1024 * 1024);
        writer.WriteEndMap();
        writer.WriteKey(3);
        writer.WriteUnsignedInteger(1);
        writer.WriteKey(4);
        writer.WriteUnsignedInteger(50);
        writer.WriteKey(5);
        writer.WriteUnsignedInteger(1);
        writer.WriteKey(6);
        writer.WriteStartMap(3);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(64 * 1024 * 1024);
        writer.WriteKey(2);
        writer.WriteUnsignedInteger(256 * 1024 * 1024);
        writer.WriteKey(3);
        writer.WriteUnsignedInteger(65_536);
        writer.WriteEndMap();
        writer.WriteKey(7);
        writer.WriteUnsignedInteger(1);
        writer.WriteKey(8);
        writer.WriteStartArray(0);
        writer.WriteEndArray();
        writer.WriteKey(9);
        writer.WriteStartArray(0);
        writer.WriteEndArray();
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

    [TestMethod]
    public void ErrorManifest_EveryAssignedReason_RoundTrips()
    {
        // A loop rather than eight cases, because the defect this pins was a
        // bound: the decoder read reasons 1–7 while the scanner was already
        // emitting 8 for a name with no faithful UTF-8 form (06 §4.3), so a
        // backup that met one wrote an error manifest that would not open
        // again. A bound is tested across its whole range and at its edge.
        for (var reason = 1; reason <= 8; reason++)
        {
            var manifest = new ErrorManifest(
            [
                new CaptureFailure(["srv"u8.ToArray(), "thing"u8.ToArray()], (CaptureFailureReason)reason, "why"),
            ]);

            var decoded = ErrorManifestCodec.Decode(ErrorManifestCodec.Encode(manifest));

            Assert.AreEqual(
                (CaptureFailureReason)reason,
                Assert.ContainsSingle(decoded.Failures).Reason,
                $"reason {reason} is assigned by 06 §8.1 and must survive the round trip");
        }
    }

    [TestMethod]
    public void MetadataDigest_NoSecurityDescriptorAndAnEmptyOne_DigestAlike_AndARealOneStillCounts()
    {
        // Every build before the capture stopped writing an empty descriptor
        // recorded the digest with it. Told apart, every file captured off
        // Windows would look changed once, and its manifest be rewritten
        // (NFR-PERF-003).
        var none = new EntryMetadata { ModifiedAt = 1_700_000_000_000, PosixMode = 0x1A4, OwnerName = "ana" };
        var empty = none with { WindowsSecurityDescriptor = ReadOnlyMemory<byte>.Empty };
        var real = none with { WindowsSecurityDescriptor = new byte[] { 1, 0, 4, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } };

        CollectionAssert.AreEqual(FileVersionManifestCodec.MetadataDigest(empty), FileVersionManifestCodec.MetadataDigest(none));
        CollectionAssert.AreNotEqual(FileVersionManifestCodec.MetadataDigest(none), FileVersionManifestCodec.MetadataDigest(real));
    }

    [TestMethod]
    public void MetadataDigest_AFlagKeptInKeyEleven_Counts()
    {
        // An incremental passes over a file whose digest is unchanged, so a
        // file whose flags alone changed must digest otherwise (ADR-0184 §1).
        var plain = new EntryMetadata { ModifiedAt = 1_700_000_000_000, PosixMode = 0x1A4, OwnerName = "ana" };
        var noDump = plain with { FileFlags = FileFlagBits.NoDump };

        CollectionAssert.AreNotEqual(FileVersionManifestCodec.MetadataDigest(plain), FileVersionManifestCodec.MetadataDigest(noDump));
        CollectionAssert.AreNotEqual(
            FileVersionManifestCodec.MetadataDigest(noDump),
            FileVersionManifestCodec.MetadataDigest(plain with { FileFlags = FileFlagBits.Immutable }));
    }

    [TestMethod]
    public void ErrorManifest_AnUnassignedReason_IsRefused()
    {
        // The other edge of the same bound. Casting an out-of-range value into
        // the enum reaches the encoder the way a future writer's manifest
        // would, without hand-patching CBOR bytes to get there.
        foreach (var unassigned in (int[])[0, 9])
        {
            var manifest = new ErrorManifest(
            [
                new CaptureFailure(["srv"u8.ToArray()], (CaptureFailureReason)unassigned, "why"),
            ]);

            var encoded = ErrorManifestCodec.Encode(manifest);

            Assert.ThrowsExactly<ManifestValidationException>(
                () => ErrorManifestCodec.Decode(encoded),
                $"reason {unassigned} is unassigned and must not decode");
        }
    }
}
