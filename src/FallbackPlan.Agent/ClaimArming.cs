using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using System.Security.Cryptography;

namespace FallbackPlan.Agent;

/// <summary>
/// The source side of arming a replica claim (specification peer-protocol 03
/// §3.2.1; ADR-0046): given the token a destination minted for one replica,
/// produce the public half of the credential that repository's
/// <em>passphrase</em> reproduces — or decline.
/// </summary>
/// <remarks>
/// <para>
/// This is what has to happen <b>before</b> the disaster. A destination will
/// only serve a replica back to the identity its attribution ledger names, and
/// that identity dies with the source's durable local state; the credential
/// registered here is the only thing a rebuilt machine can reproduce from
/// something a human still has.
/// </para>
/// <para>
/// Declining is a first-class answer, not a failure. A source that cannot
/// reach the Argon2id root must send nothing at all rather than register
/// something derived from other material: a credential the passphrase does not
/// reproduce would leave the replica looking armed and be unclaimable in the
/// one moment it is needed.
/// </para>
/// </remarks>
internal static class ClaimArming
{
    /// <summary>
    /// The claim public key for one replica at one destination, or null when
    /// this source cannot derive it.
    /// </summary>
    /// <param name="archive">The set's staging archive.</param>
    /// <param name="passphrase">The installation passphrase, when the service holds one.</param>
    /// <param name="claimToken">The destination's token for this replica, 16 bytes.</param>
    /// <returns>The 32-byte public key to register, or null to decline.</returns>
    public static byte[]? PublicKeyFor(
        ArchiveHandle archive, Passphrase? passphrase, ReadOnlyMemory<byte> claimToken)
    {
        // Both conditions are the same condition said twice: the root behind
        // the credential is the Argon2id output of THIS archive's passphrase,
        // and a service that opened the archive from a stored write credential
        // holds the bundle that root produced rather than the root itself
        // (ADR-0042 §1). Deriving from an unrelated passphrase the runtime
        // happens to hold would arm a key the archive's own passphrase can
        // never reproduce. That is Q23, and until it is settled a provisioned
        // write-only set simply stays unarmed.
        if (passphrase is null
            || archive is not { OpenedWithPassphrase: true }
            || claimToken.Length != ClaimKeyDeriver.TokenLength)
        {
            return null;
        }

        var descriptor = archive.Repository.Descriptor;

        // Open-mode validation: stored parameters are facts, and a repository
        // created under yesterday's minimums must still be able to arm its own
        // recovery.
        using var derivation = KekDerivation.Derive(
            passphrase, descriptor.KdfParameters, descriptor.KdfSalt.Span, KdfValidationMode.OpenRepository);

        var seed = ClaimKeyDeriver.DeriveSeed(derivation.Kek.Bytes, claimToken.Span);
        try
        {
            return ClaimKeyDeriver.PublicKeyOf(seed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }
}
