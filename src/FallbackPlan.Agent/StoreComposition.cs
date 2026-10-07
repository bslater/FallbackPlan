using System.Globalization;
using FallbackPlan.Application;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.AzureBlob;
using FallbackPlan.Storage.Local;
using FallbackPlan.Storage.S3;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Agent;

/// <summary>
/// The host's one decision about which provider serves a destination
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
    /// Where an Azure Blob destination's blobs live (ADR-0093): its account,
    /// container, prefix and endpoint, and — given a repository id — the
    /// folder that repository's replica is under, as a local path's is.
    /// </summary>
    /// <param name="destination">The destination's declaration, whose address the caller has found sound.</param>
    /// <param name="repositoryIdHex">The repository whose replica to address, or null for the destination's own prefix.</param>
    internal static AzureBlobLocation AzureBlobLocationOf(DestinationConfiguration destination, string? repositoryIdHex = null)
    {
        var location = new AzureBlobLocation(
            destination.Account!,
            destination.Container!,
            destination.Prefix,
            destination.Endpoint is { } endpoint ? new Uri(endpoint, UriKind.Absolute) : null);
        return repositoryIdHex is null ? location : location.Under(repositoryIdHex);
    }

    /// <summary>
    /// Opens an object-store destination's store — a bucket or a container —
    /// or says why it cannot be: a declaration whose address is defective, no
    /// credential stored that its kind can use, or a shared access signature
    /// past the expiry it states, for which no request is worth sending.
    /// </summary>
    /// <param name="runtime">The service, for the credential and the logger.</param>
    /// <param name="destination">The destination, of a kind <see cref="DestinationKinds.IsObjectStore"/> admits.</param>
    /// <param name="repositoryIdHex">The repository whose replica to open, or null for the destination's own prefix.</param>
    /// <param name="refusal">Why it could not be opened, when it could not.</param>
    /// <exception cref="FallbackPlan.Domain.ClientStateException">The stored credential is damaged.</exception>
    internal static IPrefixedObjectStore? OpenObjectStore(
        ServiceRuntime runtime, DestinationConfiguration destination, string? repositoryIdHex, out string? refusal)
    {
        if (destination.AddressDefect is { } defect)
        {
            refusal = defect;
            return null;
        }

        switch (destination.Kind)
        {
            case DestinationKind.S3:
                if (runtime.DestinationCredentials.TryLoad(destination.Id) is not { } accessKey)
                {
                    refusal = NoCredential(destination);
                    return null;
                }

                refusal = null;
                return new S3ObjectStore(
                    S3LocationOf(destination, repositoryIdHex), accessKey, runtime.LoggerFor<S3ObjectStore>());

            case DestinationKind.AzureBlob:
                if (runtime.DestinationCredentials.TryLoadAzureBlob(destination.Id) is not { } credential)
                {
                    refusal = NoCredential(destination);
                    return null;
                }

                if (credential.Expires is { } expires && expires <= DateTimeOffset.UtcNow)
                {
                    refusal = SignatureLapsed(destination.Name, expires);
                    return null;
                }

                refusal = null;
                return new AzureBlobObjectStore(
                    AzureBlobLocationOf(destination, repositoryIdHex), credential, runtime.LoggerFor<AzureBlobObjectStore>());

            default:
                throw new ArgumentException(
                    $"Destination '{destination.Name}' is not an object store.", nameof(destination));
        }
    }

    /// <summary>What a destination with no credential stored is told, with the two ways to store one.</summary>
    /// <param name="destination">The destination.</param>
    internal static string NoCredential(DestinationConfiguration destination) =>
        $"destination '{destination.Name}' has no "
        + (destination.Kind == DestinationKind.AzureBlob ? "account key or shared access signature" : "access key")
        + " stored — store one in the console's destination editor, or with "
        + $"`fallbackplan destination-credentials {destination.Name}`";

    /// <summary>What a destination whose shared access signature has lapsed is told, with the two ways to replace it.</summary>
    /// <param name="destinationName">The destination.</param>
    /// <param name="expires">When the signature lapsed, as it states.</param>
    internal static string SignatureLapsed(string destinationName, DateTimeOffset expires) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"the shared access signature stored for destination '{destinationName}' expired at "
            + $"{expires.UtcDateTime:yyyy-MM-dd HH:mm} UTC, so nothing was sent under it — store a new one in the "
            + $"console's destination editor, or with `fallbackplan destination-credentials {destinationName} --sas-env`");

    /// <summary>An object store's address in words: its bucket and endpoint, or its container and account.</summary>
    /// <param name="destination">The destination, of a kind <see cref="DestinationKinds.IsObjectStore"/> admits.</param>
    internal static string Describe(DestinationConfiguration destination) => destination.Kind == DestinationKind.AzureBlob
        ? $"container '{destination.Container}' of account '{destination.Account}'"
        : $"bucket '{destination.Bucket}' at {destination.Endpoint}";
}
