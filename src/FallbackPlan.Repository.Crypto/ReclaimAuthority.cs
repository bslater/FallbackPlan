using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto.Resources;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// A collection run's authority to author deletions
/// ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §6): the reclaim
/// sub-root, held for the run and zeroed with it.
/// </summary>
/// <remarks>
/// <para>
/// It exists because a write-only service deliberately cannot derive this key
/// (§2), and a set that could not collect at all would be a regression rather
/// than a safeguard. Retention is format-agnostic, so a v2 set collects today;
/// the grant is what lets it keep doing so once the key it signs under is one
/// the service does not hold.
/// </para>
/// <para>
/// The lifetime is the decision as much as the key is. A service compromised
/// between runs holds nothing that can author a deletion, and one compromised
/// during a run holds an authority that expires with the job it was granted
/// for — which is what the review asked for when it said the collector
/// capability should prefer short-lived credentials.
/// </para>
/// <para>
/// This is the only road to the key. No hierarchy derives it: the write
/// credential a service holds carries the signing domain and deliberately
/// not this one (ADR-0055 §2), so a service compromised between runs holds
/// nothing that deletes.
/// </para>
/// </remarks>
public sealed class ReclaimAuthority : IDisposable
{
    private readonly byte[] _reclaimRoot;
    private bool _disposed;

    /// <summary>Takes a copy of the sub-root; the caller keeps its own.</summary>
    /// <param name="reclaimRoot">The 32-byte reclaim sub-root.</param>
    /// <exception cref="ArgumentException">The root is not exactly 32 bytes.</exception>
    public ReclaimAuthority(ReadOnlySpan<byte> reclaimRoot)
    {
        if (reclaimRoot.Length != WriteOnlyDerivation.ReclaimKeyLength)
        {
            throw new ArgumentException(
                Strings.FormatReclaimAuthority_RootExactlyBytes(
                    WriteOnlyDerivation.ReclaimKeyLength),
                nameof(reclaimRoot));
        }

        _reclaimRoot = reclaimRoot.ToArray();
    }

    /// <summary>The Ed25519 seed for one generation. The caller zeroes it.</summary>
    /// <param name="generation">The key generation in force.</param>
    /// <exception cref="ObjectDisposedException">The run that held this authority has ended.</exception>
    public byte[] SeedFor(KeyGeneration generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WriteOnlyDerivation.DeriveReclaimKeySeed(_reclaimRoot, generation);
    }

    /// <summary>
    /// Whether this authority is the repository's — proved by verifying a
    /// tombstone the repository already holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no stored public key to check a granted seed against, so the
    /// proof is by use: a tombstone already on disk was signed under the real
    /// reclaim key, and a grant that verifies it is the real one. A repository
    /// holding no tombstone yet has nothing to disagree with, and the first
    /// one written defines the key every later grant is measured against.
    /// </para>
    /// <para>
    /// Checking matters because the failure is otherwise silent and late: a
    /// wrong grant would write tombstones nothing can verify, and the next
    /// sweep would report them as forgeries — an alarm about an attack that
    /// never happened, raised at whoever reads the notices rather than at
    /// whoever sent the wrong envelope.
    /// </para>
    /// </remarks>
    /// <param name="signedBytes">The tombstone's signed prefix.</param>
    /// <param name="signature">Its signature.</param>
    /// <param name="generation">The generation it was sealed under.</param>
    public bool Verifies(
        ReadOnlySpan<byte> signedBytes, ReadOnlySpan<byte> signature, KeyGeneration generation)
    {
        var seed = SeedFor(generation);
        try
        {
            using var signer = RepositorySigner.FromSeed(seed, generation);
            return signer.Verify(signedBytes, signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>Deliberately redacted.</summary>
    public override string ToString() => "reclaim-authority(redacted)";

    /// <summary>Zeroes the sub-root. The run's authority ends here.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            CryptographicOperations.ZeroMemory(_reclaimRoot);
            _disposed = true;
        }
    }
}
