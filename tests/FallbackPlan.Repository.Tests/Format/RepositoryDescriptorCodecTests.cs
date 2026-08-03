using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Descriptor;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// The repository descriptor codec (specification 01 §3; FR-REP-002,
/// NFR-COMP-003): round-trips canonically, distinguishes "not a repository"
/// from damage, verifies the digest before interpreting the body, ignores
/// the reserved field, and refuses unknown required features by name.
/// </summary>
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

    [Fact]
    public void A_descriptor_round_trips()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample());

        var ok = Assert.IsType<DescriptorParseResult.Ok>(RepositoryDescriptorCodec.Parse(bytes));

        Assert.Equal(Sample().RepositoryId, ok.Descriptor.RepositoryId);
        Assert.Equal(Sample().KdfParameters, ok.Descriptor.KdfParameters);
        Assert.Equal(Sample().KdfSalt.ToArray(), ok.Descriptor.KdfSalt.ToArray());
        Assert.Equal(Sample().CreatedBy, ok.Descriptor.CreatedBy);
        Assert.True(ok.Descriptor.UnstableFormat);
        Assert.Equal([7], ok.Descriptor.OptionalFeatures);
    }

    [Fact]
    public void Serialization_is_deterministic()
    {
        Assert.Equal(
            RepositoryDescriptorCodec.Serialize(Sample()),
            RepositoryDescriptorCodec.Serialize(Sample()));
    }

    [Fact]
    public void An_object_without_the_magic_is_not_a_repository_not_a_parse_error()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample());
        bytes[0] ^= 0x01;

        Assert.IsType<DescriptorParseResult.NotARepository>(RepositoryDescriptorCodec.Parse(bytes));
    }

    [Fact]
    public void A_flipped_body_byte_is_an_integrity_failure_not_a_parse_error()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample());
        bytes[RepositoryDescriptorCodec.HeaderLength + 3] ^= 0x01;

        // The digest is verified BEFORE the body is interpreted (01 §3.1) —
        // corruption surfaces as corruption, never as a confusing CBOR error.
        Assert.IsType<DescriptorParseResult.IntegrityFailure>(RepositoryDescriptorCodec.Parse(bytes));
    }

    [Fact]
    public void A_truncated_descriptor_is_a_format_violation()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample());

        Assert.IsType<DescriptorParseResult.FormatViolation>(
            RepositoryDescriptorCodec.Parse(bytes.AsMemory(0, bytes.Length - 1)));
    }

    [Fact]
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

        Assert.IsType<DescriptorParseResult.Ok>(RepositoryDescriptorCodec.Parse(bytes));
    }

    [Fact]
    public void An_unknown_required_feature_is_refused_and_named()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample() with { RequiredFeatures = [0x0042] });

        var refused = Assert.IsType<DescriptorParseResult.UnsupportedRequiredFeatures>(
            RepositoryDescriptorCodec.Parse(bytes));

        Assert.Equal([0x0042], refused.Features);
    }

    [Fact]
    public void A_declared_body_length_over_the_limit_is_refused_before_allocation()
    {
        var bytes = RepositoryDescriptorCodec.Serialize(Sample());
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), 200_000);

        var violation = Assert.IsType<DescriptorParseResult.FormatViolation>(RepositoryDescriptorCodec.Parse(bytes));
        Assert.Contains("65 536", violation.Message, StringComparison.Ordinal);
    }
}
