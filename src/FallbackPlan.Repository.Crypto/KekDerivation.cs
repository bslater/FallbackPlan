using Bodu;
using Bodu.Security.Cryptography;
using FallbackPlan.Domain.Configuration;
using KdfParameters = FallbackPlan.Domain.Configuration.Argon2Parameters;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Derives the key-encryption key from a passphrase via Argon2id
/// (specification 03 §2; FR-ARCH-008). This is the only production call site
/// of third-party cryptography outside the platform: Argon2id has no platform
/// implementation (ADR-0019, ADR-0021), and it is cross-verified against an
/// independent implementation on every CI run.
/// </summary>
public static class KekDerivation
{
    /// <summary>The KEK length: 32 bytes.</summary>
    public const int KekLength = 32;

    /// <summary>The descriptor salt length: 16 bytes (specification 01 §3.3).</summary>
    public const int SaltLength = 16;

    /// <summary>
    /// Derives the KEK. In <see cref="KdfValidationMode.CreateRepository"/>
    /// mode, parameters below the mandated minimums are refused with their
    /// named defects; in <see cref="KdfValidationMode.OpenRepository"/> mode
    /// they are accepted — stored parameters are facts — and reported through
    /// <see cref="KekDerivationResult.BelowCreationMinimums"/> so the caller
    /// can warn (specification 03 §2).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The salt is not exactly 16 bytes, or creation-mode parameters fall
    /// below the mandated minimums — the message names each defect.
    /// </exception>
    public static KekDerivationResult Derive(
        Passphrase passphrase,
        KdfParameters parameters,
        ReadOnlySpan<byte> salt,
        KdfValidationMode mode)
    {
        ThrowHelper.ThrowIfNull(passphrase);
        ThrowHelper.ThrowIfNull(parameters);

        if (salt.Length != SaltLength)
        {
            throw new ArgumentException($"The KDF salt is exactly {SaltLength} bytes; got {salt.Length}.", nameof(salt));
        }

        var findings = parameters.ValidateCreationMinimums();

        if (mode == KdfValidationMode.CreateRepository && !findings.IsValid)
        {
            throw new ArgumentException(
                "KDF parameters below the creation minimums: " +
                string.Join(", ", findings.Defects.Select(defect => defect.Name)) +
                " (specification 03 §2).",
                nameof(parameters));
        }

        var kekBytes = Argon2id.DeriveKey(
            passphrase.Utf8.ToArray(),
            salt.ToArray(),
            new Bodu.Security.Cryptography.Argon2Parameters
            {
                MemoryKiB = checked((int)parameters.MemoryKiB),
                Iterations = checked((int)parameters.Iterations),
                Parallelism = parameters.Parallelism,
                TagLength = KekLength,
            });

        return new KekDerivationResult(new Kek(kekBytes), belowCreationMinimums: !findings.IsValid);
    }
}

/// <summary>
/// The outcome of KEK derivation: the key, and whether the stored parameters
/// fall below today's creation minimums — accepted, but worth a warning
/// (specification 03 §2).
/// </summary>
public sealed class KekDerivationResult : IDisposable
{
    internal KekDerivationResult(Kek kek, bool belowCreationMinimums)
    {
        Kek = kek;
        BelowCreationMinimums = belowCreationMinimums;
    }

    /// <summary>The derived key-encryption key.</summary>
    public Kek Kek { get; }

    /// <summary>
    /// Whether the parameters fall below the current creation minimums —
    /// possible only when opening an existing repository.
    /// </summary>
    public bool BelowCreationMinimums { get; }

    /// <inheritdoc />
    public void Dispose() => Kek.Dispose();
}
