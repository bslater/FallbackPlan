using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Cli;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// <c>restore-resume</c> resumes a restore cut short by its name alone
/// (FR-RST-008, ADR-0115). Without a run it lists the runs the service began
/// and has not seen finish, naming none of the backup's files, so it asks no
/// passphrase. With one it resumes that run as it was asked, through a
/// source of its snapshot the passphrase unlocked, and says what the restore
/// says. A run let go is named no more. A run not left unfinished is refused
/// before the passphrase is asked, and in direct mode no run is named at all.
/// With <c>--plan</c> it plans the run's resume and resumes nothing
/// (ADR-0120), naming the run as a resume or a letting go does, and not
/// beside one. A run listed says what the parts it kept hold (ADR-0133).
/// </summary>
[TestClass]
public sealed class RestoreResumeVerbTests : IDisposable
{
    private const string ThePassphrase = "the scripted installation's passphrase";
    private const string Run = "0123456789abcdef";

    private static readonly string SnapshotId = new('e', 32);

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    [TestMethod]
    public async Task WithNoRun_TheRunsLeftUnfinishedAreListed_AndNoPassphraseIsAsked()
    {
        await using var client = new ScriptedClient(
            [new RestoreRunDescriptor(Run, SnapshotId, "docs", "/srv/restored", 1_793_097_600_000, Existing: "rename", InPlace: true)]);

        var report = await ThroughAsync(client, (gateway, token) => gateway.RestoreRunsAsync(token));

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        var line = Assert.ContainsSingle(printed => printed.Contains(Run, StringComparison.Ordinal), report.Lines);
        Assert.Contains(SnapshotId, line, StringComparison.Ordinal);
        Assert.Contains("'docs'", line, StringComparison.Ordinal);
        Assert.Contains("/srv/restored", line, StringComparison.Ordinal);
        Assert.Contains("in place", line, StringComparison.Ordinal);
        Assert.Contains("2026-10-27", line, StringComparison.Ordinal);
        Assert.IsTrue(report.Lines.Any(printed => printed.Contains("restore-resume <run>", StringComparison.Ordinal)), string.Join(" | ", report.Lines));
        Assert.IsEmpty(client.Received.OfType<OpenRestoreSourceCommand>(), "a list of runs names none of the backup's files");
    }

    [TestMethod]
    public async Task ARunListed_SaysWhatItsPartsHold_AndOneHoldingNothingSaysNothing()
    {
        const string Other = "fedcba9876543210";
        await using var client = new ScriptedClient(
        [
            new RestoreRunDescriptor(Run, SnapshotId, "docs", "/srv/restored", 1_000, Held: 2_097_152),
            new RestoreRunDescriptor(Other, SnapshotId, "docs", "/srv/restored", 2_000),
        ]);

        var report = await ThroughAsync(client, (gateway, token) => gateway.RestoreRunsAsync(token));

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        Assert.Contains(
            "holding 2097152 bytes (2.0 MiB) it had assembled",
            Assert.ContainsSingle(printed => printed.Contains(Run, StringComparison.Ordinal), report.Lines),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "holding",
            Assert.ContainsSingle(printed => printed.Contains(Other, StringComparison.Ordinal), report.Lines),
            StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WithNoneLeft_SaysSo()
    {
        await using var client = new ScriptedClient([]);

        var report = await ThroughAsync(client, (gateway, token) => gateway.RestoreRunsAsync(token));

        Assert.IsTrue(report.Ok);
        Assert.Contains("No restore run is left unfinished", Assert.ContainsSingle(report.Lines), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ARunNamed_IsResumedThroughASourceOfItsSnapshot_AndSaysWhatTheRestoreSays()
    {
        await using var client = new ScriptedClient(
            [new RestoreRunDescriptor(Run, SnapshotId, "docs", "/srv/restored", 1_000)],
            new RestoreResult(2, 0, $"/srv/restored/.fbp-quarantine/{Run}", "complete", FoundInPlace: 1, Run: Run));

        var report = await ThroughAsync(client, (gateway, token) => gateway.ResumeRestoreAsync(Run, ignoreFreeSpace: true, token));

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        var asked = Assert.ContainsSingle(client.Received.OfType<ResumeRestoreCommand>());
        Assert.AreEqual(Run, asked.Run);
        Assert.AreEqual(ScriptedClient.SourceId, asked.Source, "the resume reads through the source the passphrase unlocked");
        Assert.IsTrue(asked.IgnoreFreeSpace);
        Assert.IsTrue(report.Lines.Any(printed => printed.Contains("restored 2 file(s)", StringComparison.Ordinal)), string.Join(" | ", report.Lines));
        Assert.IsTrue(report.Lines.Any(printed => printed.Contains("1 file(s) already in place", StringComparison.Ordinal)), string.Join(" | ", report.Lines));
        Assert.ContainsSingle(client.Received.OfType<CloseRestoreSourceCommand>());
        Assert.IsEmpty(client.Received.OfType<RunRestoreCommand>(), "the run is the service's to ask again, as it was asked");
    }

    [TestMethod]
    public async Task ARunResumedThatDoesNotFinishAgain_IsNotOk_AndSaysHowToResumeIt()
    {
        await using var client = new ScriptedClient(
            [new RestoreRunDescriptor(Run, SnapshotId, "docs", "/srv/restored", 1_000)],
            new RestoreResult(1, 1, $"/srv/restored/.fbp-quarantine/{Run}", "failed", Run: Run));

        var report = await ThroughAsync(client, (gateway, token) => gateway.ResumeRestoreAsync(Run, ignoreFreeSpace: false, token));

        Assert.IsFalse(report.Ok);
        Assert.IsTrue(report.Lines.Any(printed => printed.Contains($"restore-resume {Run}", StringComparison.Ordinal)), string.Join(" | ", report.Lines));
    }

    [TestMethod]
    public async Task ARunNotLeftUnfinished_IsRefused_BeforeThePassphraseIsAsked()
    {
        await using var client = new ScriptedClient([]);

        var refused = await Assert.ThrowsExactlyAsync<CliFailureException>(
            () => ThroughAsync(client, (gateway, token) => gateway.ResumeRestoreAsync(Run, ignoreFreeSpace: false, token)));

        Assert.Contains(Run, refused.Message, StringComparison.Ordinal);
        Assert.IsEmpty(client.Received.OfType<OpenRestoreSourceCommand>());
        Assert.IsEmpty(client.Received.OfType<ResumeRestoreCommand>());
    }

    [TestMethod]
    public async Task ARunLetGo_IsForgotten_AndNoPassphraseIsAsked()
    {
        await using var client = new ScriptedClient([new RestoreRunDescriptor(Run, SnapshotId, "docs", "/srv/restored", 1_000)]);

        var report = await ThroughAsync(client, (gateway, token) => gateway.ForgetRestoreRunAsync(Run, token));

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        Assert.AreEqual(Run, Assert.ContainsSingle(client.Received.OfType<ForgetRestoreRunCommand>()).Run);
        Assert.Contains(Run, Assert.ContainsSingle(report.Lines), StringComparison.Ordinal);
        Assert.IsEmpty(client.Received.OfType<OpenRestoreSourceCommand>());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("--forget")]
    [DataRow("--plan")]
    public async Task InDirectMode_NoRunIsNamed_AndNothingIsAsked(string option)
    {
        using var cli = new CliHarness();
        await cli.InitAsync();

        string[] arguments = option.Length == 0 ? ["restore-resume", Run] : ["restore-resume", Run, option];
        var refused = await cli.RunAsync(arguments);

        Assert.AreNotEqual(0, refused.ExitCode, refused.All);
        Assert.Contains("restore --resume", refused.All, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("--plan", "restore-resume <run> --plan")]
    [DataRow("--forget", "restore-resume <run> --forget")]
    public async Task APlanOrALettingGo_WithNoRun_IsRefused_NamingHowToAsk(string option, string how)
    {
        using var cli = new CliHarness();
        await cli.InitAsync();

        var refused = await cli.RunAsync("restore-resume", option);

        Assert.AreNotEqual(0, refused.ExitCode, refused.All);
        Assert.Contains(how, refused.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task APlanBesideALettingGo_IsRefused()
    {
        using var cli = new CliHarness();
        await cli.InitAsync();

        var refused = await cli.RunAsync("restore-resume", Run, "--forget", "--plan");

        Assert.AreNotEqual(0, refused.ExitCode, refused.All);
        Assert.Contains("one or the other", refused.All, StringComparison.Ordinal);
    }

    public void Dispose() => _timeout.Dispose();

    private async Task<OperationReport> ThroughAsync(
        ScriptedClient client, Func<ServiceGateway, CancellationToken, ValueTask<OperationReport>> verb)
    {
        // A variable of this test's own, so no concurrent class reads it.
        var variable = "FBP_TEST_RESUME_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(variable, ThePassphrase);
        try
        {
            var gateway = new ServiceGateway(client, "scripted", client, variable);
            return await verb(gateway, _timeout.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// Answers as a set-up installation with one set (ADR-0042 §7): it lists
    /// <paramref name="runs"/> as left unfinished, opens a source holding
    /// <see cref="SnapshotId"/> only for a grant that proves the passphrase,
    /// and answers a resume through that source with <paramref name="resumed"/>.
    /// </summary>
    private sealed class ScriptedClient(IReadOnlyList<RestoreRunDescriptor> runs, RestoreResult? resumed = null)
        : IFallbackPlanClient
    {
        public const string SourceId = "scripted-source";

        private static readonly string SetId = new('a', 32);

        private static readonly byte[] RecipientScalar = RandomNumberGenerator.GetBytes(32);

        // The least cost Argon2id takes, which an opening derivation accepts
        // as a published fact, so the derivation stays quick.
        private static readonly Argon2Parameters Cost = new() { MemoryKiB = 64, Iterations = 1, Parallelism = 1 };

        private static readonly byte[] Salt = Enumerable.Repeat((byte)0x5A, KekDerivation.SaltLength).ToArray();

        private static readonly string SealingPublicKey = DeriveSealingPublicKey();

        public ContractVersion ServiceContractVersion => ContractVersion.Current;

        /// <summary>Every command asked of it, in order.</summary>
        public List<ServiceCommand> Received { get; } = [];

        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken)
        {
            Received.Add(command);
            return ValueTask.FromResult<ServiceResult>(command switch
            {
                DescribeServiceCommand => new ServiceDescriptionResult(
                    ContractVersion.Current.ToString(), "test", "machine", "/state", false, 0,
                    RestoreGrantRecipient: Convert.ToHexStringLower(ContentSealing.PublicKeyOf(RecipientScalar)),
                    KdfSalt: Convert.ToHexStringLower(Salt),
                    KdfMemoryKib: Cost.MemoryKiB,
                    KdfIterations: Cost.Iterations,
                    KdfParallelism: Cost.Parallelism,
                    SealingPublicKey: SealingPublicKey),
                ListBackupSetsCommand => new BackupSetsResult(
                    [new BackupSetDescriptor(SetId, "docs", "/docs", null, [], [], [])]),
                OpenRestoreSourceCommand { Envelope: { } envelope } when Proves(envelope) =>
                    new RestoreSourceOpenedResult(
                        SourceId, "docs", "/archives/docs", [new SnapshotDescriptor(SnapshotId, SetId, 0, 0, 2)], []),
                OpenRestoreSourceCommand => new ServiceError(ServiceErrorReason.Refused, "no grant proves the passphrase"),
                ListRestoreRunsCommand => new RestoreRunsResult(runs),
                ResumeRestoreCommand { Source: SourceId } when resumed is not null => resumed,
                ResumeRestoreCommand => new ServiceError(ServiceErrorReason.Refused, "a resume needs an unlocked source"),
                ForgetRestoreRunCommand forget when runs.Any(run => run.Run == forget.Run) => new AcknowledgedResult(),
                ForgetRestoreRunCommand => new ServiceError(ServiceErrorReason.NotFound, "no such run"),
                _ => new AcknowledgedResult(),
            });
        }

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<JobProgressEvent>();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static string DeriveSealingPublicKey()
        {
            using var passphrase = Passphrase.Create(ThePassphrase);
            using var authority = WriteOnlyDerivation.Derive(passphrase, Cost, Salt, KdfValidationMode.OpenRepository);
            return Convert.ToHexStringLower(authority.Credential.SealingPublicKey);
        }

        private static bool Proves(string envelopeHex)
        {
            var scalar = WriteOnlyProvisioning.OpenGrant(RecipientScalar, Convert.FromHexString(envelopeHex));
            try
            {
                return Convert.ToHexStringLower(ContentSealing.PublicKeyOf(scalar)) == SealingPublicKey;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(scalar);
            }
        }
    }
}
