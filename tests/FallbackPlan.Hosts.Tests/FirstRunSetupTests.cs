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
/// The first-run setup verb over the service (contract 1.13, ADR-0044;
/// FR-SVC-011, NFR-SEC-011): a service with no passphrase says so, one
/// sealed envelope gives the installation its root, a second is refused
/// because there is no changing it, and a remote caller may not run the
/// ceremony at all.
/// </summary>
[TestClass]
public sealed class FirstRunSetupTests : IDisposable
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
    public async Task Describe_AFreshService_AsksToBeSetUpOnTheFirstQuestion()
    {
        // The whole point of the state: the refusal an unprovisioned set
        // eventually raises arrives on a scheduler poll, hours after anybody
        // could act on it. This arrives on the client's first question.
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartWithoutPassphraseAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.AreEqual("setup_required", (await DescribeAsync(handler)).SetupState);

        await SetUpAsync(handler);

        // Not `ready` — the ceremony still owes a saved recovery kit
        // (FR-KIT-004). Reaching `ready` is the next test's business.
        Assert.AreEqual("kit_required", (await DescribeAsync(handler)).SetupState);
    }

    [TestMethod]
    public async Task Setup_AFreshService_SaysWhatTheOperatorHasJustCommittedTo()
    {
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartWithoutPassphraseAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var result = await SetUpAsync(handler);

        // Three statements, and the third is the one that matters: a ceremony
        // that captures an unchangeable master key without saying so has
        // taken a decision on the operator's behalf.
        Assert.IsTrue(
            result.Lines.Any(line => line.Contains("never be changed", StringComparison.Ordinal)),
            "the ceremony states that the passphrase cannot change");
        Assert.IsTrue(
            result.Lines.Any(line => line.Contains("unrecoverable", StringComparison.Ordinal)),
            "the ceremony states the consequence of losing it");
        Assert.IsTrue(
            result.Lines.Any(line => line.Contains("never read your files back", StringComparison.Ordinal)),
            "the ceremony states what the service can and cannot do afterwards");
    }

    [TestMethod]
    public async Task Setup_RunTwice_IsRefusedRatherThanObeyed()
    {
        // A second envelope carries a second salt. Accepting it would leave
        // every archive already written unopenable by the passphrase that
        // made it — an "idempotent" verb that destroys the backup.
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartWithoutPassphraseAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        await SetUpAsync(handler);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new ProvisionInstallationCommand(await EnvelopeAsync(handler)), _timeout.Token),
            out var refusal);
        Assert.AreEqual(ServiceErrorReason.Refused, refusal.Reason);
        Assert.Contains("already set up", refusal.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Setup_FromARemoteCaller_IsRefusedAndNamesWhereItRuns()
    {
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartWithoutPassphraseAsync();
        var local = new ServiceCommandHandler(runtime, RemoteBindingState.Off, CallerScope.Local);
        var remote = new ServiceCommandHandler(runtime, RemoteBindingState.On("127.0.0.1:9"), CallerScope.Remote);

        var envelope = await EnvelopeAsync(local);

        Assert.IsInstanceOfType<ServiceError>(
            await remote.ExecuteAsync(new ProvisionInstallationCommand(envelope), _timeout.Token),
            out var refusal);
        Assert.AreEqual(ServiceErrorReason.Refused, refusal.Reason);
        Assert.Contains("own machine", refusal.Message, StringComparison.Ordinal);

        // Refused, not merely reported: nothing was stored, so the local
        // console can still run the ceremony afterwards.
        Assert.AreEqual("setup_required", (await DescribeAsync(local)).SetupState);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await local.ExecuteAsync(new ProvisionInstallationCommand(envelope), _timeout.Token));
    }

    [TestMethod]
    public async Task Setup_ANonHexEnvelope_IsRefusedByName()
    {
        await using var runtime = await StartWithoutPassphraseAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new ProvisionInstallationCommand("not hex at all"), _timeout.Token),
            out var refusal);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, refusal.Reason);
        Assert.Contains("not hex", refusal.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Setup_AnEnvelopeSealedToAnotherService_IsRefusedAndChangesNothing()
    {
        await using var runtime = await StartWithoutPassphraseAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var stranger = ContentSealing.PublicKeyOf(RandomNumberGenerator.GetBytes(32));
        var envelope = SealProvision(Convert.ToHexStringLower(stranger), PassphraseText,
            RandomNumberGenerator.GetBytes(KekDerivation.SaltLength));

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new ProvisionInstallationCommand(envelope), _timeout.Token),
            out var refusal);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, refusal.Reason);
        Assert.Contains("different service", refusal.Message, StringComparison.Ordinal);
        Assert.AreEqual("setup_required", (await DescribeAsync(handler)).SetupState);
    }

    [TestMethod]
    public async Task Setup_ThenABackup_RunsWithNoPassphraseAnywhereAndSealsToTheChosenOne()
    {
        // The reported failure, run forwards: a service started with no
        // passphrase used to refuse this set on every poll. After setup it
        // backs it up, and the passphrase alone reproduces the archive's
        // sealing key — which is what a restore will have to do.
        _harness.WriteSourceFile("notes.txt", "protected from the first minute");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartWithoutPassphraseAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await SetUpAsync(handler);

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
        Assert.AreEqual(JobState.Complete, await watching);

        // Deliberately backed up while the kit is still unconfirmed. The
        // ceremony is incomplete and the console says so, but data
        // protection does not wait on a paperwork step — stopping backups
        // over an unsaved kit would lose data to enforce a habit.
        Assert.AreEqual("kit_required", (await DescribeAsync(handler)).SetupState);

        var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(
            new LocalFileSystemObjectStore(_harness.RepositoryPath), _timeout.Token);
        Assert.IsTrue(RepositoryLifecycle.IsWriteOnly(descriptor));

        using var passphrase = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, descriptor.KdfParameters, descriptor.KdfSalt.Span, KdfValidationMode.OpenRepository);
        SequenceAssert.AreEqual(
            descriptor.SealingPublicKey.ToArray(), authority.Credential.SealingPublicKey.ToArray());
    }

    [TestMethod]
    public async Task SetupVerb_WithoutTheAcknowledgement_RefusesAndSaysWhatIsAtStake()
    {
        // The acknowledgement is the ceremony, not a speed bump: there is no
        // recovery path to offer afterwards.
        Environment.SetEnvironmentVariable(_harness.PassphraseVariable, PassphraseText);
        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "setup", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable);

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("unrecoverable", result.All, StringComparison.Ordinal);
        Assert.Contains("--acknowledge-loss", result.All, StringComparison.Ordinal);
        Assert.IsFalse(new InstallationCredentialStore(_harness.StateDirectory).Holds);
    }

    [TestMethod]
    public async Task SetupVerb_WithoutANamedVariable_RefusesRatherThanTakingItFromTheCommandLine()
    {
        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "setup", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--acknowledge-loss");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("never on the command line", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SetupVerb_AWeakPassphrase_IsRefusedBeforeAnythingIsStored()
    {
        Environment.SetEnvironmentVariable(_harness.PassphraseVariable, "abcabcabcabc");
        try
        {
            var result = await HostHarness.RunAsync(
                AgentHost.RunAsync,
                "setup", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
                "--passphrase-env", _harness.PassphraseVariable, "--acknowledge-loss");

            Assert.AreEqual(1, result.ExitCode);
            Assert.Contains("too weak", result.All, StringComparison.Ordinal);
            Assert.IsFalse(new InstallationCredentialStore(_harness.StateDirectory).Holds);
        }
        finally
        {
            Environment.SetEnvironmentVariable(_harness.PassphraseVariable, PassphraseText);
        }
    }

    [TestMethod]
    public async Task SetupVerb_Acknowledged_SetsUpTheInstallationAndRefusesASecondRun()
    {
        Environment.SetEnvironmentVariable(_harness.PassphraseVariable, PassphraseText);

        var first = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "setup", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable, "--acknowledge-loss");

        Assert.AreEqual(0, first.ExitCode, first.All);
        Assert.Contains("never be changed", first.All, StringComparison.Ordinal);
        Assert.IsTrue(new InstallationCredentialStore(_harness.StateDirectory).Holds);

        // The headless path enforces once-ness the same way the console does,
        // because it is the same handler behind both.
        var second = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "setup", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable, "--acknowledge-loss");

        Assert.AreEqual(2, second.ExitCode);
        Assert.Contains("already set up", second.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Setup_AfterThePassphrase_WaitsForTheKitRatherThanReportingReady()
    {
        // The middle state is what makes the confirmation real. Without it,
        // a closed tab between provisioning and confirming would leave an
        // installation that looks finished and has no kit.
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartWithoutPassphraseAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.AreEqual("setup_required", (await DescribeAsync(handler)).SetupState);

        await SetUpAsync(handler);
        Assert.AreEqual("kit_required", (await DescribeAsync(handler)).SetupState);

        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handler.ExecuteAsync(new ConfirmRecoveryKitCommand(new string('a', 64)), _timeout.Token),
            out var confirmed);
        Assert.AreEqual("ready", (await DescribeAsync(handler)).SetupState);

        Assert.IsTrue(
            confirmed.Lines.Any(line => line.Contains("Keep them apart", StringComparison.Ordinal)),
            "the confirmation says why two factors kept together are one factor");
    }

    [TestMethod]
    public async Task Setup_TheConfirmationSurvivesARestart_AndTheServiceKeepsOnlyTheChecksum()
    {
        _harness.WriteConfiguration("every 1h");
        const string Checksum = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        await using (var runtime = await StartWithoutPassphraseAsync())
        {
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            await SetUpAsync(handler);
            Assert.IsInstanceOfType<ConfigurationChangeResult>(
                await handler.ExecuteAsync(new ConfirmRecoveryKitCommand(Checksum), _timeout.Token));
        }

        await using (var restarted = await StartWithoutPassphraseAsync())
        {
            Assert.AreEqual("ready", restarted.SetupState);
        }

        // The checksum and nothing else: a service holding a copy of the kit
        // would have made itself a second factor.
        var path = Path.Combine(_harness.StateDirectory, "recovery-kit.confirmed");
        var text = await File.ReadAllTextAsync(path, _timeout.Token);
        Assert.Contains(Checksum, text, StringComparison.Ordinal);
        Assert.IsTrue(
            new FileInfo(path).Length < 512,
            "the confirmation records a checksum, never a kit");
    }

    [TestMethod]
    public async Task ConfirmKit_BeforeSetup_IsRefusedAsOutOfOrder()
    {
        await using var runtime = await StartWithoutPassphraseAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new ConfirmRecoveryKitCommand(new string('a', 64)), _timeout.Token),
            out var refusal);
        Assert.AreEqual(ServiceErrorReason.Refused, refusal.Reason);
        Assert.Contains("no passphrase yet", refusal.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ConfirmKit_SomethingThatIsNotAChecksum_IsRefused()
    {
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartWithoutPassphraseAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await SetUpAsync(handler);

        foreach (var candidate in new[] { "", "not-a-checksum", new string('a', 63), new string('z', 64) })
        {
            Assert.IsInstanceOfType<ServiceError>(
                await handler.ExecuteAsync(new ConfirmRecoveryKitCommand(candidate), _timeout.Token),
                out var refusal);
            Assert.AreEqual(ServiceErrorReason.InvalidArgument, refusal.Reason, candidate);
        }

        Assert.AreEqual("kit_required", (await DescribeAsync(handler)).SetupState);
    }

    [TestMethod]
    public async Task ConfirmKit_FromARemoteCaller_IsRefused()
    {
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartWithoutPassphraseAsync();
        var local = new ServiceCommandHandler(runtime, RemoteBindingState.Off, CallerScope.Local);
        var remote = new ServiceCommandHandler(runtime, RemoteBindingState.On("127.0.0.1:9"), CallerScope.Remote);
        await SetUpAsync(local);

        Assert.IsInstanceOfType<ServiceError>(
            await remote.ExecuteAsync(new ConfirmRecoveryKitCommand(new string('a', 64)), _timeout.Token),
            out var refusal);
        Assert.AreEqual(ServiceErrorReason.Refused, refusal.Reason);
        Assert.AreEqual("kit_required", (await DescribeAsync(local)).SetupState);
    }

    [TestMethod]
    public async Task Describe_PublishesThisDevicesPublicIdentity_ForTheKitToRecord()
    {
        await using var runtime = await StartWithoutPassphraseAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var description = await DescribeAsync(handler);

        Assert.IsNotNull(description.DeviceId);
        Assert.HasCount(32, description.DeviceId!);
        SequenceAssert.AreEqual(runtime.State.DeviceId, Convert.FromHexString(description.DeviceId!));
    }

    private async Task<ServiceDescriptionResult> DescribeAsync(ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var description);
        return description;
    }

    private async Task<ConfigurationChangeResult> SetUpAsync(ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handler.ExecuteAsync(new ProvisionInstallationCommand(await EnvelopeAsync(handler)), _timeout.Token),
            out var result);
        return result;
    }

    /// <summary>Seals a setup envelope to this service, as the console would.</summary>
    private async Task<string> EnvelopeAsync(ServiceCommandHandler handler) =>
        SealProvision(
            (await DescribeAsync(handler)).RestoreGrantRecipient!, PassphraseText,
            RandomNumberGenerator.GetBytes(KekDerivation.SaltLength));

    private static string SealProvision(string recipientHex, string passphraseText, byte[] salt)
    {
        var parameters = RepositoryCreationSettings.Default.KdfParameters;
        using var passphrase = Passphrase.Create(passphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, parameters, salt, KdfValidationMode.CreateRepository);
        return Convert.ToHexStringLower(
            WriteOnlyProvisioning.SealProvision(
                Convert.FromHexString(recipientHex), authority, salt, parameters));
    }

    private async Task<ServiceRuntime> StartWithoutPassphraseAsync() =>
        await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            passphrase: null,
            _timeout.Token);
}
