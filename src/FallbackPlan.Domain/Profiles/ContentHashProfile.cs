namespace FallbackPlan.Domain.Profiles;

/// <summary>
/// A content-hash profile identifier (specification 02 §2). Instances exist
/// only for the profiles the specification assigns, so an invalid profile
/// cannot be constructed.
/// </summary>
public sealed class ContentHashProfile : IEquatable<ContentHashProfile>
{
    /// <summary>SHA-256 (<c>0x0001</c>): 32 bytes, used in full — never truncated.</summary>
    public static readonly ContentHashProfile Sha256V1 = new(0x0001, "sha-256-v1");

    private ContentHashProfile(ushort value, string name)
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
    public static bool TryFromValue(ushort value, out ContentHashProfile? profile)
    {
        profile = value switch
        {
            0x0001 => Sha256V1,
            _ => null,
        };

        return profile is not null;
    }

    /// <inheritdoc />
    public bool Equals(ContentHashProfile? other) => other is not null && Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ContentHashProfile other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value;

    /// <inheritdoc />
    public override string ToString() => Name;
}
