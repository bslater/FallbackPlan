namespace FallbackPlan.Domain;

/// <summary>
/// A key generation (specification 03 §4.1): the <c>u32</c> that selects which
/// data, metadata, or signing key derivation a blob or signature used. A blob
/// records the generation in its cleartext envelope so a reader can re-derive
/// the right key without trial decryption.
/// </summary>
public readonly struct KeyGeneration : IEquatable<KeyGeneration>, IComparable<KeyGeneration>
{
    /// <summary>Generation zero — the state of a freshly created repository.</summary>
    public static readonly KeyGeneration Zero = new(0);

    /// <summary>Creates a generation from its <c>u32</c> wire value.</summary>
    public KeyGeneration(uint value) => Value = value;

    /// <summary>The generation's <c>u32</c> wire value.</summary>
    public uint Value { get; }

    /// <inheritdoc />
    public bool Equals(KeyGeneration other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is KeyGeneration other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (int)Value;

    /// <inheritdoc />
    public int CompareTo(KeyGeneration other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool operator ==(KeyGeneration left, KeyGeneration right) => left.Equals(right);

    public static bool operator !=(KeyGeneration left, KeyGeneration right) => !left.Equals(right);

    public static bool operator <(KeyGeneration left, KeyGeneration right) => left.CompareTo(right) < 0;

    public static bool operator <=(KeyGeneration left, KeyGeneration right) => left.CompareTo(right) <= 0;

    public static bool operator >(KeyGeneration left, KeyGeneration right) => left.CompareTo(right) > 0;

    public static bool operator >=(KeyGeneration left, KeyGeneration right) => left.CompareTo(right) >= 0;
}
