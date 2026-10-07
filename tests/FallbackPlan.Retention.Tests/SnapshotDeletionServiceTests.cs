using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// The <c>delete_snapshots</c> command (FR-GC-013, contract 1.51, ADR-0080):
/// a person names snapshots of one set, a dry run says what would go and where
/// it is held, and an apply under the set's reclaim grant requests the
/// deletion, brings every reachable copy in line, and takes the snapshots and
/// what only they held out of staging. A copy that cannot be reached holds the
/// deletion, and the listing says which. Nothing else the set's policy would
/// expire is touched, the last complete snapshot is never deleted, and an id
/// the set does not hold is refused by name before anything is written. And
/// it reads and writes the set's catalogue on a connection of its own, since
/// the set's backup may be writing through the archive's while it runs
/// (FR-SVC-012).
/// </summary>
[TestClass]
public sealed class SnapshotDeletionServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-deletion-service", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "deletion-service-passphrase!!";
    private static readonly string SetId = new('e', 32);
    private static readonly DateTimeOffset Day1 = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    private string ArchivesRoot => Path.Combine(_root, "archives");
    private string RepoPath => Path.Combine(ArchivesRoot, SetId);
    private string StateDirectory => Path.Combine(_root, "state");
    private string SourceRoot => Path.Combine(_root, "source");
    private string VaultPath => Path.Combine(_root, "vault");
    private string SparePath => Path.Combine(_root, "spare");

    public SnapshotDeletionServiceTests()
    {
        Directory.CreateDirectory(StateDirectory);
        WriteOnlyInstallation.Provision(StateDirectory, PassphraseText);
        Directory.CreateDirectory(SourceRoot);
        Directory.CreateDirectory(VaultPath);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day one content");
        WriteConfiguration(retention: null);
    }

    [TestMethod]
    public async Task DryRun_SaysWhatWouldGoAndWhereItIsHeld_AndWritesNothing()
    {
        var snapshots = await BackUpThreeAsync();

        var result = await DeleteAsync([snapshots[1]], apply: false);

        Assert.IsFalse(result.Applied);
        var outcome = Assert.ContainsSingle(result.Snapshots);
        Assert.AreEqual(snapshots[1], outcome.SnapshotId);
        Assert.AreEqual("would-delete", outcome.State);
        Assert.AreEqual("vault", Assert.ContainsSingle(outcome.Awaiting));

        Assert.IsEmpty(await ListAsync(Staging, "tombstones/"));
        Assert.HasCount(3, await ListAsync(Staging, "snapshots/"));
        Assert.HasCount(3, await ListAsync(Vault, "snapshots/"));
        Assert.IsFalse((await JournalAsync()).Any(record => record.Kind == JournalRecordKind.Audit));
    }

    [TestMethod]
    public async Task Apply_TakesTheSnapshotFromStagingAndTheVault_AndWhatOnlyItHeld()
    {
        await BackUpAsync(Day1);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "what should never have been backed up");
        await BackUpAsync(Day1.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
        await BackUpAsync(Day1.AddDays(2));
        var snapshots = await ListedAsync();
        var blobsBefore = (await ListAsync(Staging, "blobs/")).Count;

        var result = await DeleteAsync([snapshots[1]], apply: true);

        Assert.IsTrue(result.Applied);
        Assert.AreEqual("deleted", Assert.ContainsSingle(result.Snapshots).State);

        // Gone from staging and from the vault, and the archive left in both
        // places walks clean.
        Assert.HasCount(2, await ListAsync(Staging, "snapshots/"));
        Assert.HasCount(2, await ListAsync(Vault, "snapshots/"));
        CollectionAssert.AreEquivalent(new[] { snapshots[0], snapshots[2] }, (await ListedAsync()).ToArray());

        // What only that snapshot held went with it, in the same command,
        // rather than waiting for a retention run nobody may ever start.
        Assert.IsLessThan(blobsBefore, (await ListAsync(Staging, "blobs/")).Count);
        Assert.IsEmpty(await ListAsync(Staging, "tombstones/04/"));

        // The journal says who asked and which snapshot.
        var audit = (await JournalAsync()).Single(record =>
            record.Payload is JournalPayload.Audit { Action: AuditAction.BulkSnapshotDeletion });
        var payload = (JournalPayload.Audit)audit.Payload;
        Assert.AreEqual("cli", payload.Actor);
        Assert.AreEqual(snapshots[1], Convert.ToHexStringLower(Assert.ContainsSingle(payload.Snapshots).Span));
    }

    [TestMethod]
    public async Task Apply_ASnapshotThePolicyKeeps_Goes_AndNothingThePolicyWouldExpireIsTouched()
    {
        // A policy that would expire the two older captures. The command
        // deletes what was named and nothing the policy would expire: that is
        // a retention run's to do, when someone asks for one.
        WriteConfiguration(retention: new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 });
        var snapshots = await BackUpThreeAsync();

        var result = await DeleteAsync([snapshots[2]], apply: true);

        Assert.AreEqual("deleted", Assert.ContainsSingle(result.Snapshots).State);
        CollectionAssert.AreEquivalent(new[] { snapshots[0], snapshots[1] }, (await ListedAsync()).ToArray());
        Assert.HasCount(2, await ListAsync(Staging, "snapshots/"));
    }

    [TestMethod]
    public async Task Apply_ADestinationThatCannotBeReached_HoldsTheDeletion_AndTheListingSaysWhere()
    {
        var snapshots = await BackUpThreeAsync();
        var detached = VaultPath + ".detached";
        Directory.Move(VaultPath, detached);

        var held = await DeleteAsync([snapshots[1]], apply: true);

        var outcome = Assert.ContainsSingle(held.Snapshots);
        Assert.AreEqual("pending", outcome.State);
        Assert.AreEqual("vault", Assert.ContainsSingle(outcome.Awaiting));
        Assert.HasCount(3, await ListAsync(Staging, "snapshots/"));
        var listed = (await ListSnapshotsAsync()).Single(snapshot => snapshot.SnapshotId == snapshots[1]);
        Assert.AreEqual("vault", Assert.ContainsSingle(listed.DeletionPending!));
        Assert.IsTrue((await ListSnapshotsAsync())
            .Where(snapshot => snapshot.SnapshotId != snapshots[1])
            .All(snapshot => snapshot.DeletionPending is null));

        // The drive comes back, and asking again finishes what was started:
        // the request stands, so this is a resume rather than a second request.
        Directory.Move(detached, VaultPath);
        var finished = await DeleteAsync([snapshots[1]], apply: true);

        Assert.AreEqual("deleted", Assert.ContainsSingle(finished.Snapshots).State);
        Assert.HasCount(2, await ListAsync(Staging, "snapshots/"));
        Assert.HasCount(2, await ListAsync(Vault, "snapshots/"));
        Assert.ContainsSingle((await JournalAsync()).Where(record =>
            record.Payload is JournalPayload.Audit { Action: AuditAction.BulkSnapshotDeletion }));
    }

    [TestMethod]
    public async Task Apply_ADestinationNoCopyHasReached_HoldsTheDeletionLikeAnyOther()
    {
        // A drive declared and never once plugged in. Its ledger row says only
        // that it was tried, and the ledger cannot vouch for what a drive
        // holds: a renamed destination starts a new row, and a ledger that
        // could not be read starts empty. So it holds the deletion, named, while
        // the copy that was reached lets the snapshot go.
        var snapshots = await BackUpThreeAsync();
        WriteConfiguration(retention: null, withSpare: true);

        var held = await DeleteAsync([snapshots[1]], apply: true);

        var outcome = Assert.ContainsSingle(held.Snapshots);
        Assert.AreEqual("pending", outcome.State);
        Assert.AreEqual("spare", Assert.ContainsSingle(outcome.Awaiting));
        Assert.HasCount(3, await ListAsync(Staging, "snapshots/"));
        Assert.HasCount(2, await ListAsync(Vault, "snapshots/"));

        // Plugged in at last, it converges, and asking again finishes it.
        Directory.CreateDirectory(SparePath);
        var finished = await DeleteAsync([snapshots[1]], apply: true);

        Assert.AreEqual("deleted", Assert.ContainsSingle(finished.Snapshots).State);
        Assert.HasCount(2, await ListAsync(Staging, "snapshots/"));
        Assert.DoesNotContain(
            snapshots[1], (await ListSnapshotsAsync()).Select(snapshot => snapshot.SnapshotId).ToList());
    }

    [TestMethod]
    public async Task Apply_EveryCompleteSnapshot_IsRefusedAndNothingIsWritten()
    {
        // A request that would leave the set with no complete snapshot is
        // refused, whichever way it is put together.
        var snapshots = await BackUpThreeAsync();

        var refused = await DeleteRefusedAsync([.. snapshots], apply: true);

        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains("complete snapshot", refused.Message, StringComparison.Ordinal);
        Assert.IsEmpty(await ListAsync(Staging, "tombstones/"));
        Assert.HasCount(3, await ListAsync(Staging, "snapshots/"));
    }

    [TestMethod]
    public async Task Apply_TheLastCompleteSnapshotAfterAnEarlierRequest_IsRefused()
    {
        // Two requests, each leaving one standing, together would leave none:
        // a pending request counts as gone.
        var snapshots = await BackUpThreeAsync();
        Directory.Move(VaultPath, VaultPath + ".detached");
        var first = await DeleteAsync([snapshots[0], snapshots[1]], apply: true);
        Assert.IsTrue(first.Snapshots.All(outcome => outcome.State == "pending"));

        var refused = await DeleteRefusedAsync([snapshots[2]], apply: true);

        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains("complete snapshot", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Apply_ASnapshotTheSetDoesNotHold_IsRefusedByNameAndNothingIsWritten()
    {
        var snapshots = await BackUpThreeAsync();
        var stranger = new string('7', 64);

        var refused = await DeleteRefusedAsync([snapshots[0], stranger], apply: true);

        Assert.AreEqual(ServiceErrorReason.NotFound, refused.Reason);
        Assert.Contains(stranger, refused.Message, StringComparison.Ordinal);
        Assert.IsEmpty(await ListAsync(Staging, "tombstones/"));
    }

    [TestMethod]
    public async Task Apply_ReadsAndWritesTheCatalogueOnAConnectionOfItsOwn_NotTheBackups()
    {
        var snapshots = await BackUpThreeAsync();
        string cataloguePath;
        Domain.Identifiers.RepositoryId repositoryId;

        await using (var runtime = await StartAsync())
        {
            // The connection the set's backup reads and writes through. A
            // deletion runs on the writer pool, whose other worker can be
            // running that backup. Sharing the connection fails only when the
            // two threads meet, which is rarely; closed, any use of it fails
            // at once.
            var archive = await runtime.ArchiveForAsync(runtime.Configuration.BackupSets.Single(), CancellationToken.None);
            cataloguePath = archive.CataloguePath;
            repositoryId = archive.Repository.RepositoryId;
            archive.Catalogue.Dispose();

            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            var description = (ServiceDescriptionResult)await handler.ExecuteAsync(
                new DescribeServiceCommand(), CancellationToken.None);
            var result = await handler.ExecuteAsync(
                new DeleteSnapshotsCommand(
                    SetId, [snapshots[1]], Apply: true,
                    WriteOnlyInstallation.ReclaimGrant(StateDirectory, PassphraseText, description.RestoreGrantRecipient!)),
                CancellationToken.None);

            Assert.IsInstanceOfType<DeleteSnapshotsResult>(
                result, out var deleted, (result as ServiceError)?.Message ?? result.GetType().Name);
            Assert.AreEqual("deleted", Assert.ContainsSingle(deleted.Snapshots).State);
        }

        // What the deletion wrote is the set's catalogue: the snapshot it took
        // is gone from the file the backup reads.
        using var catalogue = CatalogueDb.Open(cataloguePath, repositoryId);
        CollectionAssert.AreEquivalent(
            new[] { snapshots[0], snapshots[2] },
            catalogue.EnumerateSnapshots().Select(row => Convert.ToHexStringLower(row.SnapshotId.Span)).ToArray());
    }

    [TestMethod]
    public async Task Apply_WithoutAGrant_IsRefusedByName_AndADryRunNeedsNone()
    {
        var snapshots = await BackUpThreeAsync();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var refused = await handler.ExecuteAsync(
            new DeleteSnapshotsCommand(SetId, [snapshots[1]], Apply: true), CancellationToken.None);

        Assert.IsInstanceOfType<ServiceError>(refused, out var error);
        Assert.Contains("reclaim grant", error.Message, StringComparison.Ordinal);
        Assert.IsEmpty(await ListAsync(Staging, "tombstones/"));
    }

    [TestMethod]
    public async Task Apply_AnUnknownSet_IsRefusedByName()
    {
        await BackUpThreeAsync();
        var unknown = new string('f', 32);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var refused = await handler.ExecuteAsync(
            new DeleteSnapshotsCommand(unknown, [new string('5', 64)]), CancellationToken.None);

        Assert.IsInstanceOfType<ServiceError>(refused, out var error);
        Assert.AreEqual(ServiceErrorReason.NotFound, error.Reason);
        Assert.Contains(unknown, error.Message, StringComparison.Ordinal);
    }

    private LocalFileSystemObjectStore Staging => new(RepoPath);

    private LocalFileSystemObjectStore Vault => new(Directory.GetDirectories(VaultPath).Single());

    private async Task<DeleteSnapshotsResult> DeleteAsync(IReadOnlyList<string> snapshotIds, bool apply)
    {
        var result = await ExecuteDeleteAsync(snapshotIds, apply);
        Assert.IsInstanceOfType<DeleteSnapshotsResult>(
            result, (result as ServiceError)?.Message ?? result.GetType().Name);
        return (DeleteSnapshotsResult)result;
    }

    private async Task<ServiceError> DeleteRefusedAsync(IReadOnlyList<string> snapshotIds, bool apply)
    {
        var result = await ExecuteDeleteAsync(snapshotIds, apply);
        Assert.IsInstanceOfType<ServiceError>(result, result.GetType().Name);
        return (ServiceError)result;
    }

    private async Task<ServiceResult> ExecuteDeleteAsync(IReadOnlyList<string> snapshotIds, bool apply)
    {
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var description = (ServiceDescriptionResult)await handler.ExecuteAsync(
            new DescribeServiceCommand(), CancellationToken.None);

        return await handler.ExecuteAsync(
            new DeleteSnapshotsCommand(
                SetId, snapshotIds, apply,
                apply
                    ? WriteOnlyInstallation.ReclaimGrant(StateDirectory, PassphraseText, description.RestoreGrantRecipient!)
                    : null),
            CancellationToken.None);
    }

    private async Task<IReadOnlyList<SnapshotDescriptor>> ListSnapshotsAsync()
    {
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var listed = await handler.ExecuteAsync(new ListSnapshotsCommand(), CancellationToken.None);
        Assert.IsInstanceOfType<SnapshotsResult>(listed, out var snapshots);
        return snapshots.Snapshots;
    }

    /// <summary>The set's snapshots as the service lists them, oldest first.</summary>
    private async Task<IReadOnlyList<string>> ListedAsync() =>
        [.. (await ListSnapshotsAsync()).OrderBy(snapshot => snapshot.CapturedAt).Select(snapshot => snapshot.SnapshotId)];

    private Task<ServiceRuntime> StartAsync() => ServiceRuntime.StartAsync(
        new ServiceOptions { ArchivesRoot = ArchivesRoot, StateDirectory = StateDirectory },
        CancellationToken.None).AsTask();

    private async Task<IReadOnlyList<JournalRecord>> JournalAsync()
    {
        var store = Staging;
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var repository = opened.Repository;
        using var journal = new JournalReader(store, repository.RepositoryId, repository.Credential);
        var (records, _, _) = await journal.LoadAsync(
            Math.Max(repository.CurrentDataGeneration.Value, repository.CurrentMetadataGeneration.Value),
            CancellationToken.None);
        return records;
    }

    private void WriteConfiguration(RetentionConfiguration? retention, bool withSpare = false) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = VaultPath,
            },
            new DestinationConfiguration
            {
                Id = new string('2', 32), Name = "spare", Kind = DestinationKind.LocalPath, Path = SparePath,
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = SetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = SourceRoot }],
                Schedule = "every 4h",
                Retention = retention,
                Destinations = withSpare
                    ? [new SetDestinationReference { Ref = "vault" }, new SetDestinationReference { Ref = "spare" }]
                    : [new SetDestinationReference { Ref = "vault" }],
            },
        ],
    }.Save(Path.Combine(StateDirectory, "config.json"));

    /// <summary>Three daily backups, each fanned out to the vault; the set's snapshots, oldest first.</summary>
    private async Task<IReadOnlyList<string>> BackUpThreeAsync()
    {
        await BackUpAsync(Day1);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two content");
        await BackUpAsync(Day1.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
        await BackUpAsync(Day1.AddDays(2));
        return await ListedAsync();
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        var result = await AgentPass.RunAsync(ArchivesRoot, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private static async Task<List<string>> ListAsync(LocalFileSystemObjectStore store, string prefix)
    {
        var keys = new List<string>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse(prefix), ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key.Value);
        }

        return keys;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
