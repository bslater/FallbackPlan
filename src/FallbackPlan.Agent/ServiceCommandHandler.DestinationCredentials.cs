using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.S3;

namespace FallbackPlan.Agent;

public sealed partial class ServiceCommandHandler
{
    /// <summary>The longest access key id this service stores; several times what any provider issues.</summary>
    private const int MaximumAccessKeyIdLength = 128;

    /// <summary>
    /// Stores the access key an S3-compatible destination's requests are
    /// signed with (contract 1.60, ADR-0091). The secret arrives sealed to
    /// this service's recipient key for this destination and key id
    /// (NFR-SEC-009), is opened here and held in the state directory
    /// (NFR-SEC-012), and is never said back by anything.
    /// </summary>
    private ServiceResult SetDestinationCredentials(SetDestinationCredentialsCommand command)
    {
        var destination = runtime.Configuration.FindDestination(command.DestinationName);
        if (destination is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No destination named '{command.DestinationName}' is declared.");
        }

        if (destination.Kind != DestinationKind.S3)
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"Destination '{destination.Name}' is a {KindName(destination.Kind)} destination, which signs no "
                + "requests: only an s3 destination holds an access key.");
        }

        if (string.IsNullOrWhiteSpace(command.AccessKeyId)
            || command.AccessKeyId.Length > MaximumAccessKeyIdLength
            || command.AccessKeyId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"An access key id is 1 to {MaximumAccessKeyIdLength} characters with no spaces or control "
                + "characters in it.");
        }

        byte[] envelope;
        try
        {
            envelope = Convert.FromHexString(command.Envelope ?? string.Empty);
        }
        catch (FormatException)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, "The access-key envelope is not hex.");
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
