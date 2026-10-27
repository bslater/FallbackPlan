using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Cli;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// What <c>restore</c> prints when the service read some files around damage
/// (FR-RST-007, contract 1.45): the count and the service's own lines saying
/// which files came from which copy, and around what — so a person restoring
/// learns that a copy of their backup is damaged from the restore that met
/// it, not only from a notice they may never open. And what
/// <c>restore --plan</c> prints through the service (FR-RST-003, ADR-0107):
/// the plan for the run's own shape, with nothing run. And what
/// <c>restore --resume</c> asks of the service (FR-RST-008, ADR-0109): the
/// run it continues, named, and what that run found already in place. And a
/// plan held to a kept one through the service (FR-RST-003, ADR-0114): what
/// changed since it was kept, or that nothing did. And a resume's plan
/// through the service (FR-RST-008, ADR-0120): asked for the run it
/// continues, or for a run left unfinished by its name alone, saying how many
/// files the run already landed.
/// </summary>
[TestClass]
public sealed class GatewayRestoreReportTests
{
    private const string ThePassphrase = "the scripted installation's passphrase";

    private static readonly string SnapshotId = new('e', 32);

    private const string UnfinishedRun = "0123456789abcdef";

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
        Assert.IsNull(
            Assert.ContainsSingle(client.Received.OfType<RunRestoreCommand>()).Resume,
            "a restore not told to resume names no run");
        Assert.IsFalse(report.Lines.Any(printed => printed.Contains("already in place", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AResume_NamesTheRunItContinues_AndSaysWhatWasFoundInPlace()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(new RestoreResult(
            2, 0, "/out/.fbp-quarantine/0123456789abcdef", "complete", FoundInPlace: 1));

        var report = await ThroughAsync(
            client,
            (gateway, token) => gateway.RestoreAsync(
                new RestoreRequest(SnapshotId, null, "/out", Resume: true, ResumedRun: "0123456789abcdef"), token),
            timeout.Token);

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        Assert.AreEqual("0123456789abcdef", Assert.ContainsSingle(client.Received.OfType<RunRestoreCommand>()).Resume);
        Assert.IsTrue(
            report.Lines.Any(printed => printed.Contains("1 file(s) already in place", StringComparison.Ordinal)),
            string.Join(" | ", report.Lines));
    }

    [TestMethod]
    [DataRow("failed")]
    [DataRow("cancelled")]
    public async Task ARestoreThatDidNotFinish_SaysHowToResumeIt(string outcome)
    {
        // ADR-0110: the run's name is what --resume takes, so the CLI says it
        // where a run was cut short.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(new RestoreResult(
            1, outcome == "failed" ? 1 : 0, "/out/.fbp-quarantine/0123456789abcdef", outcome, Run: "0123456789abcdef"));

        var report = await RestoreAsync(client, timeout.Token);

        Assert.IsTrue(
            report.Lines.Any(printed => printed.Contains("--resume 0123456789abcdef", StringComparison.Ordinal)),
            string.Join(" | ", report.Lines));
    }

    [TestMethod]
    [DataRow("complete")]
    [DataRow("partial")]
    public async Task ARestoreAResumeWouldNotAddTo_SaysNothingOfResuming(string outcome)
    {
        // A partial run skipped what this target cannot hold, which a resume
        // would skip again.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(new RestoreResult(
            1, 0, "/out/.fbp-quarantine/0123456789abcdef", outcome, Run: "0123456789abcdef"));

        var report = await RestoreAsync(client, timeout.Token);

        Assert.IsFalse(report.Lines.Any(printed => printed.Contains("--resume", StringComparison.Ordinal)), string.Join(" | ", report.Lines));
    }

    [TestMethod]
    public async Task ARunAndItsPlan_AreAskedOfTheServiceAsTheCommandLineAsksThem()
    {
        // ADR-0117: several paths, a policy and a landing in place, as the
        // console asks them.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(
            new RestoreResult(2, 0, "/out", "complete"), new RestorePlanResult(2, 11, []));
        var request = new RestoreRequest(SnapshotId, null, "/out", Paths: ["docs/a.txt", "docs/b"], Existing: "rename", InPlace: true);

        await ThroughAsync(client, (gateway, token) => gateway.RestoreAsync(request, token), timeout.Token);
        await ThroughAsync(client, (gateway, token) => gateway.PlanRestoreAsync(request, token), timeout.Token);

        var run = Assert.ContainsSingle(client.Received.OfType<RunRestoreCommand>());
        SequenceAssert.AreEqual(["docs/a.txt", "docs/b"], run.Paths!);
        Assert.IsNull(run.Path);
        Assert.AreEqual("rename", run.Existing);
        Assert.IsTrue(run.InPlace);
        Assert.IsNull(run.Target);
        var plan = Assert.ContainsSingle(client.Received.OfType<PlanRestoreCommand>());
        SequenceAssert.AreEqual(["docs/a.txt", "docs/b"], plan.Paths!);
        Assert.AreEqual("rename", plan.Existing);
        Assert.IsTrue(plan.InPlace);
        Assert.AreEqual("/out", plan.OutputDirectory);
    }

    [TestMethod]
    public async Task TheOriginalLocation_IsAskedOfTheService_WithNoFolder()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(new RestoreResult(1, 0, "/docs", "complete"));

        var report = await ThroughAsync(
            client,
            (gateway, token) => gateway.RestoreAsync(
                new RestoreRequest(SnapshotId, "docs/a.txt", string.Empty, Target: "original", Existing: "overwrite"), token),
            timeout.Token);

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        var run = Assert.ContainsSingle(client.Received.OfType<RunRestoreCommand>());
        Assert.AreEqual("original", run.Target);
        Assert.AreEqual("overwrite", run.Existing);
        Assert.AreEqual(string.Empty, run.OutputDirectory);
    }

    [TestMethod]
    public async Task APlanOfTheOriginalLocation_SaysTheFilesGoBackWhereTheyWereCaptured()
    {
        // The run names no folder, so a plan that said where by the folder
        // asked would say nothing.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(
            new RestoreResult(1, 0, "/docs", "complete"), new RestorePlanResult(1, 5, [], WriteBytes: 4_096));

        var report = await ThroughAsync(
            client,
            (gateway, token) => gateway.PlanRestoreAsync(
                new RestoreRequest(SnapshotId, "docs/a.txt", string.Empty, Target: "original"), token),
            timeout.Token);

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        StringAssert.StartsWith(report.Lines[0], "plan: 1 file(s) back where they were captured, 4096 byte(s) written");
    }

    [TestMethod]
    public async Task APlanOfAResume_AsksForTheRunItContinues_AndSaysHowManyFilesItLanded()
    {
        // FR-RST-008, ADR-0120: planned where the run landed, and only planned.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(
            new RestoreResult(2, 0, "/out", "complete"), new RestorePlanResult(2, 9, [], Landed: 1));

        var report = await ThroughAsync(
            client,
            (gateway, token) => gateway.PlanRestoreAsync(
                new RestoreRequest(SnapshotId, null, "/out", Resume: true, ResumedRun: UnfinishedRun), token),
            timeout.Token);

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        Assert.AreEqual(UnfinishedRun, Assert.ContainsSingle(client.Received.OfType<PlanRestoreCommand>()).Resume);
        Assert.IsTrue(
            report.Lines.Any(line => line.StartsWith("resume: 1 file(s) already landed", StringComparison.Ordinal)),
            string.Join(" | ", report.Lines));
        Assert.IsEmpty(client.Received.OfType<RunRestoreCommand>(), "a plan restores nothing");
    }

    [TestMethod]
    public async Task APlanOfAResumeNamingNoRun_IsRefusedBeforeAnythingIsAsked()
    {
        // Through the service each run lands in a folder of its own, so a plan
        // of a resume that names none has nowhere to be measured.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(new RestoreResult(2, 0, "/out", "complete"), new RestorePlanResult(2, 9, []));

        var refused = await Assert.ThrowsExactlyAsync<CliFailureException>(() => ThroughAsync(
            client,
            (gateway, token) => gateway.PlanRestoreAsync(new RestoreRequest(SnapshotId, null, "/out", Resume: true), token),
            timeout.Token));

        Assert.Contains(".fbp-quarantine", refused.Message, StringComparison.Ordinal);
        Assert.IsEmpty(client.Received);
    }

    [TestMethod]
    public async Task ARunLeftUnfinished_IsPlannedByItsNameAlone_ThroughASourceOfItsSnapshot()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(
            new RestoreResult(2, 0, "/out", "complete"), new RestorePlanResult(2, 9, [], Landed: 2));

        var report = await ThroughAsync(
            client, (gateway, token) => gateway.PlanResumeRestoreAsync(UnfinishedRun, token), timeout.Token);

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        var asked = Assert.ContainsSingle(client.Received.OfType<PlanResumeRestoreCommand>());
        Assert.AreEqual(UnfinishedRun, asked.Run);
        Assert.IsNotNull(asked.Source, "the plan names the backup's files, so it reads through an unlocked source");
        Assert.IsTrue(
            report.Lines.Any(line => line.StartsWith("resume: 2 file(s) already landed", StringComparison.Ordinal)),
            string.Join(" | ", report.Lines));
        Assert.IsEmpty(client.Received.OfType<ResumeRestoreCommand>(), "a plan resumes nothing");
    }

    [TestMethod]
    public async Task APlanOfARunNotLeftUnfinished_IsRefused_BeforeThePassphraseIsAsked()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(new RestoreResult(2, 0, "/out", "complete"), new RestorePlanResult(2, 9, []));

        var refused = await Assert.ThrowsExactlyAsync<CliFailureException>(() => ThroughAsync(
            client, (gateway, token) => gateway.PlanResumeRestoreAsync("fedcba9876543210", token), timeout.Token));

        Assert.Contains("fedcba9876543210", refused.Message, StringComparison.Ordinal);
        Assert.IsEmpty(client.Received.OfType<OpenRestoreSourceCommand>());
        Assert.IsEmpty(client.Received.OfType<PlanResumeRestoreCommand>());
    }

    [TestMethod]
    public async Task AResumeNamingNoRun_IsRefusedBeforeAnythingIsAsked()
    {
        // Through the service each run lands in a folder of its own, so a
        // resume that names none has nowhere to continue.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(new RestoreResult(2, 0, "/out/.fbp-quarantine/run", "complete"));

        var refused = await Assert.ThrowsExactlyAsync<CliFailureException>(() => ThroughAsync(
            client,
            (gateway, token) => gateway.RestoreAsync(new RestoreRequest(SnapshotId, null, "/out", Resume: true), token),
            timeout.Token));

        Assert.Contains(".fbp-quarantine", refused.Message, StringComparison.Ordinal);
        Assert.IsEmpty(client.Received);
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
    public async Task APlanHeldToAKeptOne_ThroughTheService_SaysWhatChanged_AndRunsNothing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(
            new RestoreResult(2, 0, "/out", "complete"),
            new RestorePlanResult(2, 9_000, ["docs/gone.txt"], ArchivedObjects: ["docs/cold.txt"]));

        var report = await ThroughAsync(
            client,
            (gateway, token) => gateway.PlanRestoreAsync(
                new RestoreRequest(SnapshotId, null, "/out", AsPlanned: Kept(new RestorePlanResult(2, 9_000, []))), token),
            timeout.Token);

        Assert.IsFalse(report.Ok, "a plan that changed since it was kept is not the run that was approved");
        var said = string.Join(" | ", report.Lines);
        Assert.Contains("no longer as kept in /plans/approved.json", said, StringComparison.Ordinal);
        CollectionAssert.Contains(report.Lines.ToList(), "  missing now: docs/gone.txt", "each change is said under the line that says the plan changed");
        CollectionAssert.Contains(report.Lines.ToList(), "  archived now: docs/cold.txt");
        Assert.IsEmpty(client.Received.OfType<RunRestoreCommand>());
    }

    [TestMethod]
    public async Task APlanHeldToAKeptOne_SaysWhatNoLongerHolds_AndEveryCountThatMoved()
    {
        // Better news is still news: the run approved is not the run it
        // would now be.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(
            new RestoreResult(2, 0, "/out", "complete"),
            new RestorePlanResult(2, 9_000, []));

        var report = await ThroughAsync(
            client,
            (gateway, token) => gateway.PlanRestoreAsync(
                new RestoreRequest(SnapshotId, null, "/out", AsPlanned: Kept(new RestorePlanResult(
                    3, 9_000, ["docs/back.txt"], Conflicts: 1, ArchivedObjects: ["docs/warm.txt"]))),
                token),
            timeout.Token);

        Assert.IsFalse(report.Ok);
        var said = string.Join(" | ", report.Lines);
        Assert.Contains("missing no longer: docs/back.txt", said, StringComparison.Ordinal);
        Assert.Contains("archived no longer: docs/warm.txt", said, StringComparison.Ordinal);
        Assert.Contains("files: 3 kept, 2 now", said, StringComparison.Ordinal);
        Assert.Contains("conflicts: 1 kept, 0 now", said, StringComparison.Ordinal);
        Assert.IsEmpty(client.Received.OfType<RunRestoreCommand>());
    }

    [TestMethod]
    public async Task APlanHeldToAKeptOne_ThatIsUnchanged_SaysSo_EvenWhereItWouldFailFiles()
    {
        // The plan approved named a file it could not bring back: that was
        // part of what was approved, and is not news.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(
            new RestoreResult(2, 0, "/out", "complete"),
            new RestorePlanResult(2, 9_000, ["docs/gone.txt"], Conflicts: 1));

        var report = await ThroughAsync(
            client,
            (gateway, token) => gateway.PlanRestoreAsync(
                new RestoreRequest(SnapshotId, null, "/out", AsPlanned: Kept(new RestorePlanResult(2, 9_000, ["docs/gone.txt"], Conflicts: 1))),
                token),
            timeout.Token);

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        Assert.Contains("as kept in /plans/approved.json", report.Lines[0], StringComparison.Ordinal);
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

    /// <summary>A plan kept for the run <see cref="RestoreAsync"/> makes, as <c>restore --export</c> keeps one.</summary>
    private static KeptPlan Kept(RestorePlanResult plan) =>
        new("/plans/approved.json", RestorePlanFile.Read(RestorePlanFile.Serialize(
            new PlanRestoreCommand(SnapshotId, null, OutputDirectory: "/out"), plan, DateTimeOffset.UtcNow)));

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
                ListRestoreRunsCommand => new RestoreRunsResult(
                    [new RestoreRunDescriptor(UnfinishedRun, SnapshotId, "docs", "/out", 1_000, Existing: "rename")]),
                PlanResumeRestoreCommand { Source: SourceId } when planned is not null => planned,
                PlanResumeRestoreCommand => new ServiceError(ServiceErrorReason.Refused, "a plan needs an unlocked source"),
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
