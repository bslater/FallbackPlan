using System.Formats.Cbor;
using System.Text;
using FallbackPlan.Repository.Format.Resources;

namespace FallbackPlan.Repository.Format.Cbor;

/// <summary>
/// Reads deterministic CBOR, refusing every violation of the format's profile
/// (specification 00 §4.1–§4.2) rather than accepting it leniently: indefinite
/// lengths, non-shortest integers or length prefixes, unsorted or duplicate
/// map keys, non-integer map keys, floating-point values, and tags all raise
/// <see cref="CborFormatException"/> (NFR-PORT-003, NFR-COMP-004).
/// </summary>
/// <remarks>
/// <para>
/// The underlying decoder's CTAP2 canonical mode enforces definite lengths,
/// shortest-form encodings, and sorted unique keys. Two profile rules it does
/// not enforce are policed here: floating-point values are forbidden entirely
/// (rule 5 — CTAP2 permits them), and map keys must be unsigned integers
/// (00 §4.2 — CTAP2 permits sorted text keys). Restricting keys to unsigned
/// integers is also what makes CTAP2's length-first key order coincide with
/// the specification's encoded-byte order.
/// </para>
/// <para>
/// String and array reads take a caller-stated limit and refuse a longer
/// declared length from the prefix alone, before any allocation
/// (specification 00 §8).
/// </para>
/// </remarks>
public sealed class CanonicalCborReader
{
    private readonly CborReader _reader;

    /// <summary>Creates a reader over an encoded document.</summary>
    public CanonicalCborReader(ReadOnlyMemory<byte> data)
    {
        _reader = new CborReader(data, CborConformanceMode.Ctap2Canonical);
    }

    /// <summary>Begins reading a map and returns its definite entry count.</summary>
    public int ReadStartMap() =>
        Guard(static reader =>
            reader.ReadStartMap() ?? throw new CborFormatException(Strings.CanonicalCborReader_IndefiniteLengthMap));

    /// <summary>Ends the current map.</summary>
    public void ReadEndMap() => Guard(static reader => reader.ReadEndMap());

    /// <summary>
    /// Begins reading an array, refusing a declared item count above
    /// <paramref name="maxCount"/> before reading any item.
    /// </summary>
    public int ReadStartArray(int maxCount)
    {
        var count = Guard(static reader =>
            reader.ReadStartArray() ?? throw new CborFormatException(Strings.CanonicalCborReader_IndefiniteLengthArray));

        if (count > maxCount)
        {
            throw new CborFormatException(Strings.FormatCanonicalCborReader_ArrayDeclaresItemsLimitHere(count, maxCount));
        }

        return count;
    }

    /// <summary>Ends the current array.</summary>
    public void ReadEndArray() => Guard(static reader => reader.ReadEndArray());

    /// <summary>
    /// Reads a map key, which the format requires to be an unsigned integer
    /// (specification 00 §4.2). Any other key type is refused.
    /// </summary>
    public uint ReadKey()
    {
        if (Guard(static reader => reader.PeekState()) != CborReaderState.UnsignedInteger)
        {
            throw new CborFormatException(Strings.CanonicalCborReader_MapKeysMustUnsignedIntegers);
        }

        return Guard(static reader => reader.ReadUInt32());
    }

    /// <summary>Reads an unsigned integer value.</summary>
    public ulong ReadUnsignedInteger() => Guard(static reader => reader.ReadUInt64());

    /// <summary>Reads an unsigned integer that must fit a <c>u32</c>.</summary>
    public uint ReadUInt32() => Guard(static reader => reader.ReadUInt32());

    /// <summary>Reads an unsigned integer that must fit a <c>u16</c>.</summary>
    public ushort ReadUInt16()
    {
        var value = ReadUInt32();
        if (value > ushort.MaxValue)
        {
            throw new CborFormatException(Strings.FormatCanonicalCborReader_ValueDoesNotFitU(value));
        }

        return (ushort)value;
    }

    /// <summary>Reads an unsigned integer that must fit a <c>u8</c>.</summary>
    public byte ReadByte()
    {
        var value = ReadUInt32();
        if (value > byte.MaxValue)
        {
            throw new CborFormatException(Strings.FormatCanonicalCborReader_ValueDoesNotFitU2(value));
        }

        return (byte)value;
    }

    /// <summary>
    /// Reads a signed integer, for the few fields the specification declares
    /// as <c>i64</c> (for example observed clock skew, 06 §6).
    /// </summary>
    public long ReadSignedInteger() => Guard(static reader => reader.ReadInt64());

    /// <summary>Reads a boolean.</summary>
    public bool ReadBoolean() => Guard(static reader => reader.ReadBoolean());

    /// <summary>
    /// Reads a byte string, refusing a declared length above
    /// <paramref name="maxLength"/> before allocating (specification 00 §8).
    /// </summary>
    public byte[] ReadByteString(int maxLength)
    {
        // The definite-length read returns a slice of the input buffer, so the
        // length check happens before any copy is allocated.
        var slice = Guard(static reader => reader.ReadDefiniteLengthByteString());

        if (slice.Length > maxLength)
        {
            throw new CborFormatException(Strings.FormatCanonicalCborReader_ByteStringDeclaresBytesLimit(slice.Length, maxLength));
        }

        return slice.ToArray();
    }

    /// <summary>
    /// Reads a byte string that must be exactly
    /// <paramref name="expectedLength"/> bytes — the shape of every fixed-size
    /// identifier and key field.
    /// </summary>
    public byte[] ReadFixedByteString(int expectedLength)
    {
        var slice = Guard(static reader => reader.ReadDefiniteLengthByteString());

        if (slice.Length != expectedLength)
        {
            throw new CborFormatException(Strings.FormatCanonicalCborReader_ByteStringBytesExactlyRequired(slice.Length, expectedLength));
        }

        return slice.ToArray();
    }

    /// <summary>
    /// Reads a UTF-8 text string, refusing a declared length above
    /// <paramref name="maxUtf8Length"/> bytes before allocating.
    /// </summary>
    public string ReadTextString(int maxUtf8Length)
    {
        var slice = Guard(static reader => reader.ReadDefiniteLengthTextStringBytes());

        if (slice.Length > maxUtf8Length)
        {
            throw new CborFormatException(Strings.FormatCanonicalCborReader_TextStringDeclaresUTFBytes(slice.Length, maxUtf8Length));
        }

        return Encoding.UTF8.GetString(slice.Span);
    }

    /// <summary>
    /// Skips one value — the unknown-key path of specification 00 §4.3. The
    /// skipped value is still held to the full profile: a float, tag, null, or
    /// non-canonical encoding at any depth is refused.
    /// </summary>
    public void SkipValue()
    {
        switch (Guard(static reader => reader.PeekState()))
        {
            case CborReaderState.UnsignedInteger:
                Guard(static reader => reader.ReadUInt64());
                break;

            case CborReaderState.NegativeInteger:
                Guard(static reader => reader.ReadCborNegativeIntegerRepresentation());
                break;

            case CborReaderState.ByteString:
                Guard(static reader => reader.ReadDefiniteLengthByteString());
                break;

            case CborReaderState.TextString:
                Guard(static reader => reader.ReadDefiniteLengthTextStringBytes());
                break;

            case CborReaderState.Boolean:
                Guard(static reader => reader.ReadBoolean());
                break;

            case CborReaderState.StartArray:
                var itemCount = Guard(static reader =>
                    reader.ReadStartArray() ?? throw new CborFormatException(Strings.CanonicalCborReader_IndefiniteLengthArray));
                for (var i = 0; i < itemCount; i++)
                {
                    SkipValue();
                }

                ReadEndArray();
                break;

            case CborReaderState.StartMap:
                var entryCount = ReadStartMap();
                for (var i = 0; i < entryCount; i++)
                {
                    ReadKey();
                    SkipValue();
                }

                ReadEndMap();
                break;

            case CborReaderState.HalfPrecisionFloat:
            case CborReaderState.SinglePrecisionFloat:
            case CborReaderState.DoublePrecisionFloat:
                throw new CborFormatException(Strings.CanonicalCborReader_FloatingPointValuesForbidden);

            case CborReaderState.Tag:
                throw new CborFormatException(Strings.CanonicalCborReader_CBORTagsForbiddenFormatV);

            case var state:
                throw new CborFormatException(Strings.FormatCanonicalCborReader_UnsupportedCBORItemFormatObject(state));
        }
    }

    /// <summary>
    /// Asserts the document has been fully consumed — trailing bytes after the
    /// root value are refused.
    /// </summary>
    public void AssertEndOfDocument()
    {
        // PeekState reports Finished once one root value has been read even
        // when bytes remain, so the byte count is the authoritative check.
        if (Guard(static reader => reader.PeekState()) != CborReaderState.Finished || _reader.BytesRemaining != 0)
        {
            throw new CborFormatException(Strings.CanonicalCborReader_TrailingBytesAfterRootCBOR);
        }
    }

    private T Guard<T>(Func<CborReader, T> read)
    {
        try
        {
            return read(_reader);
        }
        catch (CborContentException exception)
        {
            throw new CborFormatException(Strings.CanonicalCborReader_CBORInputViolatesDeterministicEncoding, exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new CborFormatException(Strings.CanonicalCborReader_CBORItemNotRequiredType, exception);
        }
        catch (OverflowException exception)
        {
            throw new CborFormatException(Strings.CanonicalCborReader_CBORIntegerOutRangeRequired, exception);
        }
    }

    private void Guard(Action<CborReader> read)
    {
        Guard(reader =>
        {
            read(reader);
            return true;
        });
    }
}
