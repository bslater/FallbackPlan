using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.RecoveryKit;

namespace FallbackPlan.Repository;

/// <summary>
/// The claim key, from a recovery kit and its passphrase
/// ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md) §1).
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of what a claimant may assume it has. A machine asking a
/// peer for its replica back has lost the archive, the state directory, the
/// configuration and its device identity; what its owner still holds is a
/// printed kit and a passphrase. No <c>IObjectStore</c> appears here for that
/// reason — there is no archive to open, which is precisely what is being
/// recovered.
/// </para>
/// <para>
/// Both kit shapes answer through one entry point. The person holding the kit
/// should not have to know which kind they were given, and the destination
/// cannot tell either: it recorded a public key and nothing about where it
/// came from.
/// </para>
/// </remarks>
public static class RecoveryKitClaim
{
    /// <summary>
    /// The Ed25519 claim seed this kit's passphrase reaches. The caller owns
    /// the bytes and should zero them.
    /// </summary>
    /// <param name="kit">The recovery kit, of either shape.</param>
    /// <param name="passphrase">The passphrase that made it.</param>
    /// <exception cref="KeyUnwrapFailedException">
    /// The passphrase does not open this kit. Refused rather than allowed to
    /// yield a well-formed seed no destination has ever heard of, which would
    /// be answered "no replica here is claimable under that key" — true, and
    /// the wrong diagnosis entirely.
    /// </exception>
    /// <exception cref="RecoveryKitFormatException">The kit names a format-1 repository, which is withdrawn.</exception>
    public static byte[] SeedFrom(RecoveryKit kit, Passphrase passphrase)
    {
        ThrowHelper.ThrowIfNull(kit);
        ThrowHelper.ThrowIfNull(passphrase);

        var parameters = new Argon2Parameters
        {
            MemoryKiB = kit.KdfMemoryKiB,
            Iterations = kit.KdfIterations,
            Parallelism = kit.KdfParallelism,
        };

        // An installation kit and a per-repository kit both re-derive
        // everything from the passphrase and the public salt; the sealing
        // public key the kit carries is the wrong-passphrase verifier
        // (ADR-0042 §8).
        if (kit.IsInstallationKit || kit.RepositoryFormatVersion >= FormatLimits.FormatVersion)
        {
            using var authority = WriteOnlyDerivation.Derive(
                passphrase, parameters, kit.KdfSalt.Span, KdfValidationMode.OpenRepository);

            if (!authority.Credential.SealingPublicKey.SequenceEqual(kit.SealingPublicKey.Span))
            {
                throw new KeyUnwrapFailedException(
                    Resources.Strings.RecoveryKitClaim_PassphraseDoesNotReproduce);
            }

            return authority.ClaimKeySeed.ToArray();
        }

        // A format-1 kit carried the master key inside a wrapped key object.
        // Format 1 is withdrawn: nothing here can unwrap it, and a claim key
        // derived from such a kit would name a repository no peer holds.
        throw new RecoveryKitFormatException(Resources.Strings.RecoveryKitClaim_FormatOneWithdrawn);
    }
}
