using Bodu;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Repository.Format.RecoveryKit;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>
/// Builds recovery kits (specification recovery-kit/README; FR-KIT-001):
/// the verified descriptor supplies the public KDF parameters and identity,
/// the export path supplies the verbatim key object the passphrase
/// provably opens, and the caller supplies provenance. The kit is one
/// factor — it never carries the passphrase, credentials, or the device
/// private key.
/// </summary>
public static class RecoveryKitFactory
{
    /// <summary>The instructions an installation kit carries.</summary>
    /// <remarks>
    /// Written for the situation the kit exists for: no other documentation
    /// is reachable, and the person reading it has lost the machine.
    /// </remarks>
    public const string InstallationInstructions =
        "This is a FallbackPlan recovery kit. It is ONE of the two things you need. "
        + "The other is your passphrase, which is NOT in this file and is not recoverable from it. "
        + "1. Install the FallbackPlan recovery tool. "
        + "2. Point it at any archive folder this installation wrote: "
        + "fallbackplan-recover restore --repo <archive folder> --kit <this file> "
        + "--passphrase-env <VAR> --snapshot <id> --output <dir>. "
        + "3. This kit opens every archive this installation made, not just one.";

    /// <summary>The instructions rendered into every kit's text form.</summary>
    public const string DefaultInstructions =
        "1. Install the FallbackPlan recovery tool. "
        + "2. Run: fallbackplan-recover restore --repo <store> --kit <this file> "
        + "--passphrase-env <VAR> --snapshot <id> --output <dir>. "
        + "3. The kit is one factor; without the repository passphrase it opens nothing.";

    /// <summary>
    /// Builds the <b>installation</b> kit (specifications/recovery-kit §2.2;
    /// FR-KIT-004): everything a person needs to reconstruct this
    /// installation's keys on a clean machine, known the moment the
    /// passphrase is chosen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No <see cref="IObjectStore"/>, no descriptor read and no passphrase
    /// parameter, which is the whole difference from
    /// <see cref="BuildAsync"/>. The caller has already derived — setup
    /// derives once and uses the result for both the provisioning envelope
    /// and this — so asking for the passphrase again would mean a second
    /// Argon2id pass to learn nothing new.
    /// </para>
    /// <para>
    /// It carries no key material. The sealing <em>public</em> key is the
    /// verifier a recovery proves its derivation against, and the salt and
    /// parameters are public by construction: they live in every archive's
    /// descriptor already.
    /// </para>
    /// </remarks>
    /// <param name="credential">The installation's derived write credential; only its public key is read.</param>
    /// <param name="kdfSalt">The installation's 16-byte KDF salt.</param>
    /// <param name="kdfParameters">The Argon2id parameters the salt was used with.</param>
    /// <param name="issuingDeviceId">The issuing device's public identity, 16 bytes.</param>
    /// <param name="issuedAt">Issue time, Unix milliseconds.</param>
    /// <returns>The kit, ready to serialise.</returns>
    public static RecoveryKit BuildForInstallation(
        RepositoryWriteCredential credential,
        ReadOnlySpan<byte> kdfSalt,
        Domain.Configuration.Argon2Parameters kdfParameters,
        ReadOnlyMemory<byte> issuingDeviceId,
        ulong issuedAt)
    {
        ThrowHelper.ThrowIfNull(credential);
        ThrowHelper.ThrowIfNull(kdfParameters);

        return new RecoveryKit
        {
            KitFormatVersion = RecoveryKit.InstallationKitVersion,
            MinimumToolVersion = "0.1.0",
            RepositoryId = null,
            RepositoryFormatVersion = Domain.FormatLimits.SealedFormatVersion,
            KdfMemoryKiB = kdfParameters.MemoryKiB,
            KdfIterations = kdfParameters.Iterations,
            KdfParallelism = kdfParameters.Parallelism,
            KdfSalt = kdfSalt.ToArray(),
            IssuingDeviceId = issuingDeviceId,
            IssuedAt = issuedAt,
            Instructions = InstallationInstructions,
            SealingPublicKey = credential.SealingPublicKey.ToArray(),
        };
    }

    /// <summary>Builds a kit for the repository at <paramref name="store"/>.</summary>
    /// <remarks>
    /// A write-only (format v2) repository's kit carries no key material at
    /// all (ADR-0042 §8): the sealing public key rides as the verifier, the
    /// key-object field is empty, and the passphrase is still proven before
    /// export — by derive-and-compare rather than an unwrap — so a kit is
    /// never exported that the passphrase cannot use.
    /// </remarks>
    public static async ValueTask<RecoveryKit> BuildAsync(
        IObjectStore store,
        Passphrase passphrase,
        ReadOnlyMemory<byte> issuingDeviceId,
        ulong issuedAt,
        IReadOnlyList<KitDestination> destinations,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(destinations);

        RepositoryDescriptor descriptor;
        var keyObject = ReadOnlyMemory<byte>.Empty;
        var sealingPublicKey = ReadOnlyMemory<byte>.Empty;

        var probed = await RepositoryLifecycle.ReadDescriptorAsync(store, cancellationToken).ConfigureAwait(false);
        if (RepositoryLifecycle.IsWriteOnly(probed))
        {
            if (!RepositoryLifecycle.TryDeriveReadAuthority(probed, passphrase, out var authority))
            {
                throw new KeyUnwrapFailedException(
                    Resources.Strings.RepositoryLifecycle_PassphraseDoesNotReproduce);
            }

            authority!.Dispose();
            descriptor = probed;
            sealingPublicKey = probed.SealingPublicKey;
        }
        else
        {
            byte[] exported;
            (descriptor, exported) = await RepositoryLifecycle
                .ExportVerifiedKeyObjectAsync(store, passphrase, cancellationToken).ConfigureAwait(false);
            keyObject = exported;
        }

        return new RecoveryKit
        {
            KitFormatVersion = 1,
            MinimumToolVersion = "0.1.0",
            RepositoryId = descriptor.RepositoryId,
            RepositoryFormatVersion = descriptor.FormatVersion,
            KeyObject = keyObject,
            KdfMemoryKiB = descriptor.KdfParameters.MemoryKiB,
            KdfIterations = descriptor.KdfParameters.Iterations,
            KdfParallelism = descriptor.KdfParameters.Parallelism,
            KdfSalt = descriptor.KdfSalt,
            Destinations = destinations,
            IssuingDeviceId = issuingDeviceId,
            IssuedAt = issuedAt,
            Instructions = DefaultInstructions,
            SealingPublicKey = sealingPublicKey,
        };
    }
}
