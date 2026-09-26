using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The Agent pass end to end (ADR-0027; P2-H acceptance): a due set runs
/// exactly once through the real engine, a not-due set is skipped, missed
/// runs coalesce, and failures land in the journal with the right class.
/// </summary>
[TestClass]
public sealed class AgentPassTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-agent-tests", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "agent-pass-tests-passphrase!!";

    private string ArchivesRoot => Path.Combine(_root, "archives");

    private string RepoPath => Path.Combine(ArchivesRoot, new string('a', 32));

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

    // The installation is set up and the "docs" archive created the way the
    // service creates one on a set's first backup: from the installation
    // credential, under the installation's salt (ADR-0044).
    private async Task CreateRepositoryAsync()
    {
        WriteOnlyInstallation.Provision(StateDirectory, PassphraseText);
        using var provisioning = new InstallationCredentialStore(StateDirectory).TryLoad();
        Assert.IsNotNull(provisioning);
        (await RepositoryLifecycle.CreateAsync(
            new LocalFileSystemObjectStore(RepoPath), provisioning.Credential,
            provisioning.KdfSalt.ToArray(), provisioning.KdfParameters,
            createdBy: "fallbackplan-tests/1.0",
            createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None)).Dispose();
    }

    private DestinationConfiguration Vault => new()
    {
        Id = new string('d', 32),
        Name = "vault",
        Kind = DestinationKind.LocalPath,
        Path = Path.Combine(StateDirectory, "vault"),
    };

    private static SetDestinationReference VaultRef => new() { Ref = "vault" };

    private void WriteConfiguration(string schedule) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations = [Vault],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = new string('a', 32),
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = SourceRoot }],
                Schedule = schedule,
                Destinations = [VaultRef],
            },
        ],
    }.Save(Path.Combine(StateDirectory, "config.json"));

    private static string Outcomes(AgentPassResult result) =>
        string.Join("; ", result.Sets.Select(set => $"{set.Outcome}: {set.Detail}"));

    private async Task<AgentPassResult> RunPassAsync(DateTimeOffset now)
    {
        // A pass creates any missing archive from the installation credential
        // (ADR-0044), so the installation is set up before the first pass
        // whether or not a test created an archive by hand.
        using (var provisioned = new InstallationCredentialStore(StateDirectory).TryLoad())
        {
            if (provisioned is null)
            {
                WriteOnlyInstallation.Provision(StateDirectory, PassphraseText);
            }
        }

        return await AgentPass.RunAsync(ArchivesRoot, StateDirectory, now, CancellationToken.None);
    }

    [TestMethod]
    public async Task AgentPass_ADestinationDeclaredWithARelativePath_IsRecordedFailedAndWritesNothing()
    {
        // The boundary now pins relative paths absolute, but a hand-edited
        // config.json still reaches the fan-out verbatim — and verbatim, the
        // path resolves against the process working directory, which is how a
        // replica tree once appeared beside the service's logs while the
        // intended folder stayed empty. The pass must refuse the declaration
        // as needing a person (Failed, not the retried-forever Unavailable)
        // and write nothing anywhere.
        await CreateRepositoryAsync();
        var relative = "relative-vault-" + Guid.NewGuid().ToString("n")[..8];
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [Vault with { Path = relative }],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = new string('a', 32),
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = SourceRoot }],
                    Schedule = "every 4h",
                    Destinations = [VaultRef],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));

        var pass = await RunPassAsync(new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero));
        Assert.AreEqual("ran", Assert.ContainsSingle(pass.Sets).Outcome);

        var row = DestinationSyncStore.Open(StateDirectory).Find(new string('a', 32), "vault");
        Assert.IsNotNull(row);
        Assert.AreEqual(DestinationSyncState.Failed, row.State);
        Assert.Contains("relative", row.LastError!, StringComparison.Ordinal);

        Assert.IsFalse(
            Directory.Exists(Path.GetFullPath(relative)),
            "the defective declaration must never resolve against the working directory");
    }

    [TestMethod]
    public async Task AgentPass_ABackupSetIsDue_RunsItOnceAndSkipsItOnTheNextPass()
    {
        await CreateRepositoryAsync();
        WriteConfiguration("every 4h");
        var now = new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);

        // Never run: due — the pass backs it up through the real engine.
        var first = await RunPassAsync(now);
        var ran = Assert.ContainsSingle(first.Sets);
        Assert.AreEqual("ran", ran.Outcome);
        Assert.AreEqual(1, first.Ran);

        // The journal anchors the schedule; minutes later nothing is due.
        var second = await RunPassAsync(now.AddMinutes(5));
        Assert.AreEqual("not-due", Assert.ContainsSingle(second.Sets).Outcome);

        // Past the interval — one run again, no backlog however late
        // (missed runs coalesce, ADR-0027 §1). The second run is
        // incremental: everything unchanged.
        var third = await RunPassAsync(now.AddDays(3));
        var caughtUp = Assert.ContainsSingle(third.Sets);
        Assert.AreEqual("ran", caughtUp.Outcome);
        Assert.IsNotNull(caughtUp.Detail);
        Assert.Contains("2 unchanged", caughtUp.Detail);

        // The journal shows exactly two completed jobs with snapshots.
        var jobs = JobStateStore.Open(StateDirectory);
        Assert.AreEqual(2, jobs.Jobs.Count(job => job.State == JobState.Complete));
        foreach (var job in jobs.Jobs.Where(job => job.State == JobState.Complete))
        {
            Assert.IsNotNull(job.SnapshotId);
        }

        // And the catalogue really holds both snapshots.
        using var opened = await WriteOnlyInstallation.OpenAsync(
            new LocalFileSystemObjectStore(RepoPath), PassphraseText, CancellationToken.None);
        var repository = opened.Repository;
        using var catalogue = CatalogueDb.Open(
            Path.Combine(StateDirectory, $"catalogue-{repository.RepositoryId}.db"), repository.RepositoryId);
        Assert.AreEqual(2, catalogue.EnumerateSnapshots().Count);
    }

    [TestMethod]
    public async Task AgentPass_ABackupSetHasNoSchedule_ReportsManualOnlyRatherThanAnError()
    {
        await CreateRepositoryAsync();
        WriteConfiguration(schedule: null!);
        var result = await RunPassAsync(DateTimeOffset.UtcNow);
        Assert.AreEqual("manual-only", Assert.ContainsSingle(result.Sets).Outcome);
        Assert.IsEmpty(JobStateStore.Open(StateDirectory).Jobs);
    }

    [TestMethod]
    public async Task AgentPass_TwoSets_GetTwoIndependentStagingArchives()
    {
        // No CreateRepositoryAsync: staging archives are the service's own to
        // create, one per set on first backup (ADR-0034 §1, FR-DEST-002).
        var secondSource = Path.Combine(_root, "source-2");
        Directory.CreateDirectory(secondSource);
        File.WriteAllText(Path.Combine(secondSource, "p.txt"), "second set");

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [Vault],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = new string('a', 32), Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = SourceRoot }],
                    Schedule = "every 4h", Destinations = [VaultRef],
                },
                new BackupSetConfiguration
                {
                    Id = new string('f', 32), Name = "pics",
                    Roots = [new BackupRootConfiguration { Path = secondSource }],
                    Schedule = "every 4h", Destinations = [VaultRef],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));

        var result = await RunPassAsync(new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero));
        Assert.AreEqual(2, result.Ran, Outcomes(result));

        // Two archives on disk, each a complete repository with its own
        // identity — and therefore its own writer sequence and catalogue,
        // named by that identity.
        var identities = new List<string>();
        foreach (var setId in new[] { new string('a', 32), new string('f', 32) })
        {
            using var opened = await WriteOnlyInstallation.OpenAsync(
                new LocalFileSystemObjectStore(Path.Combine(ArchivesRoot, setId)), PassphraseText, CancellationToken.None);
            var identity = opened.Repository.RepositoryId.ToString();
            identities.Add(identity);

            Assert.IsTrue(File.Exists(Path.Combine(StateDirectory, $"sequence-{identity}.txt")));
            using var catalogue = CatalogueDb.Open(
                Path.Combine(StateDirectory, $"catalogue-{identity}.db"), opened.Repository.RepositoryId);
            var snapshot = Assert.ContainsSingle(catalogue.EnumerateSnapshots());
            Assert.IsTrue(snapshot.BackupSetId.Span.SequenceEqual(Convert.FromHexString(setId)));
        }

        Assert.AreNotEqual(identities[0], identities[1]);
    }

    private static async Task<HashSet<string>> KeysOfAsync(string storeRoot)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var store = new LocalFileSystemObjectStore(storeRoot);
        await foreach (var entry in store.ListAsync(
            Storage.Abstractions.ObjectPrefix.All, Storage.Abstractions.ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key.Value);
        }

        return keys;
    }

    [TestMethod]
    public async Task AgentPass_ALocalPathDestination_ReceivesACompleteReplica()
    {
        Directory.CreateDirectory(Vault.Path!);
        WriteConfiguration("every 4h");

        var result = await RunPassAsync(new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero));
        Assert.AreEqual(1, result.Ran, Outcomes(result));

        // The pair is in sync and says so durably (FR-DEST-004).
        var record = DestinationSyncStore.Open(StateDirectory).Find(new string('a', 32), "vault");
        Assert.IsNotNull(record);
        Assert.AreEqual(DestinationSyncState.InSync, record.State);

        // Byte-for-byte the same archive: every object key the staging
        // archive holds, the destination holds (FR-DEST-002). The replica
        // lands under the archive's repository id, like a peer's would.
        using var opened = await WriteOnlyInstallation.OpenAsync(
            new LocalFileSystemObjectStore(RepoPath), PassphraseText, CancellationToken.None);
        var repository = opened.Repository;
        var replicaRoot = Path.Combine(Vault.Path!, repository.RepositoryId.ToString());

        var stagingKeys = await KeysOfAsync(RepoPath);
        var replicaKeys = await KeysOfAsync(replicaRoot);
        Assert.IsTrue(stagingKeys.SetEquals(replicaKeys));
        Assert.IsNotEmpty(stagingKeys);

        // And it is a repository in its own right: it opens with nothing but
        // the path and the passphrase.
        using var replica = await WriteOnlyInstallation.OpenAsync(
            new LocalFileSystemObjectStore(replicaRoot), PassphraseText, CancellationToken.None);
        Assert.AreEqual(repository.RepositoryId, replica.Repository.RepositoryId);
    }

    [TestMethod]
    public async Task AgentPass_AnOfflineDestination_IsRecordedAndCatchesUpWhenItReturns()
    {
        // The vault's directory deliberately does not exist: an unplugged
        // drive, in this harness's terms.
        WriteConfiguration("every 4h");
        var now = new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);

        var first = await RunPassAsync(now);
        Assert.AreEqual(1, first.Ran);

        var offline = DestinationSyncStore.Open(StateDirectory).Find(new string('a', 32), "vault");
        Assert.IsNotNull(offline);
        Assert.AreEqual(DestinationSyncState.Unavailable, offline.State);
        Assert.IsNull(offline.LastSuccessAt);

        // The drive comes back; the next pass closes the gap with no command
        // issued (FR-DEST-003). No backup is due — this is pure catch-up.
        Directory.CreateDirectory(Vault.Path!);
        var second = await RunPassAsync(now.AddMinutes(30));
        Assert.AreEqual(0, second.Ran);

        var caughtUp = DestinationSyncStore.Open(StateDirectory).Find(new string('a', 32), "vault");
        Assert.IsNotNull(caughtUp);
        Assert.AreEqual(DestinationSyncState.InSync, caughtUp.State);
    }

    [TestMethod]
    public async Task AgentPass_TwoDestinations_BothConverge()
    {
        var second = Path.Combine(StateDirectory, "vault-2");
        Directory.CreateDirectory(Vault.Path!);
        Directory.CreateDirectory(second);

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                Vault,
                new DestinationConfiguration
                {
                    Id = new string('e', 32), Name = "vault-2",
                    Kind = DestinationKind.LocalPath, Path = second,
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = new string('a', 32), Name = "docs", Roots = [new BackupRootConfiguration { Path = SourceRoot }],
                    Schedule = "every 4h",
                    Destinations = [VaultRef, new SetDestinationReference { Ref = "vault-2" }],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));

        var result = await RunPassAsync(new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero));
        Assert.AreEqual(1, result.Ran, Outcomes(result));

        var ledger = DestinationSyncStore.Open(StateDirectory);
        Assert.AreEqual(DestinationSyncState.InSync, ledger.Find(new string('a', 32), "vault")!.State);
        Assert.AreEqual(DestinationSyncState.InSync, ledger.Find(new string('a', 32), "vault-2")!.State);

        var stagingKeys = await KeysOfAsync(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(
            new LocalFileSystemObjectStore(RepoPath), PassphraseText, CancellationToken.None);
        var repository = opened.Repository;
        foreach (var root in new[] { Vault.Path!, second })
        {
            var keys = await KeysOfAsync(Path.Combine(root, repository.RepositoryId.ToString()));
            Assert.IsTrue(stagingKeys.SetEquals(keys));
        }
    }

    [TestMethod]
    public async Task AgentPass_AMissingRootAndABadSchedule_AreClassifiedRecoverableAndPermanent()
    {
        await CreateRepositoryAsync();

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [Vault],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = new string('b', 32), Name = "gone",
                    Roots = [new BackupRootConfiguration { Path = Path.Combine(_root, "unmounted") }],
                    Schedule = "every 1h",
                    Destinations = [VaultRef],
                },
                new BackupSetConfiguration
                {
                    Id = new string('c', 32), Name = "typo",
                    Roots = [new BackupRootConfiguration { Path = SourceRoot }], Schedule = "hourly",
                    Destinations = [VaultRef],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));

        var result = await RunPassAsync(DateTimeOffset.UtcNow);
        Assert.AreEqual(2, result.Failed);

        // The classes differ because the user action differs (10 §3): an
        // unmounted drive resolves itself; a typo'd schedule needs a human.
        var jobs = JobStateStore.Open(StateDirectory);
        Assert.ContainsSingle(jobs.RecoverableFailures(new string('b', 32)));
        Assert.Contains(job =>
            job.BackupSetId == new string('c', 32) && job.State == JobState.FailedPermanent, jobs.Jobs);
    }

    [TestMethod]
    public async Task AgentPass_ADrillThePassStarted_IsFinishedBeforeItReturns()
    {
        // "Once, whole" has to include the drill phase. A drill still running
        // past the return is a drill whose commands are answered by a runtime
        // being torn down underneath it: they come back cancelled, and the
        // drill writes that into the state directory as a recovery failure —
        // after the caller believed the pass was over and, in a service, while
        // shutdown is deleting the things it is writing beside.
        Directory.CreateDirectory(Vault.Path!);
        WriteConfiguration("every 4h");

        // Enough content that the drill's restore is measurable work rather
        // than a handful of microseconds: the drill phase and the pass's own
        // return are both woken by the transfer phase finishing, so a drill
        // that costs nothing can win the race even while nothing waits for it.
        File.WriteAllBytes(
            Path.Combine(SourceRoot, "large.bin"),
            [.. Enumerable.Range(0, 24 * 1024 * 1024).Select(i => (byte)(i * 31))]);

        var result = await RunPassAsync(DateTimeOffset.UtcNow);
        Assert.AreEqual(1, result.Ran, Outcomes(result));
        Assert.IsTrue(result.Drills.IsCompleted, "the pass returned with a drill still running");

        // The observable that found this: the owner of the state directory
        // cannot delete it, because something it no longer knows about is
        // still writing into it.
        Directory.Delete(_root, recursive: true);
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.IsFalse(
            Directory.Exists(_root),
            "the state directory was written to after the pass returned");
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
