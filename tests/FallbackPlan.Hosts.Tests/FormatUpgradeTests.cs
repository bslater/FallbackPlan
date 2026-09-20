using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using System.Globalization;
using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Recovery;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Lifecycle;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The format-upgrade record reaches the copies
/// (specification 11 §4; FR-REP-002, NFR-COMP-004). The descriptor cannot
/// carry an upgrade: <c>DestinationShipSink</c> seeds it only if absent, a
/// peer commits an object it lacks and keeps the one it has, and
/// <c>repository-format</c> may not be deleted by instruction — so a
/// rewritten descriptor would move the source alone and leave every copy
/// claiming the older format over newer blobs. An append-only record needs
/// none of that: it is an ordinary immutable object, which is the one thing
/// every copy path already moves.
/// </summary>
/// <remarks>Does not establish FR-DRL-001: nothing here recovers content to a person.</remarks>
[TestClass]
[DoNotParallelize]
public sealed class FormatUpgradeTests : IDisposable
{
    private static readonly byte[] WriterId = [.. Enumerable.Repeat((byte)23, 16)];

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string MetadataRoot => Path.Combine(_harness.StateDirectory, "sets", _harness.DocsSetId);

    public void Dispose()
    {
        ServiceRuntime.ArchiveFormatVersion = FormatLimits.FormatVersion;
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task AnUpgradeRecord_WrittenAtTheSource_ReachesALocalPathDestinationOnTheNextOrdinaryPass()
    {
        // Created at format 2, which is where every repository written before
        // the creation default moved sits, and the only shape an upgrade has
        // anything to say about.
        ServiceRuntime.ArchiveFormatVersion = FormatVersions.SealedDataPlane;
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/report.txt", "the words worth keeping");

        await using (var runtime = await StartAsync())
        {
            var set = runtime.Configuration.BackupSets.Single();
            var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)
                .WaitAsync(Timeout);
            Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
        }

        var replica = Assert.ContainsSingle(Directory.GetDirectories(Vault));
        var upgradeKey = FormatUpgradeRecordCodec.KeyFor(FormatVersions.RelocatableRecords);
        Assert.IsFalse(
            File.Exists(Path.Combine(replica, upgradeKey.Replace('/', Path.DirectorySeparatorChar))),
            "a repository nobody upgraded carries no upgrade record");

        await WriteUpgradeAsync(FormatVersions.RelocatableRecords);

        // No protocol change, no new accept list, no seeding step: the next
        // ordinary reconciliation carries it because the copier's catch-all
        // phase claims every key no named phase does.
        await using (var runtime = await StartAsync())
        {
            var queued = FanOut.EnqueueAll(
                runtime, runtime.Configuration.BackupSets.Single(), DateTimeOffset.Now, userInitiated: true);
            await Task.WhenAll(queued).WaitAsync(Timeout);
        }

        var store = new LocalFileSystemObjectStore(replica);
        using var result = await store.OpenReadAsync(ObjectKey.Parse(upgradeKey), range: null, Timeout);
        Assert.AreEqual(OpenReadOutcome.Found, result.Outcome, "the upgrade record never reached the destination");

        using var memory = new MemoryStream();
        await result.Content!.CopyToAsync(memory, Timeout);
        var decoded = FormatUpgradeRecordCodec.Decode(memory.ToArray());
        Assert.AreEqual(FormatVersions.RelocatableRecords, decoded.Value.ToVersion);

        // And the copy is what a reader opening the destination alone would
        // act on: the replica's own effective version, read as any recovery
        // would read it, is the upgraded one.
        using var passphrase = Passphrase.Create(Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);
        var (repository, authority) = await RepositoryLifecycle.OpenForReadAsync(store, passphrase, Timeout);
        using (repository)
        using (authority)
        {
            Assert.AreEqual(
                FormatVersions.RelocatableRecords,
                await RepositoryLifecycle.ReadEffectiveFormatAsync(
                    store, repository.Descriptor, repository.Credential, Timeout));
        }
    }

    [TestMethod]
    public async Task ASetUpgradedBetweenBackups_SealsTheNewerFormatAndStillRestoresTheOlder()
    {
        // The whole point of the record, through the real service. The
        // descriptor says 2 for ever — no destination would accept a
        // replacement — so the run's version has to come from the effective
        // read, and the proof is a destination holding blobs of both stamps
        // that the recovery tool restores whole.
        ServiceRuntime.ArchiveFormatVersion = FormatVersions.SealedDataPlane;
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/before.txt", "written while this set was at format 2");
        await BackUpAsync();

        var replica = Assert.ContainsSingle(Directory.GetDirectories(Vault));
        var before = await DataBlobVersionsAsync(replica);
        Assert.IsNotEmpty(before);
        Assert.IsTrue(
            before.All(version => version == FormatVersions.SealedDataPlane),
            "the set wrote something other than format 2 before it was upgraded");

        await WriteUpgradeAsync(FormatVersions.RelocatableRecords);

        _harness.WriteSourceFile("docs/after.txt", "written after the upgrade, and bigger: " + new string('a', 200_000));
        await BackUpAsync();

        // Both stamps at the destination, and the older blobs are untouched:
        // an upgrade rewrites nothing that is already sealed.
        var after = await DataBlobVersionsAsync(replica);
        Assert.HasCount(
            before.Count,
            after.Where(version => version == FormatVersions.SealedDataPlane).ToList());
        Assert.IsNotEmpty(after.Where(version => version == FormatVersions.RelocatableRecords).ToList());

        // The descriptor at the destination still says what it always said.
        var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(
            new LocalFileSystemObjectStore(replica), Timeout);
        Assert.AreEqual(FormatVersions.SealedDataPlane, descriptor.FormatVersion);

        // The window, pinned rather than hidden. A capture ships the blobs it
        // wrote; the upgrade record is an ordinary immutable object that no
        // capture produced, so it rides the next reconciling pass. Until then
        // the destination holds format-3 blobs and does not yet hold the
        // record that explains them — which costs nothing, because every
        // blob declares its own container, and is the honest thing for the
        // tool to report while it is true.
        var beforeConverging = await RunRecoveryAsync(
            "open", "--repo", replica, "--passphrase-env", _harness.PassphraseVariable);
        Assert.AreEqual(0, beforeConverging.ExitCode, beforeConverging.Error);
        Assert.Contains("format         2", beforeConverging.Output, StringComparison.Ordinal);

        await SyncAsync();

        // And after the ordinary pass the tool a person reaches for reports
        // what the repository writes, saying what it was created at rather
        // than replacing it — a line that said 2 over format-3 blobs would be
        // the worst kind of wrong, because it would be read at the worst
        // possible moment.
        var opened = await RunRecoveryAsync(
            "open", "--repo", replica, "--passphrase-env", _harness.PassphraseVariable);
        Assert.AreEqual(0, opened.ExitCode, opened.Error);
        Assert.Contains("format         3 (created at 2)", opened.Output, StringComparison.Ordinal);

        var listing = await RunRecoveryAsync(
            "snapshots", "--repo", replica, "--passphrase-env", _harness.PassphraseVariable);
        Assert.AreEqual(0, listing.ExitCode, listing.Error);
        var snapshots = listing.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
            .ToList();
        Assert.HasCount(2, snapshots);

        // The newest snapshot spans records sealed under two constructions;
        // the reader dispatches on the envelope it finds, so both come back.
        var newest = snapshots[0];
        var into = Path.Combine(_harness.WorkPath, "recovered");
        var restore = await RunRecoveryAsync(
            "restore", "--repo", replica, "--passphrase-env", _harness.PassphraseVariable,
            "--snapshot", newest, "--output", into);
        Assert.AreEqual(0, restore.ExitCode, restore.Error);

        foreach (var name in new[] { "before.txt", "after.txt" })
        {
            var recovered = Path.Combine(into, "docs", name);
            Assert.IsTrue(File.Exists(recovered), $"{name} did not come back");
            Assert.AreEqual(
                await File.ReadAllTextAsync(Path.Combine(_harness.SourceRoot, "docs", name), Timeout),
                await File.ReadAllTextAsync(recovered, Timeout));
        }
    }

    [TestMethod]
    public async Task AnUpgrade_CommandedWhileTheServiceRuns_SealsTheNewerFormatOnTheVeryNextBackup()
    {
        // The effective version is fixed when the archive opens and the
        // runtime caches one handle per set, so a verb that wrote the record
        // and left the handle where it was would do nothing at all until the
        // next restart — while telling the person it had worked. One runtime,
        // no restart, both backups.
        ServiceRuntime.ArchiveFormatVersion = FormatVersions.SealedDataPlane;
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/before.txt", "written while this set was at format 2");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var set = runtime.Configuration.BackupSets.Single();

        await RunOnAsync(runtime, set);
        var replica = Assert.ContainsSingle(Directory.GetDirectories(Vault));
        Assert.IsTrue(
            (await DataBlobVersionsAsync(replica)).All(version => version == FormatVersions.SealedDataPlane),
            "the set wrote something other than format 2 before it was upgraded");

        // The set below the latest raised the notice at open, which is the
        // only way a person learns there is anything to do.
        Assert.IsTrue(
            runtime.Notices.Unacknowledged.Any(notice =>
                notice.Key == $"format-upgradable:{set.Id}"),
            "an upgradable set raised no notice");

        var result = await handler.ExecuteAsync(new UpgradeSetFormatCommand("docs"), Timeout);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(result, out var change);
        Assert.Contains(
            FormatLimits.FormatVersion.ToString(CultureInfo.InvariantCulture),
            string.Join('\n', change.Lines),
            StringComparison.Ordinal);
        Assert.IsFalse(
            runtime.Notices.Unacknowledged.Any(notice => notice.Key == $"format-upgradable:{set.Id}"),
            "the notice outlived the act it asked for");

        _harness.WriteSourceFile(
            "docs/after.txt", "written after the upgrade, and bigger: " + new string('a', 200_000));
        await RunOnAsync(runtime, set);

        var after = await DataBlobVersionsAsync(replica);
        Assert.IsNotEmpty(
            after.Where(version => version == FormatVersions.RelocatableRecords).ToList(),
            "the next backup still sealed the older format, so the upgrade reached nothing");
    }

    [TestMethod]
    public async Task AnUpgrade_WithADestinationThatRefusesTheWrite_IsStillRecordedLocally()
    {
        // The record goes to the set's own store, never through the ship
        // sink. Outside a run the sink holds no destinations in scope, so
        // when one it resolved fresh then fails the write it finds nothing
        // left and throws — after the local copy has already landed, which
        // would report a completed upgrade as a failure. An upgrade must not
        // turn on whether a drive happens to be writable; propagation is the
        // next reconciling pass's business.
        ServiceRuntime.ArchiveFormatVersion = FormatVersions.SealedDataPlane;
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/report.txt", "the words worth keeping");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunOnAsync(runtime, runtime.Configuration.BackupSets.Single());

        // Present and reachable, and the one key this verb writes cannot be
        // created there: a file stands where its directory would go.
        var replica = Assert.ContainsSingle(Directory.GetDirectories(Vault));
        await File.WriteAllTextAsync(
            Path.Combine(replica, FormatUpgradeRecordCodec.KeyPrefix.TrimEnd('/')), "in the way", Timeout);

        var result = await handler.ExecuteAsync(new UpgradeSetFormatCommand("docs"), Timeout);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(result, out _);

        var upgradeKey = FormatUpgradeRecordCodec.KeyFor(FormatLimits.FormatVersion);
        Assert.IsTrue(
            File.Exists(Path.Combine(MetadataRoot, upgradeKey.Replace('/', Path.DirectorySeparatorChar))),
            "the upgrade record did not reach the set's own store");
    }

    [TestMethod]
    public async Task AnUpgrade_OfASetAlreadyAtTheLatest_IsRefusedByNameAndWritesNothing()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/report.txt", "the words worth keeping");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunOnAsync(runtime, runtime.Configuration.BackupSets.Single());

        var result = await handler.ExecuteAsync(new UpgradeSetFormatCommand("docs"), Timeout);

        Assert.IsInstanceOfType<ServiceError>(result, out var error);
        Assert.AreEqual(ServiceErrorReason.Refused, error.Reason);
        Assert.Contains(
            FormatLimits.FormatVersion.ToString(CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);
        Assert.IsEmpty(
            Directory.GetDirectories(MetadataRoot, "format-upgrade", SearchOption.AllDirectories),
            "a refused upgrade still wrote a record");
    }

    [TestMethod]
    public async Task AnUpgrade_OfASetWithNoArchiveYet_IsRefusedByName()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var result = await handler.ExecuteAsync(new UpgradeSetFormatCommand("docs"), Timeout);

        Assert.IsInstanceOfType<ServiceError>(result, out var error);
        Assert.AreEqual(ServiceErrorReason.Refused, error.Reason);
        Assert.Contains("no archive", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task AnUpgrade_OfASetThatIsNotConfigured_IsNotFound()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var result = await handler.ExecuteAsync(new UpgradeSetFormatCommand("photos"), Timeout);

        Assert.IsInstanceOfType<ServiceError>(result, out var error);
        Assert.AreEqual(ServiceErrorReason.NotFound, error.Reason);
        Assert.Contains("photos", error.Message, StringComparison.Ordinal);
    }

    private async Task SyncAsync()
    {
        await using var runtime = await StartAsync();
        var queued = FanOut.EnqueueAll(
            runtime, runtime.Configuration.BackupSets.Single(), DateTimeOffset.Now, userInitiated: true);
        await Task.WhenAll(queued).WaitAsync(Timeout);
    }

    private async Task BackUpAsync()
    {
        await using var runtime = await StartAsync();
        await RunOnAsync(runtime, runtime.Configuration.BackupSets.Single());
    }

    private async Task RunOnAsync(ServiceRuntime runtime, BackupSetConfiguration set)
    {
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)
            .WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    /// <summary>The stamped container version of every data blob at a replica, read off the disk.</summary>
    private async Task<List<ushort>> DataBlobVersionsAsync(string replica)
    {
        var store = new LocalFileSystemObjectStore(replica);
        var versions = new List<ushort>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("blobs/data/"), ListOptions.Default, Timeout))
        {
            using var read = await store.OpenReadAsync(
                entry.Key, new ObjectRange(0, BlobEnvelope.MaxLength), Timeout);
            Assert.AreEqual(OpenReadOutcome.Found, read.Outcome);

            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory, Timeout);
            versions.Add(BlobEnvelope.Parse(memory.ToArray()).FormatVersion);
        }

        return versions;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunRecoveryAsync(params string[] args)
    {
        var output = new StringWriter(new StringBuilder(), CultureInfo.InvariantCulture);
        var error = new StringWriter(new StringBuilder(), CultureInfo.InvariantCulture);
        var exit = await RecoveryHost.RunAsync(args, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task WriteUpgradeAsync(ushort toVersion)
    {
        var store = new LocalFileSystemObjectStore(MetadataRoot);
        using var passphrase = Passphrase.Create(Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);
        var (repository, authority) = await RepositoryLifecycle.OpenForReadAsync(store, passphrase, Timeout);
        using (repository)
        using (authority)
        {
            await RepositoryLifecycle.WriteFormatUpgradeAsync(
                store, repository.Descriptor, repository.Credential, toVersion, WriterId,
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Timeout);
        }
    }

    private void WriteConfiguration() => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
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
                Destinations = [new SetDestinationReference { Ref = "vault" }],
                DirectShip = true,
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
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
    }
}
