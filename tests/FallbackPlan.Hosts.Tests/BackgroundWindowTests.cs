using System.Globalization;
using FallbackPlan.Agent;
using FallbackPlan.Application;

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
