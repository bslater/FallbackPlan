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
/// </summary>
[TestClass]
public sealed class RepositoryDescriptorCodecTests
{
    private static RepositoryDescriptor Sample(bool unstable = true) => new(
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10")),
        FormatVersion: 1,
        RequiredFeatures: [],
        OptionalFeatures: [7],
        new Argon2Parameters { MemoryKiB = 65536, Iterations = 3, Parallelism = 4 },
        KdfSalt: Enumerable.Range(0, 16).Select(value => (byte)value).ToArray(),
        CreatedAt: 1_722_600_000_000,
        CreatedBy: "fallbackplan-tests/1.0",
        UnstableFormat: unstable);

    [TestMethod]
    public void A_descriptor_round_trips()
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

    [TestMethod]
    public void Serialization_is_deterministic()
    {
        SequenceAssert.AreEqual(
            RepositoryDescriptorCodec.Serialize(Sample()),
            RepositoryDescriptorCodec.Serialize(Sample()));
    }

    [TestMethod]
    public void An_object_without_the_magic_is_not_a_repository_not_a_parse_error()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample());
        bytes[0] ^= 0x01;

        Assert.IsInstanceOfType<DescriptorParseResult.NotARepository>(RepositoryDescriptorCodec.Parse(bytes));
    }

    [TestMethod]
    public void A_flipped_body_byte_is_an_integrity_failure_not_a_parse_error()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample());
        bytes[RepositoryDescriptorCodec.HeaderLength + 3] ^= 0x01;

        // The digest is verified BEFORE the body is interpreted (01 §3.1) —
        // corruption surfaces as corruption, never as a confusing CBOR error.
        Assert.IsInstanceOfType<DescriptorParseResult.IntegrityFailure>(RepositoryDescriptorCodec.Parse(bytes));
    }

    [TestMethod]
    public void A_truncated_descriptor_is_a_format_violation()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample());

        Assert.IsInstanceOfType<DescriptorParseResult.FormatViolation>(
            RepositoryDescriptorCodec.Parse(bytes.AsMemory(0, bytes.Length - 1)));
    }

    [TestMethod]
    public void A_nonzero_reserved_field_is_ignored_on_read()
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
    public void An_unknown_required_feature_is_refused_and_named()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample() with { RequiredFeatures = [0x0042] });

        Assert.IsInstanceOfType<DescriptorParseResult.UnsupportedRequiredFeatures>(RepositoryDescriptorCodec.Parse(bytes), out var refused);

        SequenceAssert.AreEqual<ushort>([0x0042], refused.Features);
    }

    [TestMethod]
    public void A_declared_body_length_over_the_limit_is_refused_before_allocation()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample());
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), 200_000);

        Assert.IsInstanceOfType<DescriptorParseResult.FormatViolation>(RepositoryDescriptorCodec.Parse(bytes), out var violation);
        Assert.Contains("65 536", violation.Message, StringComparison.Ordinal);
    }
}
