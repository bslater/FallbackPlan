namespace FallbackPlan.Domain.Profiles;

/// <summary>
/// A segmentation profile identifier (specification 09 §1). Instances exist
/// only for the profiles the specification assigns, so an invalid profile
/// cannot be constructed; unknown values are refused at the boundary via
/// <see cref="TryFromValue"/>, never guessed (specification 00 §3).
/// </summary>
public sealed class SegmentationProfile : IEquatable<SegmentationProfile>
{
    /// <summary>Fixed-size segmentation, the format v1 default (<c>0x0001</c>).</summary>
    public static readonly SegmentationProfile FixedV1 = new(0x0001, "fixed-v1");

    /// <summary>
    /// Content-defined segmentation (<c>0x0002</c>), under the Rabin
    /// fingerprint parameters pinned by ADR-0023 (specification 09 §3.1).
    /// </summary>
    public static readonly SegmentationProfile CdcV1 = new(0x0002, "cdc-v1");

    private SegmentationProfile(ushort value, string name)
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
    public static bool TryFromValue(ushort value, out SegmentationProfile? profile)
    {
        profile = value switch
        {
            0x0001 => FixedV1,
            0x0002 => CdcV1,
            _ => null,
        };

        return profile is not null;
    }

    /// <inheritdoc />
    public bool Equals(SegmentationProfile? other) => other is not null && Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SegmentationProfile other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value;

    /// <inheritdoc />
    public override string ToString() => Name;
}
