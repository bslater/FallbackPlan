namespace FallbackPlan.Domain.Profiles;

/// <summary>
/// An encryption (AEAD) profile identifier (specification 03 §6), recorded per
/// record. Instances exist only for the approved suites, so an unapproved
/// suite cannot be constructed — a writer rejects it at configuration time,
/// not at write time, and no insecure selection exists as a compatibility
/// switch (specification 03 §6).
/// </summary>
public sealed class EncryptionProfile : IEquatable<EncryptionProfile>
{
    /// <summary>AES-256-GCM (<c>0x0001</c>): 32-byte key, 12-byte nonce, 16-byte tag. Platform-provided.</summary>
    public static readonly EncryptionProfile Aes256GcmV1 = new(0x0001, "aes-256-gcm-v1");

    /// <summary>
    /// XChaCha20-Poly1305 (<c>0x0002</c>): 32-byte key, 24-byte nonce, 16-byte
    /// tag. Third-party — the platform's <c>ChaCha20Poly1305</c> is the
    /// 12-byte-nonce RFC 8439 variant and is not a substitute. An implementer
    /// may omit this profile entirely (specification 03 §6.1).
    /// </summary>
    public static readonly EncryptionProfile XChaCha20Poly1305V1 = new(0x0002, "xchacha20-poly1305-v1");

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
    /// Resolves a wire value to a known profile. Fails for unassigned values
    /// and for the private-use range <c>0x8000</c> and above, which must not
    /// appear in a portable repository (specification 00 §3).
    /// </summary>
    public static bool TryFromValue(ushort value, out EncryptionProfile? profile)
    {
        profile = value switch
        {
            0x0001 => Aes256GcmV1,
            0x0002 => XChaCha20Poly1305V1,
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
