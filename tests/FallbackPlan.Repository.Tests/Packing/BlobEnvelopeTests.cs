using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Packing;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Packing;

/// <summary>
/// Exercises the 88-byte blob envelope byte-exactly (specification 05 §2).
/// </summary>
[TestClass]
public sealed class BlobEnvelopeTests
{
    private static BlobEnvelope Sample() => new(
        FormatLimits.FormatVersion,
        BlobClass.Data,
        new KeyGeneration(3),
        BlobId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7000000000000002a")),
        Enumerable.Repeat((byte)0x5A, 32).ToArray(),
        42,
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf")));

    private static byte[] SampleBytes()
    {
        var bytes = new byte[BlobEnvelope.Length];
        Sample().WriteTo(bytes);
        return bytes;
    }

    [TestMethod]
    public void BlobEnvelope_ASerialisedEnvelope_PlacesEveryFieldAtItsSpecifiedOffset()
    {
        var bytes = SampleBytes();

        SequenceAssert.AreEqual("FBPKBLOB"u8.ToArray(), bytes[..8]);
        SequenceAssert.AreEqual(new byte[] { 0x00, 0x01 }, bytes[8..10]);   // format_version
        SequenceAssert.AreEqual(new byte[] { 0x00, 0x01 }, bytes[10..12]);  // blob_class data
        SequenceAssert.AreEqual(new byte[] { 0x00, 0x00, 0x00, 0x03 }, bytes[12..16]); // generation
        Assert.AreEqual(0xA0, bytes[16]);                            // blob_id
        Assert.AreEqual(0x5A, bytes[32]);                            // blob_salt
        Assert.AreEqual(0x2A, bytes[71]);                            // blob_counter 42 BE
        Assert.AreEqual(0xA0, bytes[72]);                            // writer_id
        Assert.AreEqual(0xAF, bytes[87]);
    }

    [TestMethod]
    public void BlobEnvelope_SerialisedThenParsed_RoundTrips()
    {
        var parsed = BlobEnvelope.Parse(SampleBytes());

        Assert.AreEqual(BlobClass.Data, parsed.BlobClass);
        Assert.AreEqual(3u, parsed.KeyGeneration.Value);
        Assert.AreEqual(42UL, parsed.BlobCounter);
        Assert.IsTrue(parsed.BlobSalt.SequenceEqual(Enumerable.Repeat((byte)0x5A, 32).ToArray()));
    }

    [TestMethod]
    public void BlobEnvelope_MagicIsAbsent_SaysItIsNotABlob()
    {
        var bytes = SampleBytes();
        bytes[0] = (byte)'X';

        var exception = Assert.ThrowsExactly<BlobFormatException>(() => BlobEnvelope.Parse(bytes));
        Assert.Contains("Not a FallbackPlan blob", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void BlobEnvelope_BlobClassIsUndefined_IsRefused()
    {
        var bytes = SampleBytes();
        bytes[11] = 0x03;

        Assert.ThrowsExactly<BlobFormatException>(() => BlobEnvelope.Parse(bytes));
    }

    // ---- the 168-byte sealed form (specification 05 §2.1) -----------------

    private static byte[] SealedShare() => [.. Enumerable.Range(0x10, 80).Select(value => (byte)value)];

    private static BlobEnvelope SealedSample() => new(
        FormatLimits.SealedFormatVersion,
        BlobClass.Data,
        new KeyGeneration(3),
        BlobId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7000000000000002a")),
        Enumerable.Repeat((byte)0x5A, 32).ToArray(),
        42,
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf")),
        SealedShare());

    private static byte[] SealedSampleBytes()
    {
        var bytes = new byte[BlobEnvelope.MaxLength];
        SealedSample().WriteTo(bytes);
        return bytes;
    }

    [TestMethod]
    public void BlobEnvelope_ASealedEnvelope_PlacesTheSealedShareAtItsSpecifiedOffset()
    {
        var envelope = SealedSample();
        Assert.AreEqual(BlobEnvelope.MaxLength, envelope.EnvelopeLength);

        var bytes = SealedSampleBytes();

        // The first 88 bytes are the v1 layout verbatim, version 2 stamped.
        SequenceAssert.AreEqual("FBPKBLOB"u8.ToArray(), bytes[..8]);
        SequenceAssert.AreEqual(new byte[] { 0x00, 0x02 }, bytes[8..10]);   // format_version
        SequenceAssert.AreEqual(new byte[] { 0x00, 0x01 }, bytes[10..12]);  // blob_class data
        Assert.AreEqual(0xA0, bytes[72]);                                   // writer_id still at 72

        // The sealed content key occupies exactly [88, 168) (05 §2.1).
        Assert.AreEqual(0x10, bytes[88]);
        Assert.AreEqual(0x5F, bytes[167]);
        SequenceAssert.AreEqual(SealedShare(), bytes[88..168]);
    }

    [TestMethod]
    public void BlobEnvelope_ASealedEnvelope_RoundTrips()
    {
        var parsed = BlobEnvelope.Parse(SealedSampleBytes());

        Assert.AreEqual(FormatLimits.SealedFormatVersion, parsed.FormatVersion);
        Assert.AreEqual(BlobClass.Data, parsed.BlobClass);
        Assert.AreEqual(42UL, parsed.BlobCounter);
        SequenceAssert.AreEqual(SealedShare(), parsed.SealedContentKey.ToArray());
    }

    [TestMethod]
    public void BlobEnvelope_ASealedEnvelopeCutTo88Bytes_IsDamagedNotShorter()
    {
        // A v2 data blob DECLARES the 168-byte shape in its first 10 bytes;
        // 88 bytes of it are a truncation, refused as damage — never parsed
        // as a share-less envelope that would then read as v1-shaped.
        var refusal = Assert.ThrowsExactly<BlobFormatException>(
            () => BlobEnvelope.Parse(SealedSampleBytes().AsSpan(0, BlobEnvelope.Length)));
        Assert.Contains("sealed", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void BlobEnvelope_TheShareAndTheShapeDisagreeing_IsRefusedAtConstruction()
    {
        var blobId = BlobId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7000000000000002a"));
        var writer = WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));
        var salt = Enumerable.Repeat((byte)0x5A, 32).ToArray();

        // A v2 data blob without a share; a v1 data blob or a v2 metadata
        // blob WITH one; a share of the wrong length — each refused by the
        // single presence rule (05 §2.1): sealed ⟺ v2 ∧ data.
        Assert.ThrowsExactly<ArgumentException>(() => new BlobEnvelope(
            FormatLimits.SealedFormatVersion, BlobClass.Data, new KeyGeneration(0), blobId, salt, 1, writer));
        Assert.ThrowsExactly<ArgumentException>(() => new BlobEnvelope(
            FormatLimits.FormatVersion, BlobClass.Data, new KeyGeneration(0), blobId, salt, 1, writer, SealedShare()));
        Assert.ThrowsExactly<ArgumentException>(() => new BlobEnvelope(
            FormatLimits.SealedFormatVersion, BlobClass.Metadata, new KeyGeneration(0), blobId, salt, 1, writer, SealedShare()));
        Assert.ThrowsExactly<ArgumentException>(() => new BlobEnvelope(
            FormatLimits.SealedFormatVersion, BlobClass.Data, new KeyGeneration(0), blobId, salt, 1, writer,
            SealedShare().AsSpan(0, 79)));
    }

    [TestMethod]
    public void BlobEnvelope_WriteToTheWrongDestinationLength_IsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => SealedSample().WriteTo(new byte[BlobEnvelope.Length]));
        Assert.ThrowsExactly<ArgumentException>(() => Sample().WriteTo(new byte[BlobEnvelope.MaxLength]));
    }
}
