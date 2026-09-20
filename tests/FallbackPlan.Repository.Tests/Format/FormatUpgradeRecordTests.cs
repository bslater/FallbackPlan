using FallbackPlan.Domain;
using FallbackPlan.Repository.Format.Lifecycle;
using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// The format-upgrade record (specification 11 §5): the append-only,
/// signed statement that a repository now writes a newer format. It is the
/// one lifecycle object whose absence is meaningful — a repository with none
/// is at the version its descriptor was created with — so its encoding, the
/// bytes its signature covers and every refusal are pinned before anything
/// writes one.
/// </summary>
/// <remarks>Establishes FR-MAN-019 and NFR-COMP-004.</remarks>
[TestClass]
public sealed class FormatUpgradeRecordTests
{
    private static readonly byte[] WriterId = [.. Enumerable.Repeat((byte)5, 16)];
    private static readonly byte[] Signature = [.. Enumerable.Repeat((byte)3, 64)];

    private static FormatUpgradeRecord Upgrade(
        ushort from = FormatVersions.SealedDataPlane,
        ushort to = FormatVersions.RelocatableRecords) =>
        new(from, to, UpgradedAtUnixMilliseconds: 1_700_000_000_000, WriterId);

    [TestMethod]
    public void RoundTrip_AnUpgradeRecord_PreservesEveryFieldAndTheSignedPrefix()
    {
        var record = Upgrade();

        var decoded = FormatUpgradeRecordCodec.Decode(FormatUpgradeRecordCodec.Encode(record, Signature));

        Assert.AreEqual(FormatVersions.SealedDataPlane, decoded.Value.FromVersion);
        Assert.AreEqual(FormatVersions.RelocatableRecords, decoded.Value.ToVersion);
        Assert.AreEqual(1_700_000_000_000UL, decoded.Value.UpgradedAtUnixMilliseconds);
        Assert.IsTrue(decoded.Value.WriterId.Span.SequenceEqual(WriterId));
        Assert.IsTrue(decoded.Signature.Span.SequenceEqual(Signature));

        // The signed prefix rebuilt from the decode is byte-identical to what
        // the signer saw, or verification is a lottery.
        Assert.IsTrue(decoded.SignedBytes.Span.SequenceEqual(FormatUpgradeRecordCodec.EncodeForSigning(record)));
    }

    [TestMethod]
    public void EncodeForSigning_ExcludesTheSignature_SoWhatIsSignedDoesNotDependOnIt()
    {
        var signed = FormatUpgradeRecordCodec.EncodeForSigning(Upgrade());

        // A canonical map carries its own count, so the signed bytes are not
        // a byte prefix of the stored ones. What matters is that they do not
        // depend on the signature at all: two records signed differently are
        // signed over the same bytes, and a verifier rebuilding them from a
        // decode cannot be steered by the signature it is about to check.
        var another = new byte[64];
        another[0] = 0xFF;

        Assert.IsTrue(
            FormatUpgradeRecordCodec.Decode(FormatUpgradeRecordCodec.Encode(Upgrade(), Signature))
                .SignedBytes.Span.SequenceEqual(signed));
        Assert.IsTrue(
            FormatUpgradeRecordCodec.Decode(FormatUpgradeRecordCodec.Encode(Upgrade(), another))
                .SignedBytes.Span.SequenceEqual(signed));
    }

    [TestMethod]
    public void Decode_TrailingBytes_AreRefused()
    {
        // A record is one CBOR document. Bytes after it are either damage or
        // an attempt to have two readers disagree about what was signed.
        var stored = FormatUpgradeRecordCodec.Encode(Upgrade(), Signature);
        var padded = new byte[stored.Length + 1];
        stored.CopyTo(padded, 0);

        Assert.ThrowsExactly<ManifestValidationException>(() => FormatUpgradeRecordCodec.Decode(padded));
    }

    [TestMethod]
    public void Encode_ASignatureOfTheWrongWidth_IsRefused()
    {
        Assert.ThrowsExactly<ManifestValidationException>(
            () => FormatUpgradeRecordCodec.Encode(Upgrade(), new byte[63]));
    }

    [TestMethod]
    public void Encode_AWriterIdentityOfTheWrongWidth_IsRefused()
    {
        var wrong = Upgrade() with { WriterId = new byte[15] };

        Assert.ThrowsExactly<ManifestValidationException>(() => FormatUpgradeRecordCodec.Encode(wrong, Signature));
    }

    [TestMethod]
    public void Encode_AnUpgradeThatDoesNotMoveForward_IsRefused()
    {
        // A record naming a version at or below the one it came from is not
        // an upgrade. Admitting one would let a stranger's file argue a
        // repository backwards, which is the one direction the effective
        // version must never take.
        var sideways = Upgrade(FormatVersions.RelocatableRecords, FormatVersions.RelocatableRecords);
        var backwards = Upgrade(FormatVersions.RelocatableRecords, FormatVersions.SealedDataPlane);

        Assert.ThrowsExactly<ManifestValidationException>(() => FormatUpgradeRecordCodec.Encode(sideways, Signature));
        Assert.ThrowsExactly<ManifestValidationException>(() => FormatUpgradeRecordCodec.Encode(backwards, Signature));
    }

    [TestMethod]
    public void Decode_ASchemaVersionThisBuildDoesNotRead_IsRefusedByName()
    {
        var stored = FormatUpgradeRecordCodec.Encode(Upgrade(), Signature);

        // Key 1 is the schema version and is the first entry of a canonical
        // map, so the value byte immediately after the key is the one to move.
        var index = Array.IndexOf(stored, (byte)0x01);
        stored[index + 1] = 0x02;

        var refusal = Assert.ThrowsExactly<ManifestValidationException>(
            () => FormatUpgradeRecordCodec.Decode(stored));
        Assert.Contains("version 1", refusal.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void EffectiveVersion_WithNoRecords_IsTheDescriptorsOwn()
    {
        Assert.AreEqual(
            FormatVersions.SealedDataPlane,
            FormatUpgradeRecordCodec.EffectiveVersion(
                FormatVersions.SealedDataPlane, [], _ => true));
    }

    [TestMethod]
    public void EffectiveVersion_IsTheHighestRecordAVerifierAccepts()
    {
        // Not the newest file written and not the last one listed: the
        // highest claim that verifies. A repository that carries 2→3 and
        // 3→4 is at 4 whatever order the listing produced them in.
        ReadOnlyMemory<byte>[] records =
        [
            Stored(FormatVersions.RelocatableRecords, 4),
            Stored(FormatVersions.SealedDataPlane, FormatVersions.RelocatableRecords),
        ];

        Assert.AreEqual(
            (ushort)4,
            FormatUpgradeRecordCodec.EffectiveVersion(FormatVersions.SealedDataPlane, records, _ => true));
    }

    [TestMethod]
    public void EffectiveVersion_ARecordTheVerifierRejects_IsIgnored()
    {
        // The rule both callers ride on: an unverifiable claim about the
        // format is a claim nobody made. The engine's verifier is the
        // repository's signing key; the recovery tool's is its own; neither
        // gets to decide this differently.
        ReadOnlyMemory<byte>[] records =
        [
            Stored(FormatVersions.SealedDataPlane, FormatVersions.RelocatableRecords),
        ];

        Assert.AreEqual(
            FormatVersions.SealedDataPlane,
            FormatUpgradeRecordCodec.EffectiveVersion(FormatVersions.SealedDataPlane, records, _ => false));
    }

    [TestMethod]
    public void EffectiveVersion_ARecordThatDoesNotDecode_IsIgnoredAndItsNeighboursStillCount()
    {
        ReadOnlyMemory<byte>[] records =
        [
            new byte[] { 0x01, 0x02, 0x03 },
            Stored(FormatVersions.SealedDataPlane, FormatVersions.RelocatableRecords),
        ];

        Assert.AreEqual(
            FormatVersions.RelocatableRecords,
            FormatUpgradeRecordCodec.EffectiveVersion(FormatVersions.SealedDataPlane, records, _ => true));
    }

    [TestMethod]
    public void EffectiveVersion_ARecordBelowTheDescriptor_NeverMovesItBackwards()
    {
        // A repository created at 3 carrying a perfectly valid 1→2 record is
        // still at 3. Backwards is the one direction the answer must never
        // take, and it is the caller's descriptor that sets the floor.
        ReadOnlyMemory<byte>[] records =
        [
            Stored(FormatVersions.Symmetric, FormatVersions.SealedDataPlane),
        ];

        Assert.AreEqual(
            FormatVersions.RelocatableRecords,
            FormatUpgradeRecordCodec.EffectiveVersion(
                FormatVersions.RelocatableRecords, records, _ => true));
    }

    [TestMethod]
    public void EffectiveVersion_HandsTheVerifierWhatItNeedsToCheck()
    {
        // The verifier is given the decoded record, signature and signed
        // bytes together — a caller that had to rebuild the signed bytes
        // itself could rebuild them differently, which is how a shared rule
        // stops being shared.
        var record = Upgrade();
        DecodedFormatUpgradeRecord? seen = null;

        _ = FormatUpgradeRecordCodec.EffectiveVersion(
            FormatVersions.SealedDataPlane,
            [FormatUpgradeRecordCodec.Encode(record, Signature)],
            decoded =>
            {
                seen = decoded;
                return true;
            });

        Assert.IsNotNull(seen);
        Assert.IsTrue(seen.Signature.Span.SequenceEqual(Signature));
        Assert.IsTrue(seen.SignedBytes.Span.SequenceEqual(FormatUpgradeRecordCodec.EncodeForSigning(record)));
    }

    private static byte[] Stored(ushort from, ushort to) =>
        FormatUpgradeRecordCodec.Encode(Upgrade(from, to), Signature);

    [TestMethod]
    public void Key_ForAVersion_IsTheStablePrefixAndFourLowercaseHexDigits()
    {
        // A prefix, not a bare key, so a repository can carry 2→3 now and
        // 3→4 later and a listing answers what it has been through.
        Assert.AreEqual("format-upgrade/0003", FormatUpgradeRecordCodec.KeyFor(FormatVersions.RelocatableRecords));
        Assert.AreEqual("format-upgrade/", FormatUpgradeRecordCodec.KeyPrefix);
        Assert.StartsWith(FormatUpgradeRecordCodec.KeyPrefix, FormatUpgradeRecordCodec.KeyFor(255));
        Assert.AreEqual("format-upgrade/00ff", FormatUpgradeRecordCodec.KeyFor(255));
    }
}
