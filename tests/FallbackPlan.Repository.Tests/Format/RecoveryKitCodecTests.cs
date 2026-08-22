using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.RecoveryKit;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// The write-only kit shape at the codec (ADR-0042 §8; recovery-kit spec
/// §2.1): key 11 carries the 32-byte sealing public key, key 5 MUST be
/// empty, and the contradictions — a v2 kit smuggling a key object, a v1
/// kit claiming a sealing key, a wrong-length key — are refused in the
/// serialize direction here (the parse direction runs the same
/// <c>Validate</c>, exercised by the conformance refusal vectors).
/// </summary>
[TestClass]
public sealed class RecoveryKitCodecTests
{
    private static RecoveryKit V2Kit() => new()
    {
        KitFormatVersion = 1,
        MinimumToolVersion = "0.1.0",
        RepositoryId = RepositoryId.FromBytes(Convert.FromHexString("1112131415161718191a1b1c1d1e1f20")),
        RepositoryFormatVersion = FallbackPlan.Domain.FormatLimits.SealedFormatVersion,
        KeyObject = ReadOnlyMemory<byte>.Empty,
        KdfMemoryKiB = 8 * 1024,
        KdfIterations = 1,
        KdfParallelism = 1,
        KdfSalt = Enumerable.Repeat((byte)0x21, 16).ToArray(),
        Destinations = [new KitDestination("local-path", "file:///drill", "", "")],
        IssuingDeviceId = Enumerable.Repeat((byte)0x55, 16).ToArray(),
        IssuedAt = 1_722_600_000_000,
        Instructions = "derive, compare, restore",
        SealingPublicKey = Enumerable.Repeat((byte)0x9C, 32).ToArray(),
    };

    [TestMethod]
    public void RecoveryKitV2_SerialisedAndParsed_RoundTripsTheElevenKeyBody()
    {
        var parsed = RecoveryKitCodec.Parse(RecoveryKitCodec.Serialize(V2Kit()));

        Assert.AreEqual(FallbackPlan.Domain.FormatLimits.SealedFormatVersion, parsed.RepositoryFormatVersion);
        Assert.IsTrue(parsed.KeyObject.IsEmpty, "a write-only kit carries no key material at all");
        SequenceAssert.AreEqual(V2Kit().SealingPublicKey.ToArray(), parsed.SealingPublicKey.ToArray());
        SequenceAssert.AreEqual(V2Kit().KdfSalt.ToArray(), parsed.KdfSalt.ToArray());
        Assert.AreEqual("derive, compare, restore", parsed.Instructions);
    }

    [TestMethod]
    public void RecoveryKitV2_AContradictoryShape_IsRefusedByName()
    {
        // A v2 kit smuggling a key object — the forgery §2.1 names.
        Assert.ThrowsExactly<RecoveryKitFormatException>(() => RecoveryKitCodec.Serialize(
            V2Kit() with { KeyObject = "FBPKKEYS-and-some-wrapped-bytes"u8.ToArray() }));

        // A sealing key that is not 32 bytes.
        Assert.ThrowsExactly<RecoveryKitFormatException>(() => RecoveryKitCodec.Serialize(
            V2Kit() with { SealingPublicKey = Enumerable.Repeat((byte)0x9C, 16).ToArray() }));

        // A v1 kit claiming a sealing key it has no business carrying.
        Assert.ThrowsExactly<RecoveryKitFormatException>(() => RecoveryKitCodec.Serialize(
            V2Kit() with
            {
                RepositoryFormatVersion = FallbackPlan.Domain.FormatLimits.FormatVersion,
                KeyObject = "FBPKKEYS-shaped-key-object-bytes"u8.ToArray(),
            }));

        // A v2 kit with NO sealing key — nothing left to verify against.
        Assert.ThrowsExactly<RecoveryKitFormatException>(() => RecoveryKitCodec.Serialize(
            V2Kit() with { SealingPublicKey = ReadOnlyMemory<byte>.Empty }));
    }
}
