using System.Security.Cryptography;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Domain;
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
    public void Store_AFileInThePreReclaimShape_StillLoadsRatherThanReadingAsDamage()
    {
        // The write credential grew a sixth member when the reclaim key
        // landed, and learned to read the five-member shape an older build
        // wrote. Its CONTAINER did not: a stored provisioning is checked
        // against one exact length, so an installation.bin from before that
        // change is 201 bytes where 233 is demanded and is reported as
        // damage. There is no way back from that — TrySave never overwrites,
        // so the operator cannot re-provision, and re-running setup would
        // mint a second salt that no existing archive was written under.
        var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var parameters = RepositoryCreationSettings.Default.KdfParameters;

        var stored = new byte[8 + 168 + KekDerivation.SaltLength + 9];
        "FBPINST1"u8.CopyTo(stored);
        "FBPWCRD1"u8.CopyTo(stored.AsSpan(8));
        RandomNumberGenerator.Fill(stored.AsSpan(16, 160));
        salt.CopyTo(stored.AsSpan(8 + 168));
        var kdf = 8 + 168 + KekDerivation.SaltLength;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            stored.AsSpan(kdf), parameters.MemoryKiB);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            stored.AsSpan(kdf + 4), parameters.Iterations);
        stored[kdf + 8] = parameters.Parallelism;

        var directory = Path.Combine(_harness.StateDirectory, "write-credentials");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "installation.bin"), stored);

        var store = new InstallationCredentialStore(_harness.StateDirectory);
        using var loaded = store.TryLoad();

        Assert.IsNotNull(loaded, "an installation provisioned by an older build is still this installation");
        SequenceAssert.AreEqual(salt, loaded.KdfSalt.ToArray());
        Assert.AreEqual(parameters.MemoryKiB, loaded.KdfParameters.MemoryKiB);
        Assert.AreEqual(parameters.Iterations, loaded.KdfParameters.Iterations);
        Assert.AreEqual(parameters.Parallelism, loaded.KdfParameters.Parallelism);
        Assert.IsTrue(
            loaded.Credential.ReclaimPublicKey.IsEmpty,
            "a credential written before the reclaim key publishes none until it is re-provisioned");
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
        Assert.AreEqual(FormatLimits.FormatVersion, descriptor.FormatVersion);
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
            (await RepositoryLifecycle.CreateAsync(
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

    [TestMethod]
    public async Task Runtime_AConfigurationThatWillNotLoad_StillAnswersDescribeService()
    {
        // describe_service is the first thing a console asks and the verb
        // that decides whether it can talk to this service at all. Reading
        // config.json to answer the setup state put a typo in that file on
        // the path of that answer — found by a live drill, not by a test,
        // which is why this one exists.
        await File.WriteAllTextAsync(
            Path.Combine(_harness.StateDirectory, "config.json"),
            """{"schemaVersion": 3, "backupSets": []}""",
            _timeout.Token);

        await using var runtime = await StartWithoutPassphraseAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var description);
        Assert.AreEqual("setup_required", description.SetupState);

        // And a provisioned installation still answers — reaching the kit
        // step rather than being stuck at setup_required — because the
        // installation credential answers without the configuration at all.
        Save(Store());
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var afterwards);
        Assert.AreEqual("kit_required", afterwards.SetupState);
    }

    [TestMethod]
    public async Task Runtime_SetUpInstallation_RestoresSealedContentUnderAGrant()
    {
        // The set was created from the installation credential, so no per-set
        // credential exists — and the restore ceremony must find the one the
        // set actually opens with rather than telling the owner of a set-up
        // installation to provision a set that setup already provisioned.
        var salt = Save(Store());
        _harness.WriteSourceFile("notes.txt", "sealed until granted");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartWithoutPassphraseAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.AreEqual(JobState.Complete, await RunBackupAsync(runtime, handler));

        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var description);

        var opened = await handler.ExecuteAsync(
            new OpenRestoreSourceCommand("docs", Envelope: SealGrant(description.RestoreGrantRecipient!, salt)),
            _timeout.Token);
        Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
            opened, out var granted, (opened as ServiceError)?.Message ?? opened.GetType().Name);
        var snapshotId = Assert.ContainsSingle(granted.Snapshots).SnapshotId;

        var restoredOut = Path.Combine(_harness.WorkPath, "granted");
        Assert.IsInstanceOfType<Api.RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(snapshotId, null, restoredOut, Source: granted.SourceId, InPlace: true),
                _timeout.Token),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome, string.Join("; ", restored.FailedSample ?? []));
        Assert.AreEqual("sealed until granted", File.ReadAllText(Path.Combine(restoredOut, "notes.txt")));
    }

    [TestMethod]
    public async Task Runtime_SetUpInstallation_OpensTheDestinationsCopyAsARestoreSource()
    {
        // The same credential opens the destination's replica of the set —
        // a v2 replica carries the same descriptor (ADR-0042 §5) — so the
        // restore path that probes destinations must reach it for a set the
        // installation created, not only for one provisioned per set.
        Save(Store());
        _harness.WriteSourceFile("notes.txt", "held at the vault too");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using (var runtime = await StartWithoutPassphraseAsync())
        {
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            Assert.AreEqual(JobState.Complete, await RunBackupAsync(runtime, handler));

            // The fan-out rides the backup; the replica has to have LANDED,
            // so the wait is on the ledger, which whichever pass finishes
            // will stamp.
            Assert.IsInstanceOfType<SyncResult>(
                await handler.ExecuteAsync(new SyncCommand("docs", null), _timeout.Token));
            while (runtime.DestinationSync.Find(_harness.DocsSetId, "vault")
                is not { State: FallbackPlan.Application.DestinationSyncState.InSync })
            {
                _timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(50, _timeout.Token);
            }
        }

        await using (var runtime = await StartWithoutPassphraseAsync())
        {
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            var opened = await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs", "vault"), _timeout.Token);
            Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
                opened, out var replica, (opened as ServiceError)?.Message ?? opened.GetType().Name);
            Assert.AreEqual("vault", replica.Location);
            Assert.ContainsSingle(replica.Snapshots);
            Assert.IsEmpty(replica.Warnings, string.Join("; ", replica.Warnings));
        }
    }

    /// <summary>The restore grant a console seals: the sealing scalar, re-derived under the installation's salt.</summary>
    private static string SealGrant(string recipientHex, byte[] salt)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, RepositoryCreationSettings.Default.KdfParameters, salt, KdfValidationMode.OpenRepository);
        return Convert.ToHexStringLower(
            WriteOnlyProvisioning.SealGrant(Convert.FromHexString(recipientHex), authority.SealingPrivateKey));
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
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                // The fixture's paths share one real volume; the vault is
                // told apart by name, the compliant shape ADR-0051 describes.
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            _timeout.Token);
}
