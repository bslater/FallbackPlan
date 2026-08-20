using System.Security.Cryptography;
using Bodu.Security.Cryptography;
using FallbackPlan.Domain.Configuration;
using KdfParameters = FallbackPlan.Domain.Configuration.Argon2Parameters;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Derives a write-only repository's entire key material from its one
/// passphrase (ADR-0042 §1). <c>root = Argon2id(passphrase, salt, params)</c>;
/// every key is an independent one-way HKDF-SHA256 domain of that root —
/// <c>"fbp/seal/v2"</c> for the X25519 sealing scalar, <c>"fbp/metadata/v2"</c>
/// and <c>"fbp/signing/v2"</c> for the sub-roots the write credential expands
/// per generation, <c>"fbp/content-id/v2"</c> and <c>"fbp/key-id/v2"</c> for
/// the repository-scoped keys. Nothing is stored: the same passphrase and
/// salt always reproduce the same bundle, which is the whole design — and
/// why a v2 passphrase can never change.
/// </summary>
public static class WriteOnlyDerivation
{
    /// <summary>The X25519 key length: 32 bytes.</summary>
    public const int SealingKeyLength = 32;

    /// <summary>
    /// Derives the full read authority — the write credential plus the
    /// sealing private key. Callers that only provision a service should
    /// take <see cref="RepositoryReadAuthority.Credential"/> and dispose the
    /// authority; restore holds the whole thing for the grant's life.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The salt is not exactly 16 bytes, or creation-mode parameters fall
    /// below the mandated minimums (via <see cref="KekDerivation"/>'s rules).
    /// </exception>
    public static RepositoryReadAuthority Derive(
        Passphrase passphrase,
        KdfParameters parameters,
        ReadOnlySpan<byte> salt,
        KdfValidationMode mode)
    {
        // The KDF, its validation modes and its minimums are exactly v1's
        // (specification 03 §2): only what the output feeds differs.
        using var derivation = KekDerivation.Derive(passphrase, parameters, salt, mode);
        return FromRoot(derivation.Kek.Bytes);
    }

    /// <summary>
    /// Expands an already-derived 32-byte root into the full authority —
    /// the second half of <see cref="Derive"/>, exposed so conformance
    /// vectors (which pin the root, Argon2id having no independent
    /// implementation in the vector generator) can exercise the expansion
    /// tree in isolation (specification 03 §9.1).
    /// </summary>
    /// <exception cref="ArgumentException">The root is not exactly 32 bytes.</exception>
    public static RepositoryReadAuthority FromRoot(ReadOnlySpan<byte> root)
    {
        if (root.Length != KekDerivation.KekLength)
        {
            throw new ArgumentException(
                Resources.Strings.FormatWriteOnlyDerivation_RootExactlyBytes(KekDerivation.KekLength), nameof(root));
        }

        var sealingScalar = Expand(root, "fbp/seal/v2"u8);
        byte[] sealingPublic;
        try
        {
            using var exchange = X25519.Create();
            exchange.ImportPrivateKey(sealingScalar);
            sealingPublic = exchange.ExportPublicKey();
        }
        catch
        {
            CryptographicOperations.ZeroMemory(sealingScalar);
            throw;
        }

        var credential = new RepositoryWriteCredential(
            sealingPublic,
            Expand(root, "fbp/content-id/v2"u8),
            Expand(root, "fbp/key-id/v2"u8),
            Expand(root, "fbp/metadata/v2"u8),
            Expand(root, "fbp/signing/v2"u8));

        return new RepositoryReadAuthority(credential, sealingScalar);
    }

    private static byte[] Expand(ReadOnlySpan<byte> root, ReadOnlySpan<byte> info)
    {
        var derived = new byte[KeyHierarchy.DerivedKeyLength];
        HKDF.Expand(HashAlgorithmName.SHA256, root, derived, info);
        return derived;
    }
}

/// <summary>
/// A write credential together with the sealing private key — the shape a
/// restore holds for exactly as long as its grant lives (ADR-0042 §5), and
/// the shape setup holds for exactly as long as provisioning takes. Owns and
/// disposes both halves.
/// </summary>
public sealed class RepositoryReadAuthority : IDisposable
{
    private readonly byte[] _sealingPrivateKey;

    internal RepositoryReadAuthority(RepositoryWriteCredential credential, byte[] sealingPrivateKey)
    {
        Credential = credential;
        _sealingPrivateKey = sealingPrivateKey;
    }

    /// <summary>The write bundle.</summary>
    public RepositoryWriteCredential Credential { get; }

    /// <summary>The X25519 scalar that opens sealed content keys.</summary>
    public ReadOnlySpan<byte> SealingPrivateKey => _sealingPrivateKey;

    /// <summary>Deliberately redacted.</summary>
    public override string ToString() => "read-authority(redacted)";

    /// <summary>Zeroes the private key and disposes the credential.</summary>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_sealingPrivateKey);
        Credential.Dispose();
    }
}
