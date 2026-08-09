using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Packing;

/// <summary>
/// The FBPKSREC standalone record framing (ADR-0022 §Decision 1;
/// specification 04 applied at ordinal 0; NFR-SEC-003): round-trips, binds
/// the repository and object identity into the AAD, and refuses truncation
/// and oversize before allocation.
/// </summary>
[TestClass]
public sealed class StandaloneRecordTests
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private static readonly byte[] MetadataKey = [.. Enumerable.Range(50, 32).Select(value => (byte)value)];

    private static (byte[] Sealed, ObjectId Id, byte[] Payload) SealSample(ulong counter = 42)
    {
        var payload = "standalone metadata record payload (canonical CBOR in real use)"u8.ToArray();
        using var deriver = new ObjectIdDeriver(
            HKDF.Expand(HashAlgorithmName.SHA256, MetadataKey, 32, info: "test"u8.ToArray()));
        var objectId = deriver.Derive(ObjectType.IndexDelta, ContentHasher.Hash(payload));

        var sealedBytes = StandaloneRecordCipher.Seal(
            Repo, MetadataKey, KeyGeneration.Zero, Writer, counter, ObjectType.IndexDelta, objectId, payload);

        return (sealedBytes, objectId, payload);
    }

    [TestMethod]
    public void StandaloneRecord_SealedThenOpened_RoundTrips()
    {
        var (sealedBytes, objectId, payload) = SealSample();

        var record = StandaloneRecordFraming.Parse(sealedBytes);

        Assert.AreEqual(0u, record.Header.Ordinal);
        Assert.AreEqual(objectId, record.Header.ObjectId);
        Assert.AreEqual(ObjectType.IndexDelta, record.Header.ObjectType);
        Assert.AreEqual(Writer, record.WriterId);
        Assert.AreEqual(42UL, record.Counter);
        Assert.AreEqual(KeyGeneration.Zero, record.KeyGeneration);

        Assert.IsTrue(StandaloneRecordCipher.TryOpen(record, Repo, MetadataKey, out var plaintext));
        SequenceAssert.AreEqual(payload, plaintext);
    }

    [TestMethod]
    public void StandaloneRecord_MovedBetweenRepositories_FailsAuthentication()
    {
        var (sealedBytes, _, _) = SealSample();
        var record = StandaloneRecordFraming.Parse(sealedBytes);

        var otherRepository = RepositoryId.FromBytes(Convert.FromHexString("ffffffffffffffffffffffffffffffff"));

        // The 55-byte AAD binds the repository identity (04 §4) — replaying
        // a delta into a sibling repository must fail, not decrypt.
        Assert.IsFalse(StandaloneRecordCipher.TryOpen(record, otherRepository, MetadataKey, out var plaintext));
        Assert.IsEmpty(plaintext);
    }

    [TestMethod]
    public void StandaloneRecord_ACiphertextByteIsTampered_FailsAuthenticationAndEmitsNothing()
    {
        var (sealedBytes, _, _) = SealSample();
        sealedBytes[StandaloneRecordFraming.PrefixLength + RecordHeader.Length + 5] ^= 0x01;

        var record = StandaloneRecordFraming.Parse(sealedBytes);

        Assert.IsFalse(StandaloneRecordCipher.TryOpen(record, Repo, MetadataKey, out var plaintext));
        Assert.IsEmpty(plaintext);
    }

    [TestMethod]
    public void StandaloneRecord_OpenedWithTheWrongGenerationKey_FailsAuthentication()
    {
        var (sealedBytes, _, _) = SealSample();
        var record = StandaloneRecordFraming.Parse(sealedBytes);

        var otherGenerationKey = Enumerable.Range(90, 32).Select(value => (byte)value).ToArray();

        Assert.IsFalse(StandaloneRecordCipher.TryOpen(record, Repo, otherGenerationKey, out _));
    }

    [TestMethod]
    public void StandaloneRecord_DistinctCountersUnderOneSalt_ProduceDistinctKeys()
    {
        // The shared sequence space is what makes (salt, writer, counter)
        // unique per object (ADR-0022 §Decision 1) — two counters must never
        // share bytes even with everything else equal.
        var salt = new byte[32];
        var payload = "identical payload"u8.ToArray();
        var objectId = ObjectId.FromBytes(SHA256.HashData(payload));

        var first = StandaloneRecordCipher.Seal(
            Repo, MetadataKey, KeyGeneration.Zero, Writer, 1, ObjectType.IndexDelta, objectId, payload, salt);
        var second = StandaloneRecordCipher.Seal(
            Repo, MetadataKey, KeyGeneration.Zero, Writer, 2, ObjectType.IndexDelta, objectId, payload, salt);

        Assert.AreNotEqual(
            first[StandaloneRecordFraming.PrefixLength..],
            second[StandaloneRecordFraming.PrefixLength..]);
    }

    [TestMethod]
    public void StandaloneRecord_MagicIsAbsent_SaysItIsNotAStandaloneRecord()
    {
        var (sealedBytes, _, _) = SealSample();
        sealedBytes[0] ^= 0x01;

        var exception = Assert.ThrowsExactly<RecordFormatException>(() => StandaloneRecordFraming.Parse(sealedBytes));
        Assert.Contains("FBPKSREC", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void StandaloneRecord_TruncatedOrCarryingTrailingBytes_IsRefused()
    {
        var (sealedBytes, _, _) = SealSample();

        Assert.ThrowsExactly<RecordFormatException>(() =>
            StandaloneRecordFraming.Parse(sealedBytes.AsMemory(0, sealedBytes.Length - 1)));
        Assert.ThrowsExactly<RecordFormatException>(() =>
            StandaloneRecordFraming.Parse((byte[])[.. sealedBytes, 0xDE]));
    }

    [TestMethod]
    public void StandaloneRecord_DeclaredLengthExceedsTheMetadataBound_IsRefusedBeforeAllocation()
    {
        var (sealedBytes, _, _) = SealSample();

        // Rewrite the header's stored_length to a hostile value; the parser
        // must refuse on the declared bound before allocating.
        var headerSpan = sealedBytes.AsSpan(StandaloneRecordFraming.PrefixLength, RecordHeader.Length);
        var header = RecordHeader.Parse(headerSpan);
        var hostile = new RecordHeader(
            header.ObjectType, header.CompressionProfile, header.EncryptionProfile,
            header.Ordinal, 20 * 1024 * 1024, 20 * 1024 * 1024, header.ObjectId);
        hostile.WriteTo(headerSpan);

        var exception = Assert.ThrowsExactly<RecordFormatException>(() => StandaloneRecordFraming.Parse(sealedBytes));
        Assert.Contains("16 MiB", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void StandaloneRecord_ObjectTypeIsTheReservedOne_CannotBeSealed()
    {
        var payload = "x"u8.ToArray();

        Assert.ThrowsExactly<ArgumentException>(() =>
            StandaloneRecordCipher.Seal(
                Repo, MetadataKey, KeyGeneration.Zero, Writer, 1, (ObjectType)0x07,
                ObjectId.FromBytes(SHA256.HashData(payload)), payload));
    }
}
