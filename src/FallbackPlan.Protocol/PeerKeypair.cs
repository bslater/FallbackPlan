using System.Security.Cryptography;
using Bodu.Security.Cryptography;
using FallbackPlan.Protocol.Resources;

namespace FallbackPlan.Protocol;

/// <summary>
/// This device's long-lived peer keypair (specification peer-protocol 01 §1).
/// </summary>
/// <remarks>
/// <para>
/// Generated on first use and stored in durable local state, which
/// NFR-REL-007 already named as holding a
/// "device keypair": not rebuildable from the repository, and therefore kept
/// apart from the disposable catalogue. Losing it loses every pairing this
/// device held, which is why it lives beside the device identity rather than
/// beside the cache.
/// </para>
/// <para>
/// It MUST NOT be derived from the repository master key. That is not a
/// preference: a destination holds a peer keypair while holding no repository
/// key at all, so a derivation would make the two inseparable and force the
/// borrower of a disk to hold the keys to the data on it (ADR-0030 §1).
/// </para>
/// </remarks>
public sealed class PeerKeypair : IDisposable
{
    /// <summary>Ed25519 signatures are 64 bytes.</summary>
    public const int SignatureLength = 64;

    /// <summary>Ed25519 private keys are a 32-byte seed.</summary>
    public const int PrivateKeyLength = 32;

    private readonly Ed25519 _key;

    private PeerKeypair(Ed25519 key)
    {
        _key = key;
        Identity = PeerIdentity.FromPublicKey(key.ExportPublicKey());
    }

    /// <summary>This device's identity — the public half.</summary>
    public PeerIdentity Identity { get; }

    /// <summary>Generates a fresh keypair from the system CSPRNG.</summary>
    /// <returns>The keypair; dispose to clear it.</returns>
    public static PeerKeypair Generate()
    {
        var key = Ed25519.Create();
        try
        {
            key.GenerateKey();
            return new PeerKeypair(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Loads a keypair from its stored private seed.</summary>
    /// <param name="privateKey">The 32-byte seed.</param>
    /// <returns>The keypair; dispose to clear it.</returns>
    /// <exception cref="ArgumentException">The seed is not 32 bytes.</exception>
    public static PeerKeypair FromPrivateKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != PrivateKeyLength)
        {
            throw new ArgumentException(Strings.FormatPeerKeypair_PeerPrivateKeyBytesGot(PrivateKeyLength, privateKey.Length), nameof(privateKey));
        }

        var key = Ed25519.Create();
        try
        {
            key.ImportPrivateKey(privateKey);
            return new PeerKeypair(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Exports the private seed, for the caller to store and then clear.</summary>
    /// <returns>A fresh 32-byte array the caller owns.</returns>
    public byte[] ExportPrivateKey() => _key.ExportPrivateKey();

    /// <summary>Signs with this device's peer key.</summary>
    /// <param name="data">What to sign.</param>
    /// <returns>The 64-byte signature.</returns>
    public byte[] Sign(ReadOnlySpan<byte> data) => _key.SignData(data);

    /// <inheritdoc />
    public void Dispose() => _key.Dispose();
}
