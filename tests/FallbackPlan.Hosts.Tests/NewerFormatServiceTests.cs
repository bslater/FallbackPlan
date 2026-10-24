using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A set a newer build upgraded past the newest format this build writes
/// (NFR-COMP-003; specification 11 §5.3; ADR-0066 Amendment 1). The service
/// still opens its archive and reads it, and writes nothing into it: the
/// backup is refused naming both versions, so are a person's retention apply
/// and deletion, the upgrade verb has nothing to move it to, and each
/// destination receives a whole copy with nothing converged away, because
/// what is live under a format this build has never seen is not this build's
/// to judge.
/// </summary>
/// <remarks>Does not establish FR-DRL-001: nothing here recovers content to a person.</remarks>
[TestClass]
public sealed class NewerFormatServiceTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.StateDirectory, "vault");

    private static string Newer => $"format {NewerBuildUpgrade.UnknownVersion}";

    private static string Latest => $"format {FormatLimits.LatestFormatVersion}";

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task Backup_OfASetANewerBuildUpgraded_IsRefusedNamingBothVersions_AndWritesNothing()
    {
        await BackUpTwiceAsync();
        await UpgradePastThisBuildAsync();
        var before = await KeysAsync(_harness.RepositoryPath);

        _harness.WriteSourceFile("docs/third.txt", "a change the refused backup must not carry");
        await using var runtime = await StartAsync();
        var set = runtime.Configuration.BackupSets.Single();
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);

        Assert.AreEqual("failed", outcome.Outcome);
        Assert.IsNotNull(outcome.Detail);
        Assert.Contains(Newer, outcome.Detail, StringComparison.Ordinal);
        Assert.Contains(Latest, outcome.Detail, StringComparison.Ordinal);
        Assert.AreEqual(
            JobState.FailedPermanent,
            runtime.Jobs.Jobs.Last(job => job.BackupSetId == set.Id).State,
            "nothing but a newer build can make this backup run, so retrying it is not the answer");

        CollectionAssert.AreEquivalent(
            before, await KeysAsync(_harness.RepositoryPath), "the refused backup wrote into the repository");
    }

    [TestMethod]
    public async Task Archive_OfASetANewerBuildUpgraded_StillOpensAndSaysWhyItIsNotWritten()
    {
        await BackUpTwiceAsync();
        await UpgradePastThisBuildAsync();

        await using var runtime = await StartAsync();
        var set = runtime.Configuration.BackupSets.Single();
        var archive = await runtime.ExistingArchiveAsync(set.Id, Timeout);

        Assert.IsNotNull(archive, "the archive of a newer set must still open: everything that reads it depends on that");
        Assert.AreEqual(NewerBuildUpgrade.UnknownVersion, archive.Repository.EffectiveFormatVersion);
        Assert.IsFalse(archive.Repository.WritableByThisBuild);

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        Assert.HasCount(2, listed.Snapshots);

        var notice = Assert.ContainsSingle(
            runtime.Notices.Unacknowledged.Where(notice => notice.Key == $"format-newer:{set.Id}"),
            "a set this build will not write raised no notice saying so");
        Assert.Contains(Newer, notice.Message, StringComparison.Ordinal);
        Assert.Contains(Latest, notice.Message, StringComparison.Ordinal);
        Assert.IsFalse(
            runtime.Notices.Unacknowledged.Any(notice => notice.Key == $"format-upgradable:{set.Id}"),
            "a set above this build's format has nothing to upgrade to");
    }

    [TestMethod]
    public async Task RetentionApply_OfASetANewerBuildUpgraded_IsRefusedByName()
    {
        await BackUpTwiceAsync();
        await UpgradePastThisBuildAsync();
        var before = await KeysAsync(_harness.RepositoryPath);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var grant = await _harness.ReclaimGrantAsync(handler, Timeout);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout), out var refused);
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains(Newer, refused.Message, StringComparison.Ordinal);

        CollectionAssert.AreEquivalent(
            before, await KeysAsync(_harness.RepositoryPath), "the refused apply wrote into the repository");
    }

    [TestMethod]
    public async Task DeleteSnapshots_OfASetANewerBuildUpgraded_IsRefusedByName()
    {
        await BackUpTwiceAsync();
        await UpgradePastThisBuildAsync();
        var before = await KeysAsync(_harness.RepositoryPath);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var grant = await _harness.ReclaimGrantAsync(handler, Timeout);
        var set = runtime.Configuration.BackupSets.Single();
        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var oldest = listed.Snapshots.OrderBy(snapshot => snapshot.CapturedAt).First().SnapshotId;

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new DeleteSnapshotsCommand(set.Id, [oldest], Apply: true, ReclaimGrant: grant), Timeout),
            out var refused);
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains(Newer, refused.Message, StringComparison.Ordinal);

        CollectionAssert.AreEquivalent(
            before, await KeysAsync(_harness.RepositoryPath), "the refused deletion wrote into the repository");
    }

    [TestMethod]
    public async Task Upgrade_OfASetANewerBuildUpgraded_IsRefusedNamingBothVersions()
    {
        await BackUpTwiceAsync();
        await UpgradePastThisBuildAsync();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new UpgradeSetFormatCommand("docs"), Timeout), out var refused);
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains(Newer, refused.Message, StringComparison.Ordinal);
        Assert.Contains(Latest, refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Sync_OfASetThisBuildWrites_ConvergesTheNarrowDestinationToItsKeepSet()
    {
        // The control for the test below: the same two backups and the same
        // narrow policy, at a format this build writes, leave one snapshot at
        // the destination. Without it, a whole copy below could be a policy
        // that never dropped anything.
        await BackUpTwiceAsync();

        await SyncAsync();

        var replica = Assert.ContainsSingle(Directory.GetDirectories(Vault));
        Assert.HasCount(1, await KeysAsync(replica, "snapshots/"));
    }

    [TestMethod]
    public async Task Sync_OfASetANewerBuildUpgraded_CopiesWholeAndConvergesNothingAway()
    {
        await BackUpTwiceAsync();
        await UpgradePastThisBuildAsync();

        await using var runtime = await StartAsync();
        var set = runtime.Configuration.BackupSets.Single();
        await Task.WhenAll(FanOut.EnqueueAll(runtime, set, DateTimeOffset.Now, userInitiated: true)).WaitAsync(Timeout);

        var replica = Assert.ContainsSingle(Directory.GetDirectories(Vault));
        Assert.HasCount(
            2,
            await KeysAsync(replica, "snapshots/"),
            "the destination was converged under a format this build cannot judge liveness in");
        Assert.Contains(
            ObjectKeyFor(NewerBuildUpgrade.UnknownVersion),
            await KeysAsync(replica, "format-upgrade/"),
            "the newer build's record is an ordinary object, and copying it is a read of the source");

        var notice = Assert.ContainsSingle(
            runtime.Notices.Unacknowledged.Where(notice => notice.Key == $"convergence-unavailable:{set.Id}:vault"),
            "a destination given a whole copy instead of its keep-set must be told why");
        Assert.Contains(Newer, notice.Message, StringComparison.Ordinal);
    }

    private static string ObjectKeyFor(ushort version) =>
        Repository.Format.Lifecycle.FormatUpgradeRecordCodec.KeyFor(version);

    /// <summary>
    /// Two backups of the set at the format this build writes, with a change
    /// between them, under a destination policy that keeps one of them.
    /// </summary>
    private async Task BackUpTwiceAsync()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/first.txt", "the first backup's words");

        await using var runtime = await StartAsync();
        var set = runtime.Configuration.BackupSets.Single();
        Assert.AreEqual(
            "ran", (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout)).Outcome);
        _harness.WriteSourceFile("docs/second.txt", "the second backup's words");
        Assert.AreEqual(
            "ran", (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout)).Outcome);
    }

    /// <summary>What a newer build leaves in the set's staging archive when it upgrades it.</summary>
    private async Task UpgradePastThisBuildAsync()
    {
        using var provisioning = new InstallationCredentialStore(_harness.StateDirectory).TryLoad();
        Assert.IsNotNull(provisioning, "the installation is not set up");
        await NewerBuildUpgrade.PlantAsync(
            new LocalFileSystemObjectStore(_harness.RepositoryPath), provisioning.Credential,
            FormatLimits.LatestFormatVersion, Timeout);
    }

    private async Task SyncAsync()
    {
        await using var runtime = await StartAsync();
        var set = runtime.Configuration.BackupSets.Single();
        await Task.WhenAll(FanOut.EnqueueAll(runtime, set, DateTimeOffset.Now, userInitiated: true)).WaitAsync(Timeout);
    }

    private async Task<List<string>> KeysAsync(string root, string prefix = "")
    {
        var store = new LocalFileSystemObjectStore(root);
        var keys = new List<string>();
        await foreach (var entry in store.ListAsync(
            prefix.Length == 0 ? ObjectPrefix.All : ObjectPrefix.Parse(prefix), ListOptions.Default, Timeout))
        {
            keys.Add(entry.Key.Value);
        }

        return keys;
    }

    private void WriteConfiguration() => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('d', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = _harness.DocsSetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                Schedule = "every 4h",
                Destinations =
                [
                    new SetDestinationReference
                    {
                        Ref = "vault",
                        Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
                    },
                ],
            },
        ],
    }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                // The placement condition (ADR-0051) judges by volume, and the
                // fixture's every path shares one real volume — the vault is
                // told apart by name, the compliant install's shape.
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
    }
}
