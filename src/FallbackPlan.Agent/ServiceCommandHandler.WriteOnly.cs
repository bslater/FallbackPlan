using FallbackPlan.Api;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;

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
        catch (Exception malformed) when (malformed is SealedContentException or ArgumentException)
        {
            // Too short to be an envelope at all, or sealed to someone else:
            // both are "not an envelope this service can open".
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
            var store = StoreComposition.OpenLocal(path);
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
                var opened = await RepositoryLifecycle.CreateAsync(
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

    /// <summary>
    /// Opens this run's reclaim grant, or explains why the run cannot proceed
    /// without one ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three answers, and the middle one is the whole point. A repository that
    /// does not declare <c>reclaim-authority</c> needs nothing. A v1
    /// repository that does declare it derives the key from the master key it
    /// already holds, so it needs nothing either — §3 is explicit that the
    /// split defends the write-only shape and not that one. A <b>write-only</b>
    /// repository that declares it cannot derive the key at all, so without a
    /// grant it is refused by name rather than falling back to the key it
    /// publishes with.
    /// </para>
    /// <para>
    /// A dry run authors nothing and therefore needs no authority: refusing to
    /// even report would make the safe half of retention depend on the
    /// dangerous half's credential.
    /// </para>
    /// </remarks>
    private async ValueTask<(Repository.Crypto.ReclaimAuthority? Grant, ServiceError? Refusal)> OpenReclaimGrantAsync(
        Application.BackupSetConfiguration set,
        ArchiveHandle archive,
        bool apply,
        string? envelopeHex,
        CancellationToken cancellationToken)
    {
        var declares = archive.Repository.Descriptor.RequiredFeatures.Contains(
            Repository.Format.Descriptor.RepositoryDescriptorCodec.FeatureReclaimAuthority);

        if (!declares || !apply)
        {
            return (null, null);
        }

        if (envelopeHex is null or { Length: 0 })
        {
            return (null, new ServiceError(
                ServiceErrorReason.Refused,
                $"Set '{set.Name}': applying retention needs a reclaim grant: this service "
                + "holds the key that publishes and not the key that authorises a deletion (ADR-0055). "
                + "Derive the grant from the passphrase, seal it to this service's recipient key, and send it "
                + "with the command."));
        }

        byte[] root;
        try
        {
            root = runtime.GrantRecipient.OpenGrant(Convert.FromHexString(envelopeHex));
        }
        catch (FormatException)
        {
            return (null, new ServiceError(
                ServiceErrorReason.InvalidArgument, "The reclaim-grant envelope is not hex."));
        }
        catch (Repository.Crypto.SealedContentException)
        {
            return (null, new ServiceError(
                ServiceErrorReason.InvalidArgument,
                "The reclaim-grant envelope does not open — it was sealed to a different service's recipient key."));
        }

        Repository.Crypto.ReclaimAuthority grant;
        try
        {
            grant = new Repository.Crypto.ReclaimAuthority(root);
        }
        catch (ArgumentException)
        {
            return (null, new ServiceError(
                ServiceErrorReason.InvalidArgument,
                "The reclaim-grant envelope did not carry a reclaim sub-root of the expected length."));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(root);
        }

        // Proved before it authors anything: a wrong grant would otherwise
        // write tombstones nothing can verify, and the next sweep would report
        // them as forgeries — an alarm about an attack that never happened.
        if (!await Retention.StagingSweep.GrantProvesOutAsync(
            archive.Store, archive.Repository, grant, cancellationToken).ConfigureAwait(false))
        {
            grant.Dispose();
            return (null, new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"The reclaim grant does not verify set '{set.Name}''s existing tombstones — it was derived from "
                + "a different passphrase."));
        }

        return (grant, null);
    }
}
