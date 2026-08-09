using Bodu;
using System.Formats.Cbor;

namespace FallbackPlan.Repository.Format.Cbor;

/// <summary>
/// Writes deterministic CBOR per the format's profile (specification 00
/// §4.1–§4.2). The surface exposes only what the specification's objects need
/// — unsigned and signed integers, byte strings, text, booleans, arrays, and
/// integer-keyed maps — so a document containing a float, a tag, or an
/// indefinite length cannot be produced: the method does not exist.
/// </summary>
/// <remarks>
/// Map entries may be written in any order; the underlying canonical encoder
/// sorts them by encoded key bytes when the map is closed, which for the
/// unsigned-integer keys the format permits is identical to numeric order.
/// The wrapped <c>System.Formats.Cbor</c> types never appear in this API, so
/// the recovery tool's closure carries no third-party surface (NFR-PORT-003).
/// </remarks>
public sealed class CanonicalCborWriter
{
    private readonly CborWriter _writer = new(CborConformanceMode.Ctap2Canonical, convertIndefiniteLengthEncodings: false);

    /// <summary>Begins a definite-length map of <paramref name="entryCount"/> key–value pairs.</summary>
    public void WriteStartMap(int entryCount)
    {
        ThrowHelper.ThrowIfNegative(entryCount);
        _writer.WriteStartMap(entryCount);
    }

    /// <summary>Ends the current map.</summary>
    public void WriteEndMap() => _writer.WriteEndMap();

    /// <summary>Begins a definite-length array of <paramref name="itemCount"/> items.</summary>
    public void WriteStartArray(int itemCount)
    {
        ThrowHelper.ThrowIfNegative(itemCount);
        _writer.WriteStartArray(itemCount);
    }

    /// <summary>Ends the current array.</summary>
    public void WriteEndArray() => _writer.WriteEndArray();

    /// <summary>Writes an unsigned-integer map key (specification 00 §4.2).</summary>
    public void WriteKey(uint key) => _writer.WriteUInt32(key);

    /// <summary>Writes an unsigned integer value.</summary>
    public void WriteUnsignedInteger(ulong value) => _writer.WriteUInt64(value);

    /// <summary>
    /// Writes a signed integer value, for the few fields the specification
    /// declares as <c>i64</c> (for example observed clock skew, 06 §6).
    /// </summary>
    public void WriteSignedInteger(long value) => _writer.WriteInt64(value);

    /// <summary>Writes a definite-length byte string.</summary>
    public void WriteByteString(ReadOnlySpan<byte> value) => _writer.WriteByteString(value);

    /// <summary>Writes a definite-length UTF-8 text string.</summary>
    public void WriteTextString(string value)
    {
        ThrowHelper.ThrowIfNull(value);
        _writer.WriteTextString(value);
    }

    /// <summary>Writes a boolean.</summary>
    public void WriteBoolean(bool value) => _writer.WriteBoolean(value);

    /// <summary>Returns the encoded document.</summary>
    public byte[] Encode() => _writer.Encode();
}
