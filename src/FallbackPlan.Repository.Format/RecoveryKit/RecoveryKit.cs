using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Format.RecoveryKit;

/// <summary>One destination descriptor in a kit — where, never how to authenticate.</summary>
public sealed record KitDestination(string Kind, string Endpoint, string Container, string Prefix);

/// <summary>
/// The recovery kit's logical content (specifications/recovery-kit §2;
/// ADR-0013): everything a user needs, with the passphrase, to open the
/// repository on a clean machine — and deliberately nothing more. Key 5 is
/// the repository's key object <em>verbatim</em>, so the kit reuses the
/// repository-format unwrap path instead of introducing a second wrapping
/// construction.
/// </summary>
public sealed record RecoveryKit
{
    /// <summary>Kit format version (key 1) — this implementation writes 1.</summary>
    public required ushort KitFormatVersion { get; init; }

    /// <summary>Lowest recovery-tool version able to process the kit (key 2).</summary>
    public required string MinimumToolVersion { get; init; }

    /// <summary>The repository identity (key 3).</summary>
    public required RepositoryId RepositoryId { get; init; }

    /// <summary>The repository format version (key 4).</summary>
    public required ushort RepositoryFormatVersion { get; init; }

    /// <summary>The verbatim FBPKKEYS key object (key 5).</summary>
    public required ReadOnlyMemory<byte> KeyObject { get; init; }

    /// <summary>Argon2id memory (key 6.1).</summary>
    public required uint KdfMemoryKiB { get; init; }

    /// <summary>Argon2id iterations (key 6.2).</summary>
    public required uint KdfIterations { get; init; }

    /// <summary>Argon2id parallelism (key 6.3).</summary>
    public required byte KdfParallelism { get; init; }

    /// <summary>The public KDF salt (key 6.4), 16 bytes.</summary>
    public required ReadOnlyMemory<byte> KdfSalt { get; init; }

    /// <summary>Destination descriptors (key 7) — informational.</summary>
    public IReadOnlyList<KitDestination> Destinations { get; init; } = [];

    /// <summary>The issuing device's public identity (key 8).</summary>
    public required ReadOnlyMemory<byte> IssuingDeviceId { get; init; }

    /// <summary>Issue timestamp, Unix milliseconds (key 9).</summary>
    public required ulong IssuedAt { get; init; }

    /// <summary>Embedded recovery instructions (key 10).</summary>
    public required string Instructions { get; init; }

    /// <summary>
    /// A write-only (format v2) repository's sealing public key (key 11;
    /// ADR-0042 §8) — the derive-and-compare verifier, and the only
    /// key-shaped thing a v2 kit carries: every actual key re-derives from
    /// the passphrase. Present exactly when
    /// <see cref="RepositoryFormatVersion"/> is 2 or later, in which case
    /// <see cref="KeyObject"/> is empty — a v2 repository has no key object.
    /// </summary>
    public ReadOnlyMemory<byte> SealingPublicKey { get; init; }
}
