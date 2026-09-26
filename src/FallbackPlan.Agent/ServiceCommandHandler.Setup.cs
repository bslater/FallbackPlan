using FallbackPlan.Api;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Agent;

/// <summary>
/// First-run setup (ADR-0044): the verb that gives an installation the
/// passphrase everything derives from. The client derived the write bundle
/// and sealed it to this service's recipient key; here the envelope is
/// opened and the credential stored, and every set's staging archive is
/// created from it afterwards. The passphrase itself never reached this
/// process.
/// </summary>
public sealed partial class ServiceCommandHandler
{
    /// <summary>
    /// Said in two places — the check, and the race that check cannot close —
    /// because they are the same situation arriving a moment apart and should
    /// not read as two different problems.
    /// </summary>
    private const string AlreadySetUp =
        "This installation is already set up. Its passphrase can never be changed — that is what "
        + "makes the archives readable at all — so setup runs exactly once (ADR-0044, ADR-0042 §11).";

    private ServiceResult ProvisionInstallation(ProvisionInstallationCommand command)
    {
        if (Scope == CallerScope.Remote)
        {
            // ADR-0044 §5. Choosing the one secret that can never be changed
            // sits inside the line ADR-0028 §6 already draws around remote
            // clients — and a remote operator cannot see the machine they
            // would be committing to this passphrase forever.
            return new ServiceError(
                ServiceErrorReason.Refused,
                "First-run setup runs on the service's own machine. Open the console there, or use "
                + "`fallbackplan-agent setup` (ADR-0044 §5).");
        }

        if (runtime.IsSetUp)
        {
            // Not idempotent, and deliberately so: a second envelope carries a
            // second salt, and accepting it would leave every archive already
            // written unopenable by the passphrase that made it. There is no
            // passphrase change for a v2 repository (ADR-0042 §11), and this
            // is where that is enforced rather than merely documented.
            return new ServiceError(
                ServiceErrorReason.Refused,
                AlreadySetUp);
        }

        byte[] envelope;
        try
        {
            envelope = Convert.FromHexString(command.Envelope);
        }
        catch (FormatException)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, "The setup envelope is not hex.");
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
                "The setup envelope does not open — it was sealed to a different service's recipient key.");
        }

        InstallationProvisioning provisioning;
        try
        {
            provisioning = new InstallationProvisioning(credential, kdfSalt, kdfParameters);
        }
        catch (ArgumentException)
        {
            // Ownership passes to the provisioning only once it exists, so a
            // refused construction leaves the credential ours to zero.
            credential.Dispose();
            return new ServiceError(
                ServiceErrorReason.InvalidArgument, "The setup envelope does not carry a usable derivation.");
        }

        using (provisioning)
        {
            if (!runtime.InstallationCredential.TrySave(provisioning))
            {
                // Lost a race with another setup. Same answer as the check
                // above, because it is the same situation arriving a
                // moment later.
                return new ServiceError(
                    ServiceErrorReason.Refused,
                    AlreadySetUp);
            }
        }

        return new ConfigurationChangeResult(
        [
            "This installation is set up. Every backup set created from now on seals its contents to the "
                + "passphrase you just chose.",
            "The passphrase is not stored here, or in the backup, or anywhere else — this service can add to "
                + "your history and browse its structure, but it can never read your files back.",
            "It can never be changed, and if it is lost the backup is unrecoverable. Keep it somewhere safe.",
        ]);
    }
}
