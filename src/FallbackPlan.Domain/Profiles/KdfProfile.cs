namespace FallbackPlan.Domain.Profiles;

/// <summary>
/// A key-derivation-function profile identifier for the repository descriptor's
/// KDF parameters (specification 01 §3.3). Instances exist only for the
/// profiles the specification assigns, so an invalid profile cannot be
/// constructed.
/// </summary>
public sealed class KdfProfile : IEquatable<KdfProfile>
{
    /// <summary>Argon2id (<c>0x0001</c>), the only profile in format v1 (specification 03 §2).</summary>
    public static readonly KdfProfile Argon2id = new(0x0001, "argon2id");

    private KdfProfile(ushort value, string name)
    {
        Value = value;
        Name = name;
    }

    /// <summary>The profile's <c>u16</c> wire value.</summary>
    public ushort Value { get; }

    /// <summary>The profile's specification name.</summary>
    public string Name { get; }

    /// <summary>
    /// Resolves a wire value to a known profile. Fails for unassigned values
    /// and for the private-use range <c>0x8000</c> and above, which must not
    /// appear in a portable repository (specification 00 §3).
    /// </summary>
    public static bool TryFromValue(ushort value, out KdfProfile? profile)
    {
        profile = value switch
        {
            0x0001 => Argon2id,
            _ => null,
        };

        return profile is not null;
    }

    /// <inheritdoc />
    public bool Equals(KdfProfile? other) => other is not null && Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is KdfProfile other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value;

    /// <inheritdoc />
    public override string ToString() => Name;
}
