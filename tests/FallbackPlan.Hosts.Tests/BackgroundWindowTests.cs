using System.Globalization;
using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The background window in force (NFR-PERF-013, NFR-OPS-004, ADR-0069): the
/// first of that requirement's four named limits to exist, and the rule that
/// makes it a limit rather than a preference — background activity does not
/// start outside it, and a person is never held by it.
/// </summary>
/// <remarks>
/// <para>
/// "Background activity" is not a judgement call here: it is exactly what the
/// scheduler starts with <c>userInitiated: false</c> — captures, fan-out,
/// the deep sweep and the drills. A window that held only the capture would
/// be the setting an operator thought they had and not the one they got,
/// because the thing saturating a domestic uplink at nine in the morning is
/// as likely to be the fan-out.
/// </para>
/// <para>
/// The case that carries the most weight is the last one: an installation
/// with no window behaves exactly as it did. Every file written before schema
/// 6 says "no window" by not mentioning one, so a regression there stops
/// every backup on every existing installation at once.
/// </para>
/// <para>
/// Does not establish FR-DRL-002 — the drill is observed here as background
/// activity and nothing about what it proves is tested.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class BackgroundWindowTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string Spare => Path.Combine(_harness.WorkPath, "spare");

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task ADueSet_OutsideTheWindow_DoesNotRunAndSaysWhy()
    {
        await using var runtime = await StartAsync(ShutWindow);

        // The set is due — it has never run — so the row must not read as
        // "not due": the whole point is that a person can tell work held back
        // from work there was none of.
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);

        Assert.AreEqual(0, pass.Ran);
        var outcome = Assert.ContainsSingle(pass.Sets);
        Assert.AreEqual("outside-window", outcome.Outcome);
        Assert.IsNotNull(outcome.Detail);
        Assert.Contains("opens", outcome.Detail, StringComparison.Ordinal);

        // And nothing was captured behind the row.
        Assert.IsEmpty(runtime.Jobs.Jobs);
    }

    [TestMethod]
    public async Task ADueSet_InsideTheWindow_RunsAsItAlwaysDid()
    {
        await using var runtime = await StartAsync(OpenWindow);

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);

        Assert.AreEqual(1, pass.Ran);
        Assert.ContainsSingle(runtime.Jobs.Jobs);
    }

    [TestMethod]
    public async Task OutsideTheWindow_TheFanOutIsHeldWithTheCapture()
    {
        // A window that gated only the capture would be the setting an
        // operator thought they had rather than the one they got: the thing
        // saturating a domestic uplink at nine in the morning is as likely to
        // be the fan-out, the deep sweep or a drill, and all four are what the
        // scheduler starts with nobody waiting.
        await using var runtime = await StartAsync(OpenWindow);
        var opened = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await opened.Transfers.WaitAsync(Timeout);
        Assert.AreEqual(1, opened.Ran);

        // A second destination the pass has never reached, and a window that
        // has since shut. A pair with no ledger row is due a sync whatever
        // the clock says, so the fan-out has work here for a reason that does
        // not depend on an interval elapsing.
        Directory.CreateDirectory(Spare);
        WriteConfiguration(ShutWindow, withSpare: true);

        var shut = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await shut.Transfers.WaitAsync(Timeout);
        await shut.Drills.WaitAsync(Timeout);

        Assert.IsNull(
            runtime.DestinationSync.Find(_harness.DocsSetId, "spare"),
            "the fan-out must not have reached a new destination outside the window");

        // The control, and what makes the assertion above mean something: the
        // same instant and the same state, with the window not consulted,
        // does reach it. Without it a fixture with no work to do would pass.
        var person = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout, userInitiated: true);
        await person.Transfers.WaitAsync(Timeout);
        await person.Drills.WaitAsync(Timeout);

        Assert.IsNotNull(runtime.DestinationSync.Find(_harness.DocsSetId, "spare"));
    }

    [TestMethod]
    public async Task APersonsPass_IsNeverHeldByTheWindow()
    {
        // `--once` and the CLI's direct pass are a person at a terminal. The
        // rule is ADR-0029's — a user-initiated operation outranks a
        // scheduled one — applied to the window rather than to the pool.
        await using var runtime = await StartAsync(ShutWindow);

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout, userInitiated: true);
        await pass.Transfers.WaitAsync(Timeout);

        Assert.AreEqual(1, pass.Ran);
    }

    [TestMethod]
    public async Task NoWindow_ChangesNothing()
    {
        // The compatibility pin the whole migration rests on: absent means
        // any hour, which is what every configuration written before schema 6
        // says by not mentioning a window. A regression here stops every
        // backup on every existing installation at once.
        await using var runtime = await StartAsync(window: null);

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);

        Assert.AreEqual(1, pass.Ran);
        Assert.IsEmpty(pass.Sets.Where(set => set.Outcome == "outside-window"));
    }

    [TestMethod]
    public async Task ACaptureRunningWhenTheWindowShuts_ParksAndSaysTheWindow()
    {
        // The other half of the limit. C1 stopped background work STARTING
        // outside the window; a capture already under way was untouched, so
        // an operator who set 22:00-06:00 because their evening calls stutter
        // still got a multi-hour capture running through the evening whenever
        // one happened to be in flight at ten.
        await using var runtime = await StartManyFileCaptureAsync();
        var set = runtime.Configuration.BackupSets.Single();
        var backup = Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: false);
        await WaitUntilCapturingAsync(runtime, backup);

        WriteConfiguration(ShutWindow);
        await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);

        var parked = await WaitForStateAsync(runtime, backup, JobState.Paused);
        Assert.IsFalse(backup.IsCompleted, "the capture must be suspended, not finished");

        // And the journal says WHY in the words a person reads the next
        // morning. A row saying "suspended for a higher-priority run" would
        // send them looking for a run that never existed.
        Assert.IsNotNull(parked.Detail);
        Assert.Contains("background window", parked.Detail, StringComparison.Ordinal);

        WriteConfiguration(OpenWindow);
        await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);

        var outcome = await backup.WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    [TestMethod]
    public async Task APersonsPass_DoesNotReleaseWhatTheWindowIsHolding()
    {
        // A person's pass is not gated by the window (above) and must not
        // speak for the machine either: `agent run --once` at two in the
        // morning answering for the operator's 22:00-06:00 would undo the
        // setting on behalf of somebody who only wanted one set backed up.
        await using var runtime = await StartManyFileCaptureAsync();
        var set = runtime.Configuration.BackupSets.Single();
        var backup = Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: false);
        await WaitUntilCapturingAsync(runtime, backup);

        WriteConfiguration(ShutWindow);
        await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await WaitForStateAsync(runtime, backup, JobState.Paused);

        await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout, userInitiated: true);

        // Long enough for a freed worker to have gone round the pump: a
        // release would resume this run at once, not eventually.
        await Task.Delay(500, Timeout);
        Assert.AreEqual(JobState.Paused, Latest(runtime, backup).State);
        Assert.IsFalse(backup.IsCompleted);

        WriteConfiguration(OpenWindow);
        await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await backup.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task AClosureThatOutlastsThePauseCap_SelfCancelsAndCapturesAtTheNextOpening()
    {
        // The keystone of the whole slice, run rather than argued: a parked
        // run holds its in-memory state and a live write intent, and ADR-0047
        // already bounds how long that price is worth paying. A window that
        // stays shut past the cap therefore needs no machinery of its own —
        // the run degrades to the interruption-safe re-run path and captures
        // when the window opens.
        await using var runtime = await StartManyFileCaptureAsync(TimeSpan.FromMilliseconds(250));
        var set = runtime.Configuration.BackupSets.Single();
        var backup = Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: false);
        await WaitUntilCapturingAsync(runtime, backup);

        WriteConfiguration(ShutWindow);
        await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);

        var abandoned = await backup.WaitAsync(Timeout);
        Assert.AreNotEqual("completed", abandoned.Outcome);
        Assert.IsEmpty(runtime.Jobs.Jobs.Where(job => job.State == JobState.Paused));

        // The set is still due — nothing completed — so the opening captures
        // it, from the spool the cancelled run left behind.
        WriteConfiguration(OpenWindow);
        var opening = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);

        Assert.AreEqual(1, opening.Ran);
        Assert.ContainsSingle(runtime.Jobs.Jobs.Where(job => job.State == JobState.Complete));
    }

    private static JobRecord Latest(ServiceRuntime runtime, Task<BackupOutcome> backup)
    {
        _ = backup;
        return runtime.Jobs.Jobs[^1];
    }

    private async Task WaitUntilCapturingAsync(ServiceRuntime runtime, Task<BackupOutcome> backup)
    {
        // Genuinely mid-scan, not merely queued: the pause gate is checked
        // between scan events, so a run that has not reached the scan has no
        // boundary to park at and the case would pass for the wrong reason.
        while (!runtime.Jobs.Jobs.Any(job => job.State is JobState.Scanning or JobState.Publishing))
        {
            Assert.IsFalse(backup.IsCompleted, "the capture finished before the window could shut over it");
            await Task.Delay(10, Timeout);
        }
    }

    private async Task<JobRecord> WaitForStateAsync(
        ServiceRuntime runtime, Task<BackupOutcome> backup, JobState state)
    {
        while (true)
        {
            if (runtime.Jobs.Jobs.LastOrDefault(job => job.State == state) is { } found)
            {
                return found;
            }

            Assert.IsFalse(backup.IsCompleted, $"the capture finished before it reported {state}");
            await Task.Delay(10, Timeout);
        }
    }

    /// <summary>
    /// A fixture whose capture is long enough to still be running when the
    /// window shuts over it, and which yields thousands of file boundaries to
    /// park at.
    /// </summary>
    private async Task<ServiceRuntime> StartManyFileCaptureAsync(TimeSpan? maxPause = null)
    {
        Directory.CreateDirectory(Vault);
        for (var i = 0; i < 1500; i++)
        {
            _harness.WriteSourceFile($"many/file-{i:d5}.txt", $"contents of file {i}");
        }

        WriteConfiguration(OpenWindow);
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                MaxConcurrentBackupsOverride = 1,
                MaxPauseOverride = maxPause,
            },
            Timeout);
    }

    /// <summary>
    /// A window that is shut right now: one hour, starting three hours from
    /// now. Expressed against the real clock rather than a fixed hour of the
    /// day, because the ledger the later phases consult records real
    /// timestamps — a pass driven from a clock pointing into yesterday
    /// compares them against figures from today and the case stops meaning
    /// what it says.
    /// </summary>
    private static string ShutWindow => Window(DateTimeOffset.Now.AddHours(3), TimeSpan.FromHours(1));

    /// <summary>A window that is open right now: from an hour ago to an hour ahead.</summary>
    private static string OpenWindow => Window(DateTimeOffset.Now.AddHours(-1), TimeSpan.FromHours(2));

    private static string Window(DateTimeOffset opens, TimeSpan length) => string.Create(
        CultureInfo.InvariantCulture,
        $"{opens.ToString("HH:mm", CultureInfo.InvariantCulture)}-{(opens + length).ToString("HH:mm", CultureInfo.InvariantCulture)}");

    private void WriteConfiguration(string? window, bool withSpare = false)
    {
        List<DestinationConfiguration> destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('d', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
            },
        ];

        List<SetDestinationReference> references = [new SetDestinationReference { Ref = "vault" }];

        if (withSpare)
        {
            destinations.Add(new DestinationConfiguration
            {
                Id = new string('e', 32), Name = "spare", Kind = DestinationKind.LocalPath, Path = Spare,
            });
            references.Add(new SetDestinationReference { Ref = "spare" });
        }

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            BackgroundWindow = window,
            Destinations = destinations,
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = references,
                    DirectShip = false,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
    }

    private async Task<ServiceRuntime> StartAsync(string? window)
    {
        Directory.CreateDirectory(Vault);
        _harness.WriteSourceFile("docs/content.txt", new string('w', 60_000) + "bytes to capture");
        WriteConfiguration(window);

        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            Timeout);
    }
}
