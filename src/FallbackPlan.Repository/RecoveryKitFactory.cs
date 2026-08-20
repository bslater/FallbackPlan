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
    /// <summary>The instructions rendered into every kit's text form.</summary>
    public const string DefaultInstructions =
        "1. Install the FallbackPlan recovery tool. "
        + "2. Run: fallbackplan-recover restore --repo <store> --kit <this file> "
        + "--passphrase-env <VAR> --snapshot <id> --output <dir>. "
        + "3. The kit is one factor; without the repository passphrase it opens nothing.";

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
