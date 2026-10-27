using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Cli;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// What <c>restore</c> prints when the service read some files around damage
/// (FR-RST-007, contract 1.45): the count and the service's own lines saying
/// which files came from which copy, and around what — so a person restoring
/// learns that a copy of their backup is damaged from the restore that met
/// it, not only from a notice they may never open. And what
/// <c>restore --plan</c> prints through the service (FR-RST-003, ADR-0107):
/// the plan for the run's own shape, with nothing run.
/// </summary>
[TestClass]
public sealed class GatewayRestoreReportTests
{
    private const string ThePassphrase = "the scripted installation's passphrase";

    private static readonly string SnapshotId = new('e', 32);

    [TestMethod]
    public async Task ARestoreThatReadAroundDamage_SaysSo_AndNamesTheCopies()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        const string line = "docs/report.txt — read from destination 'spare', around destination 'vault': "
            + "Record 1234 in blob 5678 failed authentication (specification 04 §7).";
        await using var client = new ScriptedClient(new RestoreResult(
            2, 0, "/out/.fbp-quarantine/run", "complete", ReadAround: 1, ReadAroundSample: [line]));

        var report = await RestoreAsync(client, timeout.Token);

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        Assert.IsTrue(
            report.Lines.Any(printed => printed.Contains("1 file(s)", StringComparison.Ordinal)
                && printed.Contains("another copy", StringComparison.Ordinal)),
            string.Join(" | ", report.Lines));
        Assert.IsTrue(report.Lines.Any(printed => printed.Contains(line, StringComparison.Ordinal)), string.Join(" | ", report.Lines));
    }

    [TestMethod]
    public async Task ARestoreThatReadAroundNothing_SaysNothingAboutIt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(new RestoreResult(2, 0, "/out/.fbp-quarantine/run", "complete"));

        var report = await RestoreAsync(client, timeout.Token);

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        Assert.IsFalse(report.Lines.Any(printed => printed.Contains("another copy", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task APlan_AsksForTheRunsOwnShape_AndRunsNothing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(
            new RestoreResult(2, 0, "/out", "complete"),
            new RestorePlanResult(2, 9_000, [], WriteBytes: 4_096, ReadBytes: 3_000));

        var report = await PlanAsync(client, timeout.Token);

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        Assert.Contains("2 file(s)", report.Lines[0], StringComparison.Ordinal);
        var asked = Assert.ContainsSingle(client.Received.OfType<PlanRestoreCommand>());
        Assert.AreEqual("/out", asked.OutputDirectory, "a plan measures where the run would write");
        Assert.IsFalse(asked.InPlace, "the run it plans lands under quarantine, as restore's own does");
        Assert.IsEmpty(client.Received.OfType<RunRestoreCommand>(), "a plan restores nothing");
    }

    [TestMethod]
    public async Task APlanExported_KeepsWhatTheServiceAnswered()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(
            new RestoreResult(2, 0, "/out", "complete"),
            new RestorePlanResult(2, 9_000, [], ArchivedObjects: ["docs/cold.txt"], ReadBytes: 3_000));
        var file = Path.Combine(Path.GetTempPath(), $"fbp-plan-{Guid.NewGuid():N}.json");
        try
        {
            var report = await ThroughAsync(
                client,
                (gateway, token) => gateway.PlanRestoreAsync(new RestoreRequest(SnapshotId, "docs", "/out", PlanExport: file), token),
                timeout.Token);

            Assert.IsTrue(report.Lines.Any(line => line.Contains(file, StringComparison.Ordinal)), string.Join(" | ", report.Lines));
            using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(file, timeout.Token));
            var plan = document.RootElement.GetProperty("plan");
            Assert.AreEqual("docs/cold.txt", plan.GetProperty("archived_objects")[0].GetString());
            Assert.AreEqual(3_000, plan.GetProperty("read_bytes").GetInt64());
            Assert.AreEqual("docs", document.RootElement.GetProperty("path").GetString());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public async Task APlanNamingFilesHeldOnlyInAnArchivalTier_IsNotOk_AndNamesThem()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(
            new RestoreResult(2, 0, "/out", "complete"),
            new RestorePlanResult(2, 9_000, [], ArchivedObjects: ["docs/cold.txt"]));

        var report = await PlanAsync(client, timeout.Token);

        Assert.IsFalse(report.Ok, "a run would fail what the store will not serve");
        Assert.IsTrue(report.Lines.Any(line => line.Contains("archival tier", StringComparison.Ordinal)), string.Join(" | ", report.Lines));
        Assert.IsTrue(report.Lines.Any(line => line.Contains("docs/cold.txt", StringComparison.Ordinal)), string.Join(" | ", report.Lines));
    }

    [TestMethod]
    public async Task APlanThatWillNotFit_IsNotOk_AndSaysWhere()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(
            new RestoreResult(2, 0, "/out", "complete"),
            new RestorePlanResult(2, 9_000, [], Space: [new RestoreSpaceDescriptor("/out", NeededBytes: 9_000, AvailableBytes: 1_000)]));

        var report = await PlanAsync(client, timeout.Token);

        Assert.IsFalse(report.Ok);
        Assert.IsTrue(
            report.Lines.Any(line => line.Contains("/out", StringComparison.Ordinal) && line.Contains("would not fit", StringComparison.Ordinal)),
            string.Join(" | ", report.Lines));
    }

    /// <summary>
    /// Restores <see cref="SnapshotId"/> through <paramref name="client"/> as
    /// <c>restore --passphrase-env</c> does: a restore names the backup's
    /// files, so it reads through a source the passphrase unlocked (FR-WOR-007).
    /// </summary>
    private static Task<OperationReport> RestoreAsync(ScriptedClient client, CancellationToken cancellationToken) =>
        ThroughAsync(client, (gateway, token) => gateway.RestoreAsync(new RestoreRequest(SnapshotId, null, "/out"), token), cancellationToken);

    /// <summary>Plans what <see cref="RestoreAsync"/> would run, as <c>restore --plan</c> does.</summary>
    private static Task<OperationReport> PlanAsync(ScriptedClient client, CancellationToken cancellationToken) =>
        ThroughAsync(client, (gateway, token) => gateway.PlanRestoreAsync(new RestoreRequest(SnapshotId, null, "/out"), token), cancellationToken);

    private static async Task<OperationReport> ThroughAsync(
        ScriptedClient client, Func<ServiceGateway, CancellationToken, ValueTask<OperationReport>> verb, CancellationToken cancellationToken)
    {
        // A variable of this test's own, so no concurrent class reads it.
        var variable = "FBP_TEST_RESTORE_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(variable, ThePassphrase);
        try
        {
            var gateway = new ServiceGateway(client, "scripted", client, variable);
            return await verb(gateway, cancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// Answers as a set-up installation with one set (ADR-0042 §7): it
    /// publishes the facts a grant is derived under, opens a source holding
    /// <see cref="SnapshotId"/> only for a grant that proves the passphrase,
    /// and answers a restore through that source with <paramref name="restored"/>,
    /// and a plan with <paramref name="planned"/>.
    /// </summary>
    private sealed class ScriptedClient(RestoreResult restored, RestorePlanResult? planned = null) : IFallbackPlanClient
    {
        private const string SourceId = "scripted-source";

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
            return Answer(command);
        }

        private ValueTask<ServiceResult> Answer(ServiceCommand command) =>
            ValueTask.FromResult<ServiceResult>(command switch
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
                RunRestoreCommand { Source: SourceId } => restored,
                RunRestoreCommand => new ServiceError(ServiceErrorReason.Refused, "a restore needs an unlocked source"),
                PlanRestoreCommand { Source: SourceId } when planned is not null => planned,
                PlanRestoreCommand => new ServiceError(ServiceErrorReason.Refused, "a plan needs an unlocked source"),
                _ => new AcknowledgedResult(),
            });

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
