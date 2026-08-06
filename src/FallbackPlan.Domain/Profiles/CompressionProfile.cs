namespace FallbackPlan.Domain.Profiles;

/// <summary>
/// A compression profile identifier (specification 10 §1), recorded per record
/// rather than per repository. Instances exist only for the profiles the
/// specification assigns, so an invalid profile cannot be constructed.
/// </summary>
public sealed class CompressionProfile : IEquatable<CompressionProfile>
{
    /// <summary>No compression (<c>0x0000</c>); stored length equals logical length.</summary>
    public static readonly CompressionProfile None = new(0x0000, "none");

    /// <summary>Zstandard, single standard frame per record (<c>0x0001</c>, specification 10 §4.1).</summary>
    public static readonly CompressionProfile ZstdV1 = new(0x0001, "zstd-v1");

    private CompressionProfile(ushort value, string name)
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
    public static bool TryFromValue(ushort value, out CompressionProfile? profile)
    {
        profile = value switch
        {
            0x0000 => None,
            0x0001 => ZstdV1,
            _ => null,
        };

        return profile is not null;
    }

    /// <inheritdoc />
    public bool Equals(CompressionProfile? other) => other is not null && Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CompressionProfile other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value;

    /// <inheritdoc />
    public override string ToString() => Name;
}
