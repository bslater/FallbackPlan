using System.Security.Cryptography;
using FallbackPlan.Agent;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// A set-up installation for a fixture, and the archive it wrote opened for
/// a test. The service creates every set from the stored installation
/// credential, so a fixture that wants the archives a real pass produces
/// provisions the state directory first — from the passphrase alone, which
/// is what first-run setup stores — and opens them afterwards the way the
/// CLI's direct mode does: the passphrase re-derives the whole authority.
/// </summary>
internal static class WriteOnlyInstallation
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

    /// <summary>
    /// A restore grant for a service started on <paramref name="stateDirectory"/>:
    /// the sealing scalar, re-derived from the passphrase under the
    /// installation's own salt and sealed to the service's recipient key —
    /// the ceremony a console performs (ADR-0042 §5), rendered as hex.
    /// </summary>
    public static string RestoreGrant(string stateDirectory, string passphraseText, string recipientHex)
    {
        using var provisioning = new InstallationCredentialStore(stateDirectory).TryLoad()
            ?? throw new InvalidOperationException("the state directory is not set up");
        using var passphrase = Passphrase.Create(passphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, provisioning.KdfParameters, provisioning.KdfSalt, KdfValidationMode.OpenRepository);
        return Convert.ToHexStringLower(
            WriteOnlyProvisioning.SealGrant(Convert.FromHexString(recipientHex), authority.SealingPrivateKey));
    }

    /// <summary>Opens the write-only archive at <paramref name="store"/> with the passphrase, authority and all.</summary>
    public static async Task<OpenedArchive> OpenAsync(
        IObjectStore store, string passphraseText, CancellationToken cancellationToken)
    {
        using var passphrase = Passphrase.Create(passphraseText);
        var (repository, authority) = await RepositoryLifecycle.OpenWriteOnlyForReadAsync(
            store, passphrase, cancellationToken);
        return new OpenedArchive(repository, authority);
    }
}

/// <summary>
/// An archive a test opened with its passphrase: the repository, the read
/// authority a reader needs for sealed content, and the reclaim authority a
/// collection run needs to author a tombstone (ADR-0055 §6) — which the
/// write credential deliberately cannot derive, so a test that applies
/// retention hands the runner this one.
/// </summary>
internal sealed class OpenedArchive(OpenedRepository repository, RepositoryReadAuthority authority) : IDisposable
{
    private ReclaimAuthority? _reclaim;

    /// <summary>The opened repository.</summary>
    public OpenedRepository Repository { get; } = repository;

    /// <summary>The full read authority the passphrase derived.</summary>
    public RepositoryReadAuthority Authority { get; } = authority;

    /// <summary>The grant a collection run signs tombstones under.</summary>
    public ReclaimAuthority Reclaim => _reclaim ??= new ReclaimAuthority(Authority.ReclaimKeySeed);

    /// <inheritdoc />
    public void Dispose()
    {
        _reclaim?.Dispose();
        Authority.Dispose();
        Repository.Dispose();
    }
}
