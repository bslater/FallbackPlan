namespace FallbackPlan.Domain;

/// <summary>
/// The object types a record may carry (specification 02 §3.1). The type byte
/// domain-separates object identifiers, so identical plaintext stored as a
/// segment and as a manifest yields distinct identifiers.
/// </summary>
/// <remarks>
/// The value <c>0x07</c> is deliberately absent: it is reserved as the domain
/// separator in the store-blob-key derivation (specification 02 §4.3) and must
/// never be assigned to a record's object type.
/// </remarks>
public enum ObjectType : byte
{
    /// <summary>A segment record — encrypted file content.</summary>
    SegmentRecord = 0x01,

    /// <summary>A file-version manifest (specification 06 §4).</summary>
    FileVersionManifest = 0x02,

    /// <summary>A tree manifest (specification 06 §5).</summary>
    TreeManifest = 0x03,

    /// <summary>A snapshot manifest (specification 06 §6).</summary>
    SnapshotManifest = 0x04,

    /// <summary>A policy manifest (specification 06 §7).</summary>
    PolicyManifest = 0x05,

    /// <summary>An error manifest (specification 06 §8).</summary>
    ErrorManifest = 0x06,
}

/// <summary>
/// Validation for <see cref="ObjectType"/> values read from untrusted bytes.
/// </summary>
public static class ObjectTypes
{
    /// <summary>
    /// Returns whether <paramref name="value"/> is an assigned object type.
    /// Rejects zero, the reserved store-key domain separator <c>0x07</c>, and
    /// everything unassigned above it — an unknown type in an object the
    /// reader must interpret is refused, never guessed (specification 00 §3).
    /// </summary>
    public static bool IsValid(byte value) =>
        value is >= 0x01 and <= 0x06;

    /// <summary>
    /// Converts a byte read from untrusted input into an
    /// <see cref="ObjectType"/>, refusing unassigned values.
    /// </summary>
    public static bool TryFromValue(byte value, out ObjectType objectType)
    {
        if (IsValid(value))
        {
            objectType = (ObjectType)value;
            return true;
        }

        objectType = default;
        return false;
    }
}
