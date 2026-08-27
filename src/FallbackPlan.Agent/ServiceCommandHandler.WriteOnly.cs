using FallbackPlan.Api;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
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
            // The set's repository lives where its shape says (ADR-0046): a
            // direct-ship set's is its metadata store beside the state, a
            // staging set's is its staging archive. Routing by flag keeps a
            // provision-then-flag ceremony from minting a staging archive
            // that would read as a bogus migration source — and existence is
            // asked of the SAME path, so an already-captured direct-ship set
            // adopts its metadata store instead of mis-detecting against an
            // empty staging directory.
            var path = set.DirectShip ? runtime.SetMetadataPath(set.Id) : runtime.ArchivePath(set.Id);
            var exists = File.Exists(Path.Combine(path, RepositoryLifecycle.DescriptorKey.Value));
            Directory.CreateDirectory(path);
            var store = new LocalFileSystemObjectStore(path);
            var lines = new List<string>();

            if (exists)
            {
                // Adoption (ADR-0042 §10): a descriptor is already there — a
                // moved archive, a restored replica, or a state directory that
                // was lost. Prove the passphrase reproduces THIS repository's
                // keys before storing anything.
                RepositoryDescriptor descriptor;
                try
                {
                    descriptor = await RepositoryLifecycle.ReadDescriptorAsync(store, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (RepositoryOpenException damaged)
                {
                    // A descriptor that no longer verifies is the archive's
                    // problem, named as such — never an unhandled crash of
                    // the provisioning verb.
                    return new ServiceError(
                        ServiceErrorReason.Failed,
                        $"Set '{set.Name}' has a repository whose descriptor does not read: {damaged.Message}");
                }
                if (!RepositoryLifecycle.IsWriteOnly(descriptor))
                {
                    return new ServiceError(
                        ServiceErrorReason.InvalidArgument,
                        $"Set '{set.Name}' has a format {descriptor.FormatVersion} repository — an existing "
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
                lines.Add(set.DirectShip
                    ? $"Set '{set.Name}' now has a write-only (format 2) metadata store; the write credential is stored."
                    : $"Set '{set.Name}' now has a write-only (format 2) staging archive; the write credential is stored.");
            }

            runtime.WriteCredentials.Save(set.Id, credential);
            lines.Add("This service can add to the archive and read its structure, but never file contents.");
            lines.Add("Restore and adoption need the passphrase again; if it is lost the backup is unrecoverable.");
            return new ConfigurationChangeResult(lines);
        }
    }
}
