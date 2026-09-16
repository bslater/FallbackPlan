using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// The repository descriptor codec (specification 01 §3; FR-REP-002,
/// NFR-COMP-003): round-trips canonically, distinguishes "not a repository"
/// from damage, verifies the digest before interpreting the body, ignores
/// the reserved field, and refuses unknown required features by name.
/// Establishes NFR-COMP-001 and NFR-COMP-002.
/// </summary>
[TestClass]
public sealed class RepositoryDescriptorCodecTests
{
    private static RepositoryDescriptor Sample(bool unstable = true) => new(
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10")),
        FallbackPlan.Domain.FormatLimits.FormatVersion,
        RequiredFeatures: [RepositoryDescriptorCodec.FeatureSealedDataPlane],
        OptionalFeatures: [7],
        new Argon2Parameters { MemoryKiB = 65536, Iterations = 3, Parallelism = 4 },
        KdfSalt: Enumerable.Range(0, 16).Select(value => (byte)value).ToArray(),
        CreatedAt: 1_722_600_000_000,
        CreatedBy: "fallbackplan-tests/1.0",
        UnstableFormat: unstable,
        SealingPublicKey: Enumerable.Repeat((byte)0xAB, 32).ToArray());

    [TestMethod]
    public void RepositoryDescriptor_EncodedAndDecoded_RoundTrips()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample());

        Assert.IsInstanceOfType<DescriptorParseResult.Ok>(RepositoryDescriptorCodec.Parse(bytes), out var ok);

        Assert.AreEqual(Sample().RepositoryId, ok.Descriptor.RepositoryId);
        Assert.AreEqual(Sample().KdfParameters, ok.Descriptor.KdfParameters);
        SequenceAssert.AreEqual(Sample().KdfSalt.ToArray(), ok.Descriptor.KdfSalt.ToArray());
        Assert.AreEqual(Sample().CreatedBy, ok.Descriptor.CreatedBy);
        Assert.IsTrue(ok.Descriptor.UnstableFormat);
        SequenceAssert.AreEqual<ushort>([7], ok.Descriptor.OptionalFeatures);
    }

    private static RepositoryDescriptor SampleV2() => Sample();

    [TestMethod]
    public void RepositoryDescriptorV2_EncodedAndDecoded_RoundTripsTheSealingKey()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(SampleV2());

        // A format-v2 descriptor (ADR-0042 §1): the sealing public key rides
        // key 9, the sealed-data-plane feature is required, and this reader
        // implements it — so the parse proceeds rather than refusing.
        Assert.IsInstanceOfType<DescriptorParseResult.Ok>(RepositoryDescriptorCodec.Parse(bytes), out var ok);
        Assert.AreEqual(FallbackPlan.Domain.FormatLimits.FormatVersion, ok.Descriptor.FormatVersion);
        SequenceAssert.AreEqual(
            SampleV2().SealingPublicKey.ToArray(), ok.Descriptor.SealingPublicKey.ToArray());
        SequenceAssert.AreEqual<ushort>(
            [RepositoryDescriptorCodec.FeatureSealedDataPlane], ok.Descriptor.RequiredFeatures);
    }

    [TestMethod]
    public void RepositoryDescriptorV2_TheKeyAndVersionDisagreeing_IsRefusedAtSerialize()
    {
        // A descriptor without its verifier is a caller bug — refused before
        // anything is stored. So is a format-1 stamp: format 1 is withdrawn,
        // and nothing writes it.
        Assert.ThrowsExactly<ArgumentException>(() => RepositoryDescriptorCodec.Serialize(
            SampleV2() with { SealingPublicKey = ReadOnlyMemory<byte>.Empty }));
        Assert.ThrowsExactly<ArgumentException>(() => RepositoryDescriptorCodec.Serialize(
            Sample() with { FormatVersion = 1, SealingPublicKey = ReadOnlyMemory<byte>.Empty }));
    }

    [TestMethod]
    public void RepositoryDescriptor_AFormatOneStamp_IsRefusedByNameNotMisread()
    {
        // ADR-0014's rule: refuse, never misread. A stored descriptor stamped
        // format 1 is a distinct finding with its remedy named — not "not a
        // repository", not damage, not an unknown feature. Both copies of the
        // version move (framing at offset 8, body key 2 at offset 36) and the
        // digest is re-sealed, so the refusal is the version's alone.
        var bytes = RepositoryDescriptorCodec.Serialize(Sample());
        bytes[9] = 1;
        const int bodyVersion = RepositoryDescriptorCodec.HeaderLength + 1 + 1 + 1 + 16 + 1;
        Assert.AreEqual(0x02, bytes[bodyVersion]);
        bytes[bodyVersion] = 0x01;
        System.Security.Cryptography.SHA256.HashData(
            bytes.AsSpan(0, bytes.Length - RepositoryDescriptorCodec.DigestLength),
            bytes.AsSpan(bytes.Length - RepositoryDescriptorCodec.DigestLength));

        Assert.IsInstanceOfType<DescriptorParseResult.FormatViolation>(RepositoryDescriptorCodec.Parse(bytes), out var violation);
        Assert.Contains("format 1", violation.Message, StringComparison.Ordinal);
        Assert.Contains("withdrawn", violation.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void RepositoryDescriptorV2_TheSealingKeyTampered_IsAnIntegrityFailure()
    {
        // Self-locating tamper: serialize twice with one sealing-key byte
        // changed, and the first differing offset IS the encoded key. A
        // swapped verifier must never parse as valid — the digest refuses it
        // as damage before any derive-and-compare could be misled.
        var bytes = RepositoryDescriptorCodec.Serialize(SampleV2());
        var mutatedKey = SampleV2().SealingPublicKey.ToArray();
        mutatedKey[5] ^= 0x01;
        var mutated = RepositoryDescriptorCodec.Serialize(SampleV2() with { SealingPublicKey = mutatedKey });

        var keyOffset = Enumerable.Range(0, bytes.Length).First(i => bytes[i] != mutated[i]);
        bytes[keyOffset] ^= 0x01;

        Assert.IsInstanceOfType<DescriptorParseResult.IntegrityFailure>(RepositoryDescriptorCodec.Parse(bytes));
    }

    [TestMethod]
    public void RepositoryDescriptorV2_AnUnknownRequiredFeatureBesideTheSealedPlane_IsRefused()
    {
        // The real sealed-data-plane bit passes; anything unknown beside it
        // still refuses cleanly through the required-features path — a v3
        // capability never half-reads on a v2-only build.
        var bytes = RepositoryDescriptorCodec.Serialize(SampleV2() with
        {
            RequiredFeatures = [RepositoryDescriptorCodec.FeatureSealedDataPlane, 0x7F],
        });

        Assert.IsInstanceOfType<DescriptorParseResult.UnsupportedRequiredFeatures>(
            RepositoryDescriptorCodec.Parse(bytes));
    }

    [TestMethod]
    public void RepositoryDescriptor_EncodedTwice_ProducesIdenticalBytes()
    {
        SequenceAssert.AreEqual(
            RepositoryDescriptorCodec.Serialize(Sample()),
            RepositoryDescriptorCodec.Serialize(Sample()));
    }

    [TestMethod]
    public void RepositoryDescriptor_MagicIsAbsent_SaysItIsNotARepositoryRatherThanAParseError()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample());
        bytes[0] ^= 0x01;

        Assert.IsInstanceOfType<DescriptorParseResult.NotARepository>(RepositoryDescriptorCodec.Parse(bytes));
    }

    [TestMethod]
    public void RepositoryDescriptor_ABodyByteIsFlipped_IsAnIntegrityFailureRatherThanAParseError()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample());
        bytes[RepositoryDescriptorCodec.HeaderLength + 3] ^= 0x01;

        // The digest is verified BEFORE the body is interpreted (01 §3.1) —
        // corruption surfaces as corruption, never as a confusing CBOR error.
        Assert.IsInstanceOfType<DescriptorParseResult.IntegrityFailure>(RepositoryDescriptorCodec.Parse(bytes));
    }

    [TestMethod]
    public void RepositoryDescriptor_Truncated_IsAFormatViolation()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample());

        Assert.IsInstanceOfType<DescriptorParseResult.FormatViolation>(
            RepositoryDescriptorCodec.Parse(bytes.AsMemory(0, bytes.Length - 1)));
    }

    [TestMethod]
    public void RepositoryDescriptor_ReservedFieldIsNonZero_IsIgnoredOnRead()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample());

        // 00 §9: reserved fields are written zero and ignored on read — but
        // the digest covers them, so re-seal the object after the change to
        // isolate exactly the reserved-field rule.
        bytes[10] = 0xFF;
        System.Security.Cryptography.SHA256.HashData(
            bytes.AsSpan(0, bytes.Length - RepositoryDescriptorCodec.DigestLength),
            bytes.AsSpan(bytes.Length - RepositoryDescriptorCodec.DigestLength));

        Assert.IsInstanceOfType<DescriptorParseResult.Ok>(RepositoryDescriptorCodec.Parse(bytes));
    }

    [TestMethod]
    public void RepositoryDescriptor_ARequiredFeatureIsUnknown_IsRefusedAndNamed()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample() with { RequiredFeatures = [0x0042] });

        Assert.IsInstanceOfType<DescriptorParseResult.UnsupportedRequiredFeatures>(RepositoryDescriptorCodec.Parse(bytes), out var refused);

        SequenceAssert.AreEqual<ushort>([0x0042], refused.Features);
    }

    [TestMethod]
    public void RepositoryDescriptor_DeclaredBodyLengthExceedsTheLimit_IsRefusedBeforeAllocation()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample());
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), 200_000);

        Assert.IsInstanceOfType<DescriptorParseResult.FormatViolation>(RepositoryDescriptorCodec.Parse(bytes), out var violation);
        Assert.Contains("65 536", violation.Message, StringComparison.Ordinal);
    }
}
