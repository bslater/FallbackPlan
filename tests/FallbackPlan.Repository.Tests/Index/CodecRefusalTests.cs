using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Cbor;
using FallbackPlan.Repository.Index;

namespace FallbackPlan.Repository.Tests.Index;

/// <summary>
/// What the index codecs refuse. A decoder that accepts a malformed delta
/// is worse than one that crashes: the index is what says where every
/// object lives, so a silently mis-decoded record becomes a restore that
/// reads the wrong bytes or reports data as absent.
/// </summary>
/// <remarks>
/// Each case is built by hand rather than by mutating a valid encoding,
/// because a mutation that happens to stay well-formed proves nothing —
/// the point is to construct exactly one violation and show it is named.
/// </remarks>
[TestClass]
public sealed class CodecRefusalTests
{
    private static WriterId Writer => WriterId.FromBytes(Enumerable.Repeat((byte)0xA1, 16).ToArray());

    private static byte[] Bytes(byte value, int count = 16) => [.. Enumerable.Repeat(value, count)];

    /// <summary>
    /// Writes a delta body key by key, so a test can omit a mandatory key,
    /// add one the specification never assigned, or give one the wrong
    /// value — none of which a valid encoder can be asked to produce.
    /// </summary>
    private static byte[] Delta(
        bool includeWriter = true,
        bool includeSequence = true,
        bool includeGeneration = true,
        bool includeSignature = true,
        bool? isVoid = null,
        uint? unknownKey = null)
    {
        var keys = (includeWriter ? 1 : 0) + (includeSequence ? 1 : 0) + (includeGeneration ? 1 : 0)
            + (includeSignature ? 1 : 0) + (isVoid is null ? 0 : 1) + (unknownKey is null ? 0 : 1)
            + 2; // keys 6 and 7 are always written

        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(keys);

        if (includeWriter)
        {
            writer.WriteKey(1);
            writer.WriteByteString(Writer.ToArray());
        }

        if (includeSequence)
        {
            writer.WriteKey(2);
            writer.WriteUnsignedInteger(1);
        }

        if (includeGeneration)
        {
            writer.WriteKey(4);
            writer.WriteUnsignedInteger(0);
        }

        writer.WriteKey(6);
        writer.WriteStartArray(0);
        writer.WriteEndArray();

        writer.WriteKey(7);
        writer.WriteStartArray(0);
        writer.WriteEndArray();

        if (isVoid is { } voidValue)
        {
            writer.WriteKey(8);
            writer.WriteBoolean(voidValue);
        }

        if (includeSignature)
        {
            writer.WriteKey(9);
            writer.WriteByteString(Bytes(0x5A, 64));
        }

        if (unknownKey is { } key)
        {
            writer.WriteKey(key);
            writer.WriteUnsignedInteger(0);
        }

        writer.WriteEndMap();
        return writer.Encode();
    }

    [TestMethod]
    public void IndexDelta_CarriesAnUnassignedKey_IsRefused()
    {
        // Ignoring an unknown key would let a future — or forged — writer
        // smuggle meaning past a reader that cannot act on it (07 §2).
        var exception = Assert.ThrowsExactly<IndexFormatException>(() => IndexDeltaCodec.Decode(Delta(unknownKey: 42)));

        Assert.Contains("unknown key", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [DataRow(false, true, true, true)]   // no writer_id
    [DataRow(true, false, true, true)]   // no sequence
    [DataRow(true, true, false, true)]   // no generation
    [DataRow(true, true, true, false)]   // no signature
    public void IndexDelta_AMandatoryKeyIsMissing_IsRefused(
        bool writer, bool sequence, bool generation, bool signature)
    {
        var exception = Assert.ThrowsExactly<IndexFormatException>(() => IndexDeltaCodec.Decode(
            Delta(includeWriter: writer, includeSequence: sequence,
                  includeGeneration: generation, includeSignature: signature)));

        Assert.Contains("mandatory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void IndexDelta_IsVoidIsEncodedAsFalse_IsRefused()
    {
        // The key exists to mark a void delta, so present-and-false is a
        // third state the format does not have: writing it means the
        // producer misunderstood the flag (07 §2 key 8).
        var exception = Assert.ThrowsExactly<IndexFormatException>(() => IndexDeltaCodec.Decode(Delta(isVoid: false)));

        Assert.Contains("is_void", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void IndexDelta_DeclaredVoid_IsAccepted()
    {
        // The other side of the rule: is_void present and true decodes, so
        // the refusal above is about the value and not about the key.
        var decoded = IndexDeltaCodec.Decode(Delta(isVoid: true));

        Assert.IsTrue(decoded.Delta.IsVoid);
        Assert.IsEmpty(decoded.Delta.Entries);
    }

    [TestMethod]
    public void IndexDelta_TrailingBytesFollowTheDocument_AreRefused()
    {
        // A decoder that stops at the end of the map would accept a record
        // with content appended after it — the shape a smuggling attack
        // takes against a length-prefixed reader.
        var valid = Delta();
        var padded = new byte[valid.Length + 1];
        valid.CopyTo(padded, 0);

        Assert.Throws<Exception>(() => IndexDeltaCodec.Decode(padded));
    }

    [TestMethod]
    public void IndexDelta_InputIsEmpty_IsRefused()
    {
        Assert.Throws<Exception>(() => IndexDeltaCodec.Decode(ReadOnlyMemory<byte>.Empty));
    }
}
