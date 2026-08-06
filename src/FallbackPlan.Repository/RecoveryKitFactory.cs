using FallbackPlan.Repository.Crypto;
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
    public static async ValueTask<RecoveryKit> BuildAsync(
        IObjectStore store,
        Passphrase passphrase,
        ReadOnlyMemory<byte> issuingDeviceId,
        ulong issuedAt,
        IReadOnlyList<KitDestination> destinations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destinations);

        var (descriptor, keyObject) = await RepositoryLifecycle
            .ExportVerifiedKeyObjectAsync(store, passphrase, cancellationToken).ConfigureAwait(false);

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
        };
    }
}
