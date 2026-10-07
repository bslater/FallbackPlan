using FallbackPlan.Application;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.Storage.S3;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Agent;

/// <summary>
/// The host's one decision about which provider serves a local archive path
/// (ADR-0012: the store contract is the provider seam, and Amendment 2 makes
/// it the fan-out seam too).
/// </summary>
/// <remarks>
/// Before this existed, nine call sites across the agent each constructed
/// <see cref="LocalFileSystemObjectStore"/> for themselves — which meant a
/// second provider (peer-served, S3, Azure) could not become a configuration
/// choice without editing nine places that had each made the choice
/// implicitly. Composition is a host's job, and a host does it once.
/// Everything downstream holds <see cref="IObjectStore"/> and cannot tell.
/// </remarks>
internal static class StoreComposition
{
    /// <summary>Opens the store rooted at a local path.</summary>
    internal static LocalFileSystemObjectStore OpenLocal(string rootPath, ILogger? logger = null) =>
        new LocalFileSystemObjectStore(rootPath, logger);

    /// <summary>
    /// Where an S3-compatible destination's objects live (ADR-0091): its
    /// endpoint, bucket, region and prefix, and — given a repository id — the
    /// folder that repository's replica is under, as a local path's is.
    /// </summary>
    /// <param name="destination">The destination's declaration, whose address the caller has found sound.</param>
    /// <param name="repositoryIdHex">The repository whose replica to address, or null for the destination's own prefix.</param>
    internal static S3Location S3LocationOf(DestinationConfiguration destination, string? repositoryIdHex = null)
    {
        var location = new S3Location(
            new Uri(destination.Endpoint!, UriKind.Absolute),
            destination.Bucket!,
            destination.EffectiveRegion,
            destination.Prefix,
            destination.Addressing == BucketAddressing.VirtualHost ? S3Addressing.VirtualHost : S3Addressing.Path);
        return repositoryIdHex is null ? location : location.Under(repositoryIdHex);
    }

    /// <summary>
    /// Opens an S3-compatible destination's store, or says why it cannot be:
    /// a declaration whose address is defective, or no access key stored.
    /// </summary>
    /// <param name="runtime">The service, for the key and the logger.</param>
    /// <param name="destination">The destination.</param>
    /// <param name="repositoryIdHex">The repository whose replica to open, or null for the destination's own prefix.</param>
    /// <param name="refusal">Why it could not be opened, when it could not.</param>
    /// <exception cref="FallbackPlan.Domain.ClientStateException">The stored key is damaged.</exception>
    internal static S3ObjectStore? OpenS3(
        ServiceRuntime runtime, DestinationConfiguration destination, string? repositoryIdHex, out string? refusal)
    {
        if (destination.AddressDefect is { } defect)
        {
            refusal = defect;
            return null;
        }

        if (runtime.DestinationCredentials.TryLoad(destination.Id) is not { } credentials)
        {
            refusal = NoAccessKey(destination.Name);
            return null;
        }

        refusal = null;
        return new S3ObjectStore(
            S3LocationOf(destination, repositoryIdHex), credentials, runtime.LoggerFor<S3ObjectStore>());
    }

    /// <summary>What a destination with no access key stored is told, with the two ways to store one.</summary>
    /// <param name="destinationName">The destination.</param>
    internal static string NoAccessKey(string destinationName) =>
        $"destination '{destinationName}' has no access key stored — store one in the console's destination "
        + $"editor, or with `fallbackplan destination-credentials {destinationName}`";
}
