using FallbackPlan.Api;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Agent;

/// <summary>
/// The write-only provisioning surface (ADR-0042 §4, §10): one verb serving
/// both ceremonies. The admin client derived the write bundle from the
/// passphrase and sealed it to this service's recipient key; here the
/// envelope is opened and the set is either <b>created</b> as a fresh v2
/// repository or <b>adopts</b> an existing one after the derived sealing
/// public key proves against the descriptor's copy. The passphrase itself
/// never reached this process.
/// </summary>
public sealed partial class ServiceCommandHandler
{
    private async ValueTask<ServiceResult> ProvisionWriteOnlySetAsync(
        ProvisionWriteOnlySetCommand command, CancellationToken cancellationToken)
    {
        var set = runtime.Configuration.FindSet(command.SetName);
        if (set is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No backup set named '{command.SetName}' is configured.");
        }

        byte[] envelope;
        try
        {
            envelope = Convert.FromHexString(command.Envelope);
        }
        catch (FormatException)
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument, "The provisioning envelope is not hex.");
        }

        RepositoryWriteCredential credential;
        byte[] kdfSalt;
        Domain.Configuration.Argon2Parameters kdfParameters;
        try
        {
            (credential, kdfSalt, kdfParameters) = runtime.GrantRecipient.OpenProvision(envelope);
        }
        catch (SealedContentException)
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                "The provisioning envelope does not open — it was sealed to a different service's recipient key.");
        }

        using (credential)
        {
            var path = runtime.ArchivePath(set.Id);
            Directory.CreateDirectory(path);
            var store = new LocalFileSystemObjectStore(path);
            var lines = new List<string>();

            if (runtime.ArchiveExists(set.Id))
            {
                // Adoption (ADR-0042 §10): a descriptor is already there — a
                // moved archive, a restored replica, or a state directory that
                // was lost. Prove the passphrase reproduces THIS repository's
                // keys before storing anything.
                var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(store, cancellationToken)
                    .ConfigureAwait(false);
                if (!RepositoryLifecycle.IsWriteOnly(descriptor))
                {
                    return new ServiceError(
                        ServiceErrorReason.InvalidArgument,
                        $"Set '{set.Name}' has a format {descriptor.FormatVersion} staging archive — an existing "
                        + "repository cannot become write-only; write-only is chosen at creation (ADR-0042).");
                }

                if (!credential.SealingPublicKey.SequenceEqual(descriptor.SealingPublicKey.Span))
                {
                    return new ServiceError(
                        ServiceErrorReason.InvalidArgument,
                        "The derived sealing public key does not match this repository's descriptor — the "
                        + "passphrase it was derived from is not this repository's.");
                }

                lines.Add($"Set '{set.Name}' adopted its existing write-only archive; the write credential is stored.");
            }
            else
            {
                var opened = await RepositoryLifecycle.CreateWriteOnlyFromCredentialAsync(
                        store, credential, kdfSalt, kdfParameters, createdBy: Environment.MachineName,
                        (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken)
                    .ConfigureAwait(false);
                opened.Dispose();
                lines.Add($"Set '{set.Name}' now has a write-only (format 2) staging archive; the write credential is stored.");
            }

            runtime.WriteCredentials.Save(set.Id, credential);
            lines.Add("This service can add to the archive and read its structure, but never file contents.");
            lines.Add("Restore and adoption need the passphrase again; if it is lost the backup is unrecoverable.");
            return new ConfigurationChangeResult(lines);
        }
    }
}
