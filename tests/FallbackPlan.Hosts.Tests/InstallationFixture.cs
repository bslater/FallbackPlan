using System.Security.Cryptography;
using FallbackPlan.Agent;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A set-up installation for a fixture that builds its own state directory
/// rather than going through <see cref="HostHarness"/>: the credential the
/// service opens every set with, written where the service keeps it, from
/// the passphrase alone. What first-run setup stores, without the verb.
/// </summary>
internal static class InstallationFixture
{
    /// <summary>Provisions <paramref name="stateDirectory"/> from <paramref name="passphraseText"/> under a fresh salt.</summary>
    public static void Provision(string stateDirectory, string passphraseText)
    {
        var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var parameters = RepositoryCreationSettings.Default.KdfParameters;

        using var passphrase = Passphrase.Create(passphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, parameters, salt, KdfValidationMode.CreateRepository);
        using var provisioning = new InstallationProvisioning(
            RepositoryWriteCredential.FromBytes(authority.Credential.ToBytes()), salt, parameters);

        Assert.IsTrue(
            new InstallationCredentialStore(stateDirectory).TrySave(provisioning),
            "the installation credential was not saved — is the state directory already set up?");
    }
}
