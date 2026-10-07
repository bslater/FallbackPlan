using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.AzureBlob;
using FallbackPlan.Storage.S3;

namespace FallbackPlan.Agent;

public sealed partial class ServiceCommandHandler
{
    /// <summary>The longest access key id this service stores; several times what any provider issues.</summary>
    private const int MaximumAccessKeyIdLength = 128;

    /// <summary>
    /// Stores the credential an object-store destination's requests are
    /// authorised by (contract 1.60, ADR-0091; contract 1.61, ADR-0093): an
    /// S3-compatible store's access key, or an Azure Blob container's account
    /// key or shared access signature. The secret arrives sealed to this
    /// service's recipient key for this destination, and for an access key
    /// its key id (NFR-SEC-009), is opened here and held in the state
    /// directory (NFR-SEC-012), and is never said back by anything.
    /// </summary>
    private ServiceResult SetDestinationCredentials(SetDestinationCredentialsCommand command)
    {
        var destination = runtime.Configuration.FindDestination(command.DestinationName);
        if (destination is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No destination named '{command.DestinationName}' is declared.");
        }

        if (!destination.Kind.IsObjectStore())
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"Destination '{destination.Name}' is a {KindName(destination.Kind)} destination, which signs no "
                + "requests: only an s3 or azure-blob destination holds a credential.");
        }

        // A request that names no kind is from before contract 1.61, when an
        // access key was the only credential there was.
        var kind = command.Kind ?? DestinationCredentialStore.AccessKeyKind;
        if (kind is not (DestinationCredentialStore.AccessKeyKind
            or DestinationCredentialStore.SharedKeyKind
            or DestinationCredentialStore.SharedAccessSignatureKind))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"'{kind}' is not a credential kind ({DestinationCredentialStore.AccessKeyKind}, "
                + $"{DestinationCredentialStore.SharedKeyKind}, {DestinationCredentialStore.SharedAccessSignatureKind}).");
        }

        if (!DestinationCredentialStore.Fits(kind, destination.Kind))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                destination.Kind == DestinationKind.AzureBlob
                    ? $"Destination '{destination.Name}' is an azure-blob destination, which takes an account key "
                        + $"({DestinationCredentialStore.SharedKeyKind}) or a shared access signature "
                        + $"({DestinationCredentialStore.SharedAccessSignatureKind}); an {kind} is a credential of "
                        + "another API."
                    : $"Destination '{destination.Name}' is an s3 destination, which takes an access key "
                        + $"({DestinationCredentialStore.AccessKeyKind}); a {kind} is a credential of another API.");
        }

        return kind == DestinationCredentialStore.AccessKeyKind
            ? StoreAccessKey(destination, command)
            : StoreAzureBlobCredential(destination, command, kind);
    }

    private ServiceResult StoreAccessKey(DestinationConfiguration destination, SetDestinationCredentialsCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.AccessKeyId)
            || command.AccessKeyId.Length > MaximumAccessKeyIdLength
            || command.AccessKeyId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"An access key id is 1 to {MaximumAccessKeyIdLength} characters with no spaces or control "
                + "characters in it.");
        }

        if (!TryReadEnvelope(command.Envelope, "access-key", out var envelope, out var notHex))
        {
            return notHex;
        }

        string secret;
        try
        {
            secret = runtime.GrantRecipient.OpenAccessKeySecret(envelope, destination.Name, command.AccessKeyId);
        }
        catch (Exception exception) when (exception is SealedContentException or ArgumentException)
        {
            // One refusal for every envelope that is not this one: another
            // service's, another destination's, another key id's, or damaged.
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                "The access-key envelope does not open — it was sealed to a different service's recipient key, "
                + $"or for a destination or key id other than '{destination.Name}' and {command.AccessKeyId}.");
        }

        runtime.DestinationCredentials.Save(destination.Id, new S3Credentials(command.AccessKeyId, secret));
        return new ConfigurationChangeResult(
        [
            $"Access key {command.AccessKeyId} stored for destination '{destination.Name}'; its next sync signs "
            + "with it, and `sync` starts one now.",
        ]);
    }

    private ServiceResult StoreAzureBlobCredential(
        DestinationConfiguration destination, SetDestinationCredentialsCommand command, string kind)
    {
        var signature = kind == DestinationCredentialStore.SharedAccessSignatureKind;
        var noun = signature ? "shared access signature" : "account key";
        if (command.AccessKeyId is not null)
        {
            // The key id is bound into an access key's envelope and sent in
            // clear; nothing of an account key or a signature is either, so
            // one given here is a mistake about which credential this is.
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"An access key id goes with an access key only; an {noun} is stored without one.");
        }

        if (!TryReadEnvelope(command.Envelope, signature ? "shared-access-signature" : "account-key", out var envelope, out var notHex))
        {
            return notHex;
        }

        string secret;
        try
        {
            secret = signature
                ? runtime.GrantRecipient.OpenSharedAccessSignature(envelope, destination.Name)
                : runtime.GrantRecipient.OpenAccountKey(envelope, destination.Name);
        }
        catch (Exception exception) when (exception is SealedContentException or ArgumentException)
        {
            // As an access key's: one refusal for every envelope that is not
            // this one, whether another service's, another destination's,
            // sealed as the other kind of credential, or damaged.
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"The {noun} envelope does not open — it was sealed to a different service's recipient key, for "
                + $"a destination other than '{destination.Name}', or as another kind of credential.");
        }

        var credentialKind = signature ? AzureBlobCredentialKind.SharedAccessSignature : AzureBlobCredentialKind.SharedKey;
        if (AzureBlobCredentials.DefectOf(credentialKind, secret) is { } defect)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, defect);
        }

        var credential = signature ? AzureBlobCredentials.SharedAccessSignature(secret) : AzureBlobCredentials.SharedKey(secret);
        if (credential.Expires is { } lapsed && lapsed <= DateTimeOffset.UtcNow)
        {
            // Every request under it would be refused, and the refusal would
            // arrive as a failed sync rather than as this answer.
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The shared access signature for destination '{destination.Name}' expired at "
                    + $"{lapsed.UtcDateTime:yyyy-MM-dd HH:mm} UTC, so the store would refuse everything sent under "
                    + $"it — issue one that has not, and store that."));
        }

        runtime.DestinationCredentials.Save(destination.Id, credential);
        return new ConfigurationChangeResult(
        [
            !signature
                ? $"The account key for destination '{destination.Name}' is stored; its next sync signs with it, "
                    + "and `sync` starts one now."
                : credential.Expires is { } expires
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"The shared access signature for destination '{destination.Name}' is stored, honoured "
                        + $"until {expires.UtcDateTime:yyyy-MM-dd HH:mm} UTC; its next sync sends it, and `sync` "
                        + $"starts one now. Store a new one before then.")
                    : $"The shared access signature for destination '{destination.Name}' is stored; it states no "
                        + "expiry. Its next sync sends it, and `sync` starts one now.",
        ]);
    }

    /// <summary>Reads an envelope's hex, or says it is not hex.</summary>
    private static bool TryReadEnvelope(
        string? hex, string what, out byte[] envelope, [NotNullWhen(false)] out ServiceError? notHex)
    {
        try
        {
            envelope = Convert.FromHexString(hex ?? string.Empty);
            notHex = null;
            return true;
        }
        catch (FormatException)
        {
            envelope = [];
            notHex = new ServiceError(ServiceErrorReason.InvalidArgument, $"The {what} envelope is not hex.");
            return false;
        }
    }

    /// <summary>
    /// Whether the service holds a credential the destination can use, which
    /// one, and when a signature lapses (contract 1.61) — never what it is
    /// (ADR-0091, ADR-0093). A kind that signs nothing is not said to lack one.
    /// </summary>
    private (bool? Stored, string? Kind, string? Expires) CredentialHeld(DestinationConfiguration destination)
    {
        var credentials = runtime.DestinationCredentials;
        if (!destination.Kind.IsObjectStore())
        {
            return (null, null, null);
        }

        if (!credentials.HoldsFor(destination))
        {
            return (false, null, null);
        }

        var kind = credentials.KindHeld(destination.Id);
        if (kind != DestinationCredentialStore.SharedAccessSignatureKind)
        {
            return (true, kind, null);
        }

        try
        {
            return (true, kind, credentials.TryLoadAzureBlob(destination.Id)?.Expires?.UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }
        catch (Domain.ClientStateException)
        {
            // Damage is said where the credential is used; a listing says
            // what it can.
            return (true, kind, null);
        }
    }

    /// <summary>The contract's spelling of how a bucket is named in a request.</summary>
    private static string? AddressingName(BucketAddressing? addressing) => addressing switch
    {
        BucketAddressing.Path => "path",
        BucketAddressing.VirtualHost => "virtual-host",
        _ => null,
    };

    /// <summary>Parses the contract's spelling of a bucket addressing; an empty text is none.</summary>
    private static bool TryParseAddressing(string? text, out BucketAddressing? addressing)
    {
        (var parsed, addressing) = text switch
        {
            null or "" => (true, (BucketAddressing?)null),
            "path" => (true, BucketAddressing.Path),
            "virtual-host" => (true, BucketAddressing.VirtualHost),
            _ => (false, null),
        };

        return parsed;
    }
}
