using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The Agent pass end to end (ADR-0027; P2-H acceptance): a due set runs
/// exactly once through the real engine, a not-due set is skipped, missed
/// runs coalesce, and failures land in the journal with the right class.
/// </summary>
public sealed class AgentPassTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-agent-tests", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "agent-pass-tests-passphrase!!";

    private string RepoPath => Path.Combine(_root, "repo");

    private string StateDirectory => Path.Combine(_root, "state");

    private string SourceRoot => Path.Combine(_root, "source");

    public AgentPassTests()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(SourceRoot);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "hello agent");
        Directory.CreateDirectory(Path.Combine(SourceRoot, "sub"));
        File.WriteAllBytes(Path.Combine(SourceRoot, "sub", "b.bin"), [.. Enumerable.Range(0, 5000).Select(i => (byte)i)]);
    }

    private async Task CreateRepositoryAsync()
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var _ = await RepositoryLifecycle.CreateAsync(
            new LocalFileSystemObjectStore(RepoPath), passphrase,
            Domain.Configuration.RepositoryCreationSettings.Default,
            createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None);
    }

    private void WriteConfiguration(string schedule) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = new string('a', 32),
                Name = "docs",
                Root = SourceRoot,
                Schedule = schedule,
            },
        ],
    }.Save(Path.Combine(StateDirectory, "config.json"));

    private async Task<AgentPassResult> RunPassAsync(DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        return await AgentPass.RunAsync(RepoPath, passphrase, StateDirectory, now, CancellationToken.None);
    }

    [Fact]
    public async Task A_due_set_runs_once_and_the_next_pass_skips_it()
    {
        await CreateRepositoryAsync();
        WriteConfiguration("every 4h");
        var now = new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);

        // Never run: due — the pass backs it up through the real engine.
        var first = await RunPassAsync(now);
        var ran = Assert.Single(first.Sets);
        Assert.Equal("ran", ran.Outcome);
        Assert.Equal(1, first.Ran);

        // The journal anchors the schedule; minutes later nothing is due.
        var second = await RunPassAsync(now.AddMinutes(5));
        Assert.Equal("not-due", Assert.Single(second.Sets).Outcome);

        // Past the interval — one run again, no backlog however late
        // (missed runs coalesce, ADR-0027 §1). The second run is
        // incremental: everything unchanged.
        var third = await RunPassAsync(now.AddDays(3));
        var caughtUp = Assert.Single(third.Sets);
        Assert.Equal("ran", caughtUp.Outcome);
        Assert.Contains("2 unchanged", caughtUp.Detail);

        // The journal shows exactly two completed jobs with snapshots.
        var jobs = JobStateStore.Open(StateDirectory);
        Assert.Equal(2, jobs.Jobs.Count(job => job.State == JobState.Complete));
        Assert.All(
            jobs.Jobs.Where(job => job.State == JobState.Complete),
            job => Assert.NotNull(job.SnapshotId));

        // And the catalogue really holds both snapshots.
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(
            new LocalFileSystemObjectStore(RepoPath), passphrase, CancellationToken.None);
        using var catalogue = CatalogueDb.Open(Path.Combine(StateDirectory, "catalogue.db"), repository.RepositoryId);
        Assert.Equal(2, catalogue.EnumerateSnapshots().Count);
    }

    [Fact]
    public async Task An_unscheduled_set_is_manual_only_never_an_error()
    {
        await CreateRepositoryAsync();
        WriteConfiguration(schedule: null!);
        var result = await RunPassAsync(DateTimeOffset.UtcNow);
        Assert.Equal("manual-only", Assert.Single(result.Sets).Outcome);
        Assert.Empty(JobStateStore.Open(StateDirectory).Jobs);
    }

    [Fact]
    public async Task A_missing_root_is_a_recoverable_failure_and_a_bad_schedule_is_permanent()
    {
        await CreateRepositoryAsync();

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = new string('b', 32), Name = "gone",
                    Root = Path.Combine(_root, "unmounted"), Schedule = "every 1h",
                },
                new BackupSetConfiguration
                {
                    Id = new string('c', 32), Name = "typo",
                    Root = SourceRoot, Schedule = "hourly",
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));

        var result = await RunPassAsync(DateTimeOffset.UtcNow);
        Assert.Equal(2, result.Failed);

        // The classes differ because the user action differs (10 §3): an
        // unmounted drive resolves itself; a typo'd schedule needs a human.
        var jobs = JobStateStore.Open(StateDirectory);
        Assert.Single(jobs.RecoverableFailures(new string('b', 32)));
        Assert.Contains(jobs.Jobs, job =>
            job.BackupSetId == new string('c', 32) && job.State == JobState.FailedPermanent);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
