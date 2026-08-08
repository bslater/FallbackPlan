namespace FallbackPlan.Domain.Profiles;

/// <summary>
/// An encryption (AEAD) profile identifier (specification 03 §6), recorded per
/// record. Format version 1 admits exactly one; an instance exists only for
/// it, so an unapproved suite cannot be constructed — a writer rejects it at
/// configuration time, not at write time, and no insecure selection exists as
/// a compatibility switch.
/// </summary>
public sealed class EncryptionProfile : IEquatable<EncryptionProfile>
{
    /// <summary>AES-256-GCM (<c>0x0001</c>): 32-byte key, 12-byte nonce, 16-byte tag. Platform-provided.</summary>
    public static readonly EncryptionProfile Aes256GcmV1 = new(0x0001, "aes-256-gcm-v1");

    /// <summary>
    /// The one value this format reserves and never assigns (<c>0x0002</c>).
    /// </summary>
    /// <remarks>
    /// A draft admitted <c>xchacha20-poly1305-v1</c> here. It was withdrawn
    /// before the freeze because no second implementation existed to
    /// cross-verify against, and an unverified AEAD is discovered inside bytes
    /// the user already stored (specification 03 §6.1). The value stays
    /// reserved rather than freed: draft repositories and draft readers
    /// understood it, and a value meaning one thing in a draft and another in
    /// the frozen format is what a version number cannot repair.
    /// </remarks>
    public const ushort ReservedWithdrawnValue = 0x0002;

    private EncryptionProfile(ushort value, string name)
    {
        Value = value;
        Name = name;
    }

    /// <summary>The profile's <c>u16</c> wire value.</summary>
    public ushort Value { get; }

    /// <summary>The profile's specification name.</summary>
    public string Name { get; }

    /// <summary>
    /// Resolves a wire value to a known profile. Fails for unassigned values —
    /// including the reserved <see cref="ReservedWithdrawnValue"/> — and for
    /// the private-use range <c>0x8000</c> and above, which must not appear in
    /// a portable repository (specification 00 §3).
    /// </summary>
    public static bool TryFromValue(ushort value, out EncryptionProfile? profile)
    {
        profile = value switch
        {
            0x0001 => Aes256GcmV1,
            _ => null,
        };

        return profile is not null;
    }

    /// <inheritdoc />
    public bool Equals(EncryptionProfile? other) => other is not null && Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EncryptionProfile other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value;

    /// <inheritdoc />
    public override string ToString() => Name;
}
