using System.Security.Cryptography;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The installation credential first-run setup provisions (ADR-0044 §2;
/// FR-SVC-011): one derivation root for the whole machine, from which every
/// set's staging archive is created on its first backup — in place of the
/// silent format-1 archive a passphrase-holding service creates today.
/// </summary>
[TestClass]
public sealed class InstallationCredentialTests : IDisposable
{
    private const string PassphraseText = "the one long passphrase of this installation";

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(5));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public void Provisioning_RoundTripped_KeepsTheCredentialSaltAndParameters()
    {
        // The salt and parameters travel with the credential because every
        // archive stamps them into its own descriptor — they are what lets a
        // restore reproduce this bundle from the passphrase years later.
        var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var parameters = RepositoryCreationSettings.Default.KdfParameters;

        using var passphrase = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, parameters, salt, KdfValidationMode.CreateRepository);

        using var original = new InstallationProvisioning(
            RepositoryWriteCredential.FromBytes(authority.Credential.ToBytes()), salt, parameters);
        using var parsed = InstallationProvisioning.FromBytes(original.ToBytes());

        SequenceAssert.AreEqual(salt, parsed.KdfSalt.ToArray());
        Assert.AreEqual(parameters, parsed.KdfParameters);
        SequenceAssert.AreEqual(
            authority.Credential.SealingPublicKey.ToArray(), parsed.Credential.SealingPublicKey.ToArray());
    }

    [TestMethod]
    public void Provisioning_ASaltOfTheWrongLength_IsRefused()
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, RepositoryCreationSettings.Default.KdfParameters,
            RandomNumberGenerator.GetBytes(KekDerivation.SaltLength), KdfValidationMode.CreateRepository);
        var credential = RepositoryWriteCredential.FromBytes(authority.Credential.ToBytes());

        Assert.ThrowsExactly<ArgumentException>(() => _ = new InstallationProvisioning(
            credential, new byte[KekDerivation.SaltLength - 1], RepositoryCreationSettings.Default.KdfParameters));

        credential.Dispose();
    }

    [TestMethod]
    public void Store_NothingProvisioned_HoldsNothingAndLoadsNull()
    {
        var store = new InstallationCredentialStore(_harness.StateDirectory);

        Assert.IsFalse(store.Holds);
        Assert.IsNull(store.TryLoad());
    }

    [TestMethod]
    public void Store_ADamagedFile_IsNamedAsDamageRatherThanReportedAsNotSetUp()
    {
        // Reporting "not set up" would send the operator back through setup,
        // which mints a NEW salt — and every archive already written under
        // the old one becomes unopenable by the passphrase that made it.
        var store = new InstallationCredentialStore(_harness.StateDirectory);
        Save(store);

        var path = Path.Combine(_harness.StateDirectory, "write-credentials", "installation.bin");
        var bytes = File.ReadAllBytes(path);
        bytes[3] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.IsTrue(store.Holds, "the file is still there, so the store must not pretend otherwise");
        var failure = Assert.ThrowsExactly<RepositoryOpenException>(() => store.TryLoad());
        Assert.Contains("damaged", failure.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Runtime_SetUpInstallation_CreatesANewSetsArchiveWriteOnlyRatherThanFormatOne()
    {
        // The heart of ADR-0044: a set created after setup is write-only
        // because the installation is, not because anybody remembered a
        // dialog — and it backs up with no passphrase in the environment.
        _harness.WriteSourceFile("notes.txt", "written under the installation root");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartWithoutPassphraseAsync();
        Save(Store());

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.AreEqual(JobState.Complete, await RunBackupAsync(runtime, handler));

        var descriptor = await ReadDescriptorAsync();
        Assert.IsTrue(
            RepositoryLifecycle.IsWriteOnly(descriptor),
            $"the archive is format {descriptor.FormatVersion}, not write-only");
    }

    [TestMethod]
    public async Task Runtime_SetUpInstallation_StampsTheInstallationsSaltIntoEveryArchive()
    {
        // One salt across the installation is what makes one passphrase open
        // every set. If an archive recorded a different one, the passphrase
        // that created it would not reproduce its keys.
        _harness.WriteSourceFile("notes.txt", "one root, several sets");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartWithoutPassphraseAsync();
        var salt = Save(Store());

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.AreEqual(JobState.Complete, await RunBackupAsync(runtime, handler));

        var descriptor = await ReadDescriptorAsync();
        SequenceAssert.AreEqual(salt, descriptor.KdfSalt.ToArray());

        // And the passphrase alone reproduces the sealing key the archive
        // records — which is exactly what a restore will have to do.
        using var passphrase = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, descriptor.KdfParameters, descriptor.KdfSalt.Span, KdfValidationMode.OpenRepository);
        SequenceAssert.AreEqual(
            descriptor.SealingPublicKey.ToArray(), authority.Credential.SealingPublicKey.ToArray());
    }

    [TestMethod]
    public async Task Runtime_AWriteOnlyArchiveFromADifferentPassphrase_IsRefusedRatherThanWrittenTo()
    {
        // A replica moved in from another installation, or a state directory
        // restored over the top of one. Writing to it with the wrong
        // credential would add records this archive's passphrase can never
        // read back.
        _harness.WriteSourceFile("notes.txt", "not this installation's");
        _harness.WriteConfiguration("every 1h");

        var strangerSalt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        using (var strangerPassphrase = Passphrase.Create("an entirely different installation's passphrase"))
        using (var strangerAuthority = WriteOnlyDerivation.Derive(
            strangerPassphrase, RepositoryCreationSettings.Default.KdfParameters,
            strangerSalt, KdfValidationMode.CreateRepository))
        {
            Directory.CreateDirectory(_harness.RepositoryPath);
            (await RepositoryLifecycle.CreateWriteOnlyFromCredentialAsync(
                new LocalFileSystemObjectStore(_harness.RepositoryPath), strangerAuthority.Credential,
                strangerSalt, RepositoryCreationSettings.Default.KdfParameters, "stranger",
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _timeout.Token)).Dispose();
        }

        await using var runtime = await StartWithoutPassphraseAsync();
        Save(Store());

        // Asserted at the open rather than through a backup: this is the
        // refusal itself, and a job state would only tell us that something
        // downstream of it went wrong.
        var failure = await Assert.ThrowsExactlyAsync<RepositoryOpenException>(
            async () => await runtime.ArchiveForAsync(runtime.Configuration.BackupSets[0], _timeout.Token));

        Assert.Contains("different passphrase", failure.Message, StringComparison.Ordinal);
        Assert.Contains("adopt", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Runtime_AFormatOneArchiveThatPredatesSetup_KeepsOpeningWithThePassphrase()
    {
        // Write-only is chosen at creation (ADR-0042). Setting up an
        // installation must not quietly change what an existing set is.
        _harness.WriteSourceFile("notes.txt", "made before setup");
        _harness.WriteConfiguration("every 1h");
        await _harness.CreateRepositoryAsync();

        using var passphrase = Passphrase.Create(Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            passphrase, _timeout.Token);
        Save(Store());

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.AreEqual(JobState.Complete, await RunBackupAsync(runtime, handler));

        var descriptor = await ReadDescriptorAsync();
        Assert.IsFalse(
            RepositoryLifecycle.IsWriteOnly(descriptor),
            "the pre-existing format 1 archive must not have been migrated");
    }

    [TestMethod]
    public async Task Runtime_NoPassphraseAndNoSetup_RefusesNamingSetupAsARemedy()
    {
        _harness.WriteSourceFile("notes.txt", "nothing is set up");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartWithoutPassphraseAsync();

        var failure = await Assert.ThrowsExactlyAsync<RepositoryOpenException>(
            async () => await runtime.ArchiveForAsync(
                runtime.Configuration.BackupSets[0], _timeout.Token));

        Assert.Contains("setup", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Runtime_IsSetUp_ReportsWhatIsActuallyHeld()
    {
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartWithoutPassphraseAsync();

        Assert.IsFalse(runtime.IsSetUp, "a fresh state directory is not set up");

        Save(Store());

        Assert.IsTrue(runtime.IsSetUp);
    }

    [TestMethod]
    public async Task Runtime_ASetProvisionedTheOlderPerSetWay_CountsAsSetUp()
    {
        // An installation that predates ADR-0044 is already working. Telling
        // it to run setup would offer a ceremony whose only outcome is a
        // second, unrelated derivation root.
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartWithoutPassphraseAsync();
        Assert.IsFalse(runtime.IsSetUp);

        var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        using var passphrase = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, RepositoryCreationSettings.Default.KdfParameters, salt, KdfValidationMode.CreateRepository);
        new WriteCredentialStore(_harness.StateDirectory).Save(_harness.DocsSetId, authority.Credential);

        Assert.IsTrue(runtime.IsSetUp);
    }

    [TestMethod]
    public void Store_ASecondProvisioning_IsRefusedByTheFilesystemRatherThanOverwritten()
    {
        // The handler checks first and refuses politely. This is what makes
        // that refusal true rather than merely likely: two setups submitted
        // at once would both pass a check-then-act, and the loser would
        // replace a salt that archives already record.
        var store = new InstallationCredentialStore(_harness.StateDirectory);
        var first = Save(store);

        var parameters = RepositoryCreationSettings.Default.KdfParameters;
        var secondSalt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        using var passphrase = Passphrase.Create("an entirely different second attempt");
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, parameters, secondSalt, KdfValidationMode.CreateRepository);
        using var second = new InstallationProvisioning(
            RepositoryWriteCredential.FromBytes(authority.Credential.ToBytes()), secondSalt, parameters);

        Assert.IsFalse(store.TrySave(second));

        using var held = store.TryLoad();
        SequenceAssert.AreEqual(first, held!.KdfSalt.ToArray());
        Assert.IsFalse(
            File.Exists(Path.Combine(_harness.StateDirectory, "write-credentials", "installation.bin.tmp")),
            "the loser must not leave key material in a .tmp beside the real one");
    }

    /// <summary>Provisions the installation as setup would, returning the salt it used.</summary>
    private static byte[] Save(InstallationCredentialStore store)
    {
        var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var parameters = RepositoryCreationSettings.Default.KdfParameters;

        using var passphrase = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, parameters, salt, KdfValidationMode.CreateRepository);
        using var provisioning = new InstallationProvisioning(
            RepositoryWriteCredential.FromBytes(authority.Credential.ToBytes()), salt, parameters);

        Assert.IsTrue(store.TrySave(provisioning));
        return salt;
    }

    private async Task<FallbackPlan.Repository.Format.Descriptor.RepositoryDescriptor> ReadDescriptorAsync() =>
        await RepositoryLifecycle.ReadDescriptorAsync(
            new LocalFileSystemObjectStore(_harness.RepositoryPath), _timeout.Token);

    /// <summary>Runs the "docs" backup and waits for the terminal observation.</summary>
    private async Task<JobState> RunBackupAsync(ServiceRuntime runtime, ServiceCommandHandler handler)
    {
        var progress = runtime.Progress.WatchAsync(_timeout.Token);
        var watching = Task.Run(
            async () =>
            {
                await foreach (var observation in progress)
                {
                    if (observation.Progress.State is JobState.Complete or JobState.CompletedWithFailures
                        or JobState.FailedPermanent or JobState.FailedRecoverable)
                    {
                        return observation.Progress.State;
                    }
                }

                return JobState.Pending;
            },
            _timeout.Token);

        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand("docs", Full: false), _timeout.Token));
        return await watching;
    }

    /// <summary>This harness's installation credential store.</summary>
    private InstallationCredentialStore Store() => new(_harness.StateDirectory);

    private async Task<ServiceRuntime> StartWithoutPassphraseAsync() =>
        await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            passphrase: null,
            _timeout.Token);
}
