namespace FallbackPlan.Repository.Format.Cbor;

/// <summary>
/// Schema-free validation of a complete CBOR document against the
/// deterministic profile (specification 00 §4.1–§4.2): one root value, fully
/// canonical at every depth, nothing after it.
/// </summary>
public static class CanonicalCbor
{
    /// <summary>
    /// Validates <paramref name="data"/>, throwing
    /// <see cref="CborFormatException"/> on any profile violation.
    /// </summary>
    public static void Validate(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);

        reader.SkipValue();
        reader.AssertEndOfDocument();
    }
}
