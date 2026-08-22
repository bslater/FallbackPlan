using System.Security.Cryptography;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;
using RestoreResult = FallbackPlan.Api.RestoreResult;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Write-only sets over the service (ADR-0042; FR-WOR-001..005, NFR-SEC-009
/// as amended, NFR-SEC-010): the provisioning ceremony creates a v2 staging
/// archive from a sealed envelope — the service never sees the passphrase —
/// backups, browsing and planning run passphrase-free, a grant-less restore
/// degrades honestly instead of failing the verbs, a sealed restore grant
/// brings the bytes back identical, and moving the archive to a fresh state
/// directory is an adoption that costs the passphrase exactly once.
/// </summary>
[TestClass]
public sealed class WriteOnlySetTests : IDisposable
{
    private const string PassphraseText = "the one long passphrase of this drill";

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(5));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task WriteOnlySet_ProvisionBackupBrowseAndGrantedRestore_WorksWithoutAServicePassphrase()
    {
        _harness.WriteSourceFile("notes.txt", "sealed away and brought back");
        _harness.WriteSourceFile("photos/beach.jpg", new string('p', 4_000));
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartWithoutPassphraseAsync(_harness.StateDirectory);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        // The recipient key is published before any ceremony — it is what the
        // admin client seals to (ADR-0042 §4).
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var description);
        Assert.IsNotNull(description.RestoreGrantRecipient);

        // The client-side ceremony: derive from the passphrase, seal the
        // write bundle to the service. The passphrase never crosses.
        var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handler.ExecuteAsync(
                new ProvisionWriteOnlySetCommand(
                    "docs", SealProvision(description.RestoreGrantRecipient, PassphraseText, salt)),
                _timeout.Token),
            out var provisioned);
        Assert.IsTrue(
            provisioned.Lines.Any(line => line.Contains("write-only", StringComparison.Ordinal)),
            "the ceremony's result names what was created");

        // An envelope sealed to some OTHER recipient key is refused by name —
        // and never half-applies.
        var wrongRecipient = ContentSealing.PublicKeyOf(RandomNumberGenerator.GetBytes(32));
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new ProvisionWriteOnlySetCommand(
                    "docs",
                    SealProvision(Convert.ToHexStringLower(wrongRecipient), PassphraseText, salt)),
                _timeout.Token),
            out var wrongService);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, wrongService.Reason);
        Assert.Contains("different service", wrongService.Message, StringComparison.Ordinal);

        // No /keys/ object exists anywhere in the v2 archive (FR-WOR-001).
        Assert.IsFalse(
            Directory.Exists(Path.Combine(_harness.RepositoryPath, "keys"))
            && Directory.EnumerateFileSystemEntries(Path.Combine(_harness.RepositoryPath, "keys")).Any(),
            "a write-only repository stores no key object");

        // Backup, browse and plan — all on a service that holds no passphrase.
        await RunBackupAndWaitAsync(runtime, handler);

        // A records-level sweep is honest about the sealed plane (ADR-0042
        // §7): zero failures, the sealed count reported, and the check names
        // the incapacity as exactly that — never as damage.
        Assert.IsInstanceOfType<VerificationResult>(
            await handler.ExecuteAsync(new VerifyCommand("records"), _timeout.Token), out var verified);
        Assert.AreEqual(0L, verified.Failures, "sealed content must never verify as damage");
        Assert.IsTrue(verified.SealedRecords > 0, "the sealed records are reported, not silently passed");

        Assert.IsInstanceOfType<CheckResult>(
            await handler.ExecuteAsync(new CheckCommand("records"), _timeout.Token), out var checkedUp);
        var incapacity = Assert.ContainsSingle(checkedUp.Findings);
        Assert.Contains("restore grant", incapacity, StringComparison.Ordinal);
        Assert.Contains("Not damage", incapacity, StringComparison.Ordinal);

        Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
            await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs"), _timeout.Token), out var structure);
        var snapshotId = Assert.ContainsSingle(structure.Snapshots).SnapshotId;

        Assert.IsInstanceOfType<DirectoryResult>(
            await handler.ExecuteAsync(
                new ListDirectoryCommand(snapshotId, null, Source: structure.SourceId), _timeout.Token),
            out var top);
        CollectionAssert.AreEquivalent(
            new[] { "notes.txt", "photos" }, top.Entries.Select(entry => entry.Name).ToArray());

        Assert.IsInstanceOfType<RestorePlanResult>(
            await handler.ExecuteAsync(
                new PlanRestoreCommand(snapshotId, null, Source: structure.SourceId), _timeout.Token),
            out var plan);
        Assert.AreEqual(2, plan.Files);

        // A grant-less run is honest degradation, not a crash: every content
        // read reports sealed, the receipt says so, and nothing is written.
        var sealedOut = Path.Combine(_harness.WorkPath, "sealed");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    snapshotId, null, sealedOut, Source: structure.SourceId, InPlace: true),
                _timeout.Token),
            out var refusedRun);
        Assert.AreEqual(0, refusedRun.Restored);
        Assert.IsTrue(refusedRun.Failed > 0, "sealed content must not read without a grant");
        Assert.IsTrue(
            refusedRun.FailedSample!.Any(line => line.Contains("ContentSealed", StringComparison.Ordinal)),
            $"the failure names the sealed content: {string.Join("; ", refusedRun.FailedSample!)}");

        Assert.IsInstanceOfType<AcknowledgedResult>(
            await handler.ExecuteAsync(new CloseRestoreSourceCommand(structure.SourceId), _timeout.Token));

        // The restore ceremony: re-enter the passphrase (client side), derive
        // against the descriptor's recorded salt and parameters, seal the
        // grant. The source opened with it restores byte-identically.
        Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
            await handler.ExecuteAsync(
                new OpenRestoreSourceCommand(
                    "docs",
                    Envelope: await SealGrantAsync(description.RestoreGrantRecipient!, PassphraseText)),
                _timeout.Token),
            out var granted);

        var restoredOut = Path.Combine(_harness.WorkPath, "granted");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    snapshotId, null, restoredOut, Source: granted.SourceId, InPlace: true),
                _timeout.Token),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome);
        Assert.AreEqual(
            "sealed away and brought back", File.ReadAllText(Path.Combine(restoredOut, "notes.txt")));
        Assert.AreEqual(
            new string('p', 4_000), File.ReadAllText(Path.Combine(restoredOut, "photos", "beach.jpg")));

        // Closing the source is what ends the grant (ADR-0042 §5): the next
        // verb against the handle is an expiry, not a lingering capability.
        Assert.IsInstanceOfType<AcknowledgedResult>(
            await handler.ExecuteAsync(new CloseRestoreSourceCommand(granted.SourceId), _timeout.Token));
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new PlanRestoreCommand(snapshotId, null, Source: granted.SourceId), _timeout.Token),
            out var expired);
        Assert.Contains("expired", expired.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WriteOnlySet_MovedToAFreshStateDirectory_NeedsAdoptionAndRefusesTheWrongPassphrase()
    {
        _harness.WriteSourceFile("notes.txt", "survives the migration");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        string snapshotId;
        string recipient;

        // Machine A: provision, back up, and let the fan-out land the
        // replica in the vault — machine B's readable copy.
        await using (var runtime = await StartWithoutPassphraseAsync(_harness.StateDirectory))
        {
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            Assert.IsInstanceOfType<ServiceDescriptionResult>(
                await handler.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var description);

            var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
            Assert.IsInstanceOfType<ConfigurationChangeResult>(
                await handler.ExecuteAsync(
                    new ProvisionWriteOnlySetCommand(
                        "docs", SealProvision(description.RestoreGrantRecipient!, PassphraseText, salt)),
                    _timeout.Token));

            await RunBackupAndWaitAsync(runtime, handler);

            Assert.IsInstanceOfType<SyncResult>(
                await handler.ExecuteAsync(new SyncCommand("docs", null), _timeout.Token));
            while (runtime.DestinationSync.Find(_harness.DocsSetId, "vault")
                is not { State: FallbackPlan.Application.DestinationSyncState.InSync })
            {
                _timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(50, _timeout.Token);
            }

            Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
                await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs"), _timeout.Token), out var opened);
            snapshotId = Assert.ContainsSingle(opened.Snapshots).SnapshotId;
            Assert.IsInstanceOfType<AcknowledgedResult>(
                await handler.ExecuteAsync(new CloseRestoreSourceCommand(opened.SourceId), _timeout.Token));
        }

        // Machine B: the archive moved (same archives root), the state
        // directory — write credential included — did not. Even metadata is
        // unreadable until an adoption, and the refusal says so (ADR-0042
        // §10, decision the plan-rejection feedback made binding).
        var stateB = Path.Combine(_harness.WorkPath, "state-b");
        Directory.CreateDirectory(stateB);
        File.Copy(
            Path.Combine(_harness.StateDirectory, "config.json"),
            Path.Combine(stateB, "config.json"));

        await using (var runtime = await StartWithoutPassphraseAsync(stateB))
        {
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

            var refused = await Assert.ThrowsExactlyAsync<RepositoryOpenException>(async () =>
                await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs"), _timeout.Token));
            Assert.Contains("provision", refused.Message, StringComparison.OrdinalIgnoreCase);

            Assert.IsInstanceOfType<ServiceDescriptionResult>(
                await handler.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var description);
            recipient = description.RestoreGrantRecipient!;

            // The new service instance has its own recipient key — a moved
            // state directory must not mean a portable grant target.
            var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(
                new LocalFileSystemObjectStore(_harness.RepositoryPath), _timeout.Token);

            // The wrong passphrase derives a bundle whose public key does not
            // match the descriptor — refused before anything is stored.
            Assert.IsInstanceOfType<ServiceError>(
                await handler.ExecuteAsync(
                    new ProvisionWriteOnlySetCommand(
                        "docs",
                        SealProvision(
                            recipient, "not the passphrase this archive was born from",
                            descriptor.KdfSalt.ToArray(), descriptor.KdfParameters)),
                    _timeout.Token),
                out var wrongPassphrase);
            Assert.AreEqual(ServiceErrorReason.InvalidArgument, wrongPassphrase.Reason);
            Assert.Contains("does not match", wrongPassphrase.Message, StringComparison.Ordinal);

            // Adoption with the right passphrase, against the descriptor's
            // recorded salt and parameters — then metadata reads again.
            Assert.IsInstanceOfType<ConfigurationChangeResult>(
                await handler.ExecuteAsync(
                    new ProvisionWriteOnlySetCommand(
                        "docs",
                        SealProvision(
                            recipient, PassphraseText,
                            descriptor.KdfSalt.ToArray(), descriptor.KdfParameters)),
                    _timeout.Token),
                out var adopted);
            Assert.IsTrue(
                adopted.Lines.Any(line => line.Contains("adopted", StringComparison.Ordinal)),
                "adoption is named as adoption, not creation");

            // The staging archive opens again — metadata is readable, which
            // is exactly what adoption bought. (The per-archive catalogue is
            // machine-local derived state, so machine B browses through the
            // replica source, which rebuilds its own from the repository.)
            Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
                await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs"), _timeout.Token), out var reopened);
            Assert.IsInstanceOfType<AcknowledgedResult>(
                await handler.ExecuteAsync(new CloseRestoreSourceCommand(reopened.SourceId), _timeout.Token));

            Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
                await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs", "vault"), _timeout.Token),
                out var browsable);
            Assert.AreEqual(snapshotId, Assert.ContainsSingle(browsable.Snapshots).SnapshotId);
            Assert.IsInstanceOfType<AcknowledgedResult>(
                await handler.ExecuteAsync(new CloseRestoreSourceCommand(browsable.SourceId), _timeout.Token));

            // And the granted restore works on machine B — passphrase, not
            // state, is what carries restore capability across machines.
            Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
                await handler.ExecuteAsync(
                    new OpenRestoreSourceCommand(
                        "docs", "vault", Envelope: await SealGrantAsync(recipient, PassphraseText)),
                    _timeout.Token),
                out var granted);
            var restoredOut = Path.Combine(_harness.WorkPath, "machine-b");
            Assert.IsInstanceOfType<RestoreResult>(
                await handler.ExecuteAsync(
                    new RunRestoreCommand(
                        snapshotId, null, restoredOut, Source: granted.SourceId, InPlace: true),
                    _timeout.Token),
                out var restored);
            Assert.AreEqual("complete", restored.Outcome);
            Assert.AreEqual(
                "survives the migration", File.ReadAllText(Path.Combine(restoredOut, "notes.txt")));
        }
    }

    [TestMethod]
    public async Task ProvisionWriteOnlySet_AgainstAnExistingV1Archive_IsRefusedByName()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "a v1 archive");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartWithoutPassphraseAsync(_harness.StateDirectory);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var description);

        var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new ProvisionWriteOnlySetCommand(
                    "docs", SealProvision(description.RestoreGrantRecipient!, PassphraseText, salt)),
                _timeout.Token),
            out var refused);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, refused.Reason);
        Assert.Contains("format 1", refused.Message, StringComparison.Ordinal);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new ProvisionWriteOnlySetCommand(
                    "no-such-set", SealProvision(description.RestoreGrantRecipient!, PassphraseText, salt)),
                _timeout.Token),
            out var unknownSet);
        Assert.AreEqual(ServiceErrorReason.NotFound, unknownSet.Reason);
    }

    [TestMethod]
    public async Task ProvisionWriteOnlySet_MalformedOrCrossPurposeEnvelopes_AreRefusedAndReProvisionIsAdoption()
    {
        _harness.WriteSourceFile("notes.txt", "the envelope gauntlet");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartWithoutPassphraseAsync(_harness.StateDirectory);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var description);

        // Not hex at all: refused by name before anything is opened.
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new ProvisionWriteOnlySetCommand("docs", "zz this is not hex zz"), _timeout.Token),
            out var notHex);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, notHex.Reason);
        Assert.Contains("not hex", notHex.Message, StringComparison.Ordinal);

        // Provision properly, so a real grant envelope exists to replay.
        var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handler.ExecuteAsync(
                new ProvisionWriteOnlySetCommand(
                    "docs", SealProvision(description.RestoreGrantRecipient!, PassphraseText, salt)),
                _timeout.Token));

        // A RESTORE-GRANT envelope replayed as a provision: sealed to the
        // right recipient but under the grant purpose, so the AAD separation
        // refuses it exactly like a foreign envelope — purposes never blur.
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new ProvisionWriteOnlySetCommand(
                    "docs", await SealGrantAsync(description.RestoreGrantRecipient!, PassphraseText)),
                _timeout.Token),
            out var crossPurpose);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, crossPurpose.Reason);
        Assert.Contains("different service", crossPurpose.Message, StringComparison.Ordinal);

        // Re-provisioning the provisioned set with the same passphrase is an
        // idempotent adoption, never a destructive re-creation.
        var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(
            new LocalFileSystemObjectStore(_harness.RepositoryPath), _timeout.Token);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handler.ExecuteAsync(
                new ProvisionWriteOnlySetCommand(
                    "docs",
                    SealProvision(
                        description.RestoreGrantRecipient!, PassphraseText,
                        descriptor.KdfSalt.ToArray(), descriptor.KdfParameters)),
                _timeout.Token),
            out var readopted);
        Assert.IsTrue(
            readopted.Lines.Any(line => line.Contains("adopted", StringComparison.Ordinal)),
            "a re-provision of the same archive is named an adoption");
    }

    [TestMethod]
    public async Task OpenRestoreSource_MalformedWrongServiceOrWrongPassphraseGrants_AreRefusedByName()
    {
        _harness.WriteSourceFile("notes.txt", "granted or refused, never confused");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartWithoutPassphraseAsync(_harness.StateDirectory);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var description);

        var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var provisionEnvelope = SealProvision(description.RestoreGrantRecipient!, PassphraseText, salt);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handler.ExecuteAsync(
                new ProvisionWriteOnlySetCommand("docs", provisionEnvelope), _timeout.Token));
        await RunBackupAndWaitAsync(runtime, handler);

        // Not hex.
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new OpenRestoreSourceCommand("docs", Envelope: "certainly not hex"), _timeout.Token),
            out var notHex);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, notHex.Reason);
        Assert.Contains("not hex", notHex.Message, StringComparison.Ordinal);

        // The PROVISION envelope replayed as a grant: right recipient, wrong
        // purpose — the AAD separation holds in this direction too.
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new OpenRestoreSourceCommand("docs", Envelope: provisionEnvelope), _timeout.Token),
            out var crossPurpose);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, crossPurpose.Reason);
        Assert.Contains("different service", crossPurpose.Message, StringComparison.Ordinal);

        // A grant derived from the WRONG passphrase: it opens (it was sealed
        // to this service), but the scalar does not reproduce the
        // repository's sealing key — a wrong passphrase at the client,
        // refused by name, never a damage claim.
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new OpenRestoreSourceCommand(
                    "docs",
                    Envelope: await SealGrantFromWrongPassphraseAsync(description.RestoreGrantRecipient!)),
                _timeout.Token),
            out var wrongPassphrase);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, wrongPassphrase.Reason);
        Assert.Contains("not this repository's", wrongPassphrase.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task OpenRestoreSource_AGrantOnAV1Source_IsIgnoredAndTheRestoreStillWorks()
    {
        // A v1 archive with a service that holds its passphrase — the world
        // the grant machinery must leave completely alone.
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "v1 ignores grants");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartWithServicePassphraseAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);

        // The contract says a grant on a v1 source is ignored — even one
        // that is not hex — because there is nothing for it to mean.
        Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
            await handler.ExecuteAsync(
                new OpenRestoreSourceCommand("docs", Envelope: "zz ignored on v1 zz"), _timeout.Token),
            out var opened);
        var snapshotId = Assert.ContainsSingle(opened.Snapshots).SnapshotId;

        var restoredOut = Path.Combine(_harness.WorkPath, "v1-ignored");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    snapshotId, null, restoredOut, Source: opened.SourceId, InPlace: true),
                _timeout.Token),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome);
        Assert.AreEqual("v1 ignores grants", File.ReadAllText(Path.Combine(restoredOut, "notes.txt")));
    }

    [TestMethod]
    public async Task OpenRestoreSource_ACorruptStoredWriteCredential_RefusesByNamingAdoption()
    {
        _harness.WriteSourceFile("notes.txt", "the credential rots");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using (var runtime = await StartWithoutPassphraseAsync(_harness.StateDirectory))
        {
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            Assert.IsInstanceOfType<ServiceDescriptionResult>(
                await handler.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var description);

            var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
            Assert.IsInstanceOfType<ConfigurationChangeResult>(
                await handler.ExecuteAsync(
                    new ProvisionWriteOnlySetCommand(
                        "docs", SealProvision(description.RestoreGrantRecipient!, PassphraseText, salt)),
                    _timeout.Token));
            await RunBackupAndWaitAsync(runtime, handler);
        }

        // Rot the stored credential in place: still a file, no longer a
        // credential. The next service life must name it as damage — the
        // remedy is adoption — never a silent "not provisioned".
        var credentialPath = Path.Combine(
            _harness.StateDirectory, "write-credentials", $"{_harness.DocsSetId}.bin");
        var bytes = await File.ReadAllBytesAsync(credentialPath, _timeout.Token);
        bytes[0] ^= 0x01;
        await File.WriteAllBytesAsync(credentialPath, bytes, _timeout.Token);

        await using (var runtime = await StartWithoutPassphraseAsync(_harness.StateDirectory))
        {
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            var refused = await Assert.ThrowsExactlyAsync<RepositoryOpenException>(async () =>
                await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs"), _timeout.Token));
            Assert.Contains("adopt", refused.Message, StringComparison.Ordinal);
            Assert.Contains(_harness.DocsSetId, refused.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The client half of the provisioning ceremony: Argon2id where the
    /// person typed, the write bundle sealed to the service's published
    /// recipient key, rendered as the contract's hex string.
    /// </summary>
    private static string SealProvision(
        string recipientHex, string passphraseText, byte[] salt, Argon2Parameters? parameters = null)
    {
        var kdfParameters = parameters ?? RepositoryCreationSettings.Default.KdfParameters;
        using var passphrase = Passphrase.Create(passphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, kdfParameters, salt, KdfValidationMode.OpenRepository);
        return Convert.ToHexStringLower(
            WriteOnlyProvisioning.SealProvision(
                Convert.FromHexString(recipientHex), authority, salt, kdfParameters));
    }

    /// <summary>
    /// The client half of the restore ceremony: re-derive against the
    /// descriptor's recorded salt and parameters, prove by public-key
    /// equality, seal the scalar alone.
    /// </summary>
    private async Task<string> SealGrantAsync(string recipientHex, string passphraseText)
    {
        var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(
            new LocalFileSystemObjectStore(_harness.RepositoryPath), _timeout.Token);
        using var passphrase = Passphrase.Create(passphraseText);
        Assert.IsTrue(
            RepositoryLifecycle.TryDeriveReadAuthority(descriptor, passphrase, out var authority),
            "the drill's passphrase reproduces the repository's keys");
        using (authority)
        {
            return Convert.ToHexStringLower(
                WriteOnlyProvisioning.SealGrant(
                    Convert.FromHexString(recipientHex), authority!.SealingPrivateKey));
        }
    }

    /// <summary>
    /// A grant sealed correctly to this service — but derived from a
    /// passphrase that is not this repository's, against the descriptor's
    /// own salt and parameters so only the passphrase is wrong.
    /// </summary>
    private async Task<string> SealGrantFromWrongPassphraseAsync(string recipientHex)
    {
        var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(
            new LocalFileSystemObjectStore(_harness.RepositoryPath), _timeout.Token);
        using var passphrase = Passphrase.Create("emphatically not the passphrase of this drill");
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, descriptor.KdfParameters, descriptor.KdfSalt.ToArray(), KdfValidationMode.OpenRepository);
        return Convert.ToHexStringLower(
            WriteOnlyProvisioning.SealGrant(
                Convert.FromHexString(recipientHex), authority.SealingPrivateKey));
    }

    private async Task<ServiceRuntime> StartWithServicePassphraseAsync()
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            passphrase,
            _timeout.Token);
    }

    private async Task RunBackupAndWaitAsync(ServiceRuntime runtime, ServiceCommandHandler handler)
    {
        var progress = runtime.Progress.WatchAsync(_timeout.Token);
        var watching = Task.Run(
            async () =>
            {
                await foreach (var observation in progress)
                {
                    if (observation.Progress.State is JobState.Complete or JobState.CompletedWithFailures)
                    {
                        return observation.Progress.State;
                    }
                }

                return JobState.Pending;
            },
            _timeout.Token);

        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand("docs", Full: false), _timeout.Token));
        Assert.AreEqual(JobState.Complete, await watching, "the write-only backup completes");
    }

    private async Task<ServiceRuntime> StartWithoutPassphraseAsync(string stateDirectory) =>
        await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = stateDirectory,
            },
            passphrase: null,
            _timeout.Token);
}
