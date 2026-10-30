using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Replication;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Replication.Tests;

/// <summary>
/// A destination that refuses a delete its policy asked for (FR-GC-006,
/// FR-GC-010, ADR-0171): a write-once store, a bucket under object lock, a
/// credential without the right to delete. The refusal is counted and the
/// pass goes on, so the sync is not failed for it, and nothing beneath the
/// refused object is dropped in that pass, so the replica still restores
/// every snapshot it lists. A store that cannot be reached is not a refusal:
/// that pass fails and is tried again. Neither is one that keeps versions,
/// which answers the delete and keeps the bytes as an earlier version: the
/// pass goes on beneath it and counts what was kept (ADR-0171 Amendment 2).
/// Nor is one under soft delete, which keeps the bytes only for its own
/// retention period: what it keeps is counted apart and logged (ADR-0171
/// Amendment 4).
/// </summary>
/// <remarks>
/// The fixture is a replica holding two snapshots and the blobs each reaches,
/// with a policy that keeps the newer: the older snapshot and the blob only
/// it reaches are what the pass condemns, snapshot first.
/// </remarks>
[TestClass]
public sealed class ConvergeRefusalTests
{
    private const string OldSnapshot = "snapshots/aa/old";
    private const string NewSnapshot = "snapshots/bb/new";
    private const string OldOnlyBlob = "blobs/data/aaaa/old-only";
    private const string SharedBlob = "blobs/data/bbbb/shared";

    /// <summary>Every object in the fixture is the four bytes <c>held</c>.</summary>
    private const long HeldLength = 4;

    private const int RefusedByStore = 3006;
    private const int StoppedShort = 3007;
    private const int SoftDeletedByStore = 3008;

    private string _root = null!;
    private LocalFileSystemObjectStore _source = null!;
    private LocalFileSystemObjectStore _replica = null!;

    [TestInitialize]
    public async Task SetUpAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"fbp-converge-refusal-{Guid.NewGuid():N}");
        _source = new LocalFileSystemObjectStore(Path.Combine(_root, "source"));
        _replica = new LocalFileSystemObjectStore(Path.Combine(_root, "replica"));

        foreach (var key in new[] { OldSnapshot, NewSnapshot, OldOnlyBlob, SharedBlob })
        {
            await _source.PutAsync(
                ObjectKey.Parse(key),
                _ => ValueTask.FromResult<Stream>(new MemoryStream([.. "held"u8], writable: false)),
                PutConditions.None,
                CancellationToken.None);
        }

        await StoreToStoreCopier.CopyAsync(_source, _replica, CancellationToken.None);
    }

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    /// <summary>The policy: the older snapshot, and what only it reaches, are dropped.</summary>
    private static bool Keeps(string key) => !key.Contains("old", StringComparison.Ordinal);

    [TestMethod]
    [DataRow("io", DisplayName = "the store answers no")]
    [DataRow("access", DisplayName = "the path may not be written")]
    public async Task Converge_ADestinationThatRefusesASnapshotsDelete_KeepsEverythingBeneathIt_AndThePassGoesOn(string refusal)
    {
        var refusing = new DeleteFaultingObjectStore(_replica);
        refusing.ArmThrow(
            key => refusal == "io"
                ? new IOException($"The store refused to delete '{key}'.")
                : new UnauthorizedAccessException($"Access to '{key}' is denied."),
            key => key.StartsWith("snapshots/", StringComparison.Ordinal));

        var outcome = await StoreToStoreCopier.ConvergeAsync(_source, refusing, Keeps, CancellationToken.None);

        Assert.AreEqual(1L, outcome.Refused);
        Assert.AreEqual(0L, outcome.Deleted);
        Assert.AreEqual(HeldLength, outcome.RefusedBytes, "the space the refusal kept, in bytes");
        Assert.IsTrue(await HoldsAsync(OldSnapshot), "the store kept what it refused");
        Assert.IsTrue(await HoldsAsync(OldOnlyBlob), "and what that snapshot reaches, so it still restores");

        // Once the store will delete, the next pass finishes what this one could not.
        refusing.Heal();
        var healed = await StoreToStoreCopier.ConvergeAsync(_source, refusing, Keeps, CancellationToken.None);
        Assert.AreEqual(0L, healed.Refused);
        Assert.AreEqual(0L, healed.RefusedBytes);
        Assert.AreEqual(2L, healed.Deleted);
        Assert.IsFalse(await HoldsAsync(OldSnapshot));
        Assert.IsFalse(await HoldsAsync(OldOnlyBlob));
        Assert.IsTrue(await HoldsAsync(NewSnapshot) && await HoldsAsync(SharedBlob));
    }

    [TestMethod]
    public async Task Converge_ADeleteAnsweredNotFoundForAnObjectStillThere_KeepsEverythingBeneathIt()
    {
        // The object was listed a moment before. A store that says it held
        // nothing has not shown it removed, and what it reaches is kept.
        var lying = new DeleteFaultingObjectStore(_replica);
        lying.ArmNotFound(key => key.StartsWith("snapshots/", StringComparison.Ordinal));

        var outcome = await StoreToStoreCopier.ConvergeAsync(_source, lying, Keeps, CancellationToken.None);

        Assert.AreEqual(1L, outcome.Refused);
        Assert.AreEqual(HeldLength, outcome.RefusedBytes, "still there, so still counted");
        Assert.IsTrue(await HoldsAsync(OldSnapshot));
        Assert.IsTrue(await HoldsAsync(OldOnlyBlob));
    }

    [TestMethod]
    public async Task Converge_ADestinationThatRefusesABlobsDelete_HasDroppedTheSnapshotAboveIt_AndCountsTheBlob()
    {
        var refusing = new DeleteFaultingObjectStore(_replica);
        refusing.ArmThrow(key => key.StartsWith("blobs/", StringComparison.Ordinal));

        var outcome = await StoreToStoreCopier.ConvergeAsync(_source, refusing, Keeps, CancellationToken.None);

        Assert.AreEqual(1L, outcome.Deleted);
        Assert.AreEqual(1L, outcome.Refused);
        Assert.AreEqual(HeldLength, outcome.RefusedBytes);
        Assert.IsFalse(await HoldsAsync(OldSnapshot));
        Assert.IsTrue(await HoldsAsync(OldOnlyBlob), "a blob no snapshot lists is space unreclaimed, not damage");
    }

    [TestMethod]
    public async Task Converge_ARefusal_IsLoggedWithItsKey_AndSoIsThePhaseItHeldBack()
    {
        var refusing = new DeleteFaultingObjectStore(_replica);
        refusing.ArmThrow(key => key.StartsWith("snapshots/", StringComparison.Ordinal));
        var log = new RecordingLogger();

        await StoreToStoreCopier.ConvergeAsync(
            _source, refusing, Keeps, CancellationToken.None, destinationName: "spare", logger: log);

        var refused = Assert.ContainsSingle(log.Records.Where(record => record.EventId == RefusedByStore));
        Assert.AreEqual(LogLevel.Warning, refused.Level);
        Assert.AreEqual(ObjectKey.Parse(OldSnapshot), refused.Values.First(value => value.Key == "Key").Value);
        Assert.IsNotNull(refused.Exception, "the store's own words travel with it");

        var stopped = Assert.ContainsSingle(log.Records.Where(record => record.EventId == StoppedShort));
        Assert.AreEqual(LogLevel.Warning, stopped.Level);
        Assert.AreEqual(1L, stopped.Values.First(value => value.Key == "Refused").Value);
        Assert.AreEqual(LogLabel.Of("spare"), stopped.Values.First(value => value.Key == "Destination").Value);
    }

    [TestMethod]
    public async Task Converge_ADestinationThatCannotBeReached_FailsThePass_AndDropsNothingBeneath()
    {
        // Not a refusal: the store did not answer. The pass fails, so the
        // sync is tried again rather than counted done.
        var away = new DeleteFaultingObjectStore(_replica);
        away.ArmThrow(
            key => new StoreBusyException($"The store is too busy to delete '{key}'."),
            key => key.StartsWith("snapshots/", StringComparison.Ordinal));

        await Assert.ThrowsExactlyAsync<StoreBusyException>(async () =>
            await StoreToStoreCopier.ConvergeAsync(_source, away, Keeps, CancellationToken.None));

        Assert.IsTrue(await HoldsAsync(OldSnapshot));
        Assert.IsTrue(await HoldsAsync(OldOnlyBlob));
    }

    [TestMethod]
    public async Task Converge_ADestinationThatKeepsVersions_DropsWhatItsPolicyReleases_AndCountsWhatItKept()
    {
        // A bucket that keeps versions answers each delete and keeps the
        // bytes behind a delete marker. Nothing lists or reads the object
        // after, so it is gone as far as the replica goes and the pass drops
        // what lay beneath it; what the store kept is counted, in bytes.
        var versioned = new VersionKeepingStore(_replica);

        var outcome = await StoreToStoreCopier.ConvergeAsync(_source, versioned, Keeps, CancellationToken.None);

        Assert.AreEqual(2L, outcome.Deleted);
        Assert.AreEqual(0L, outcome.Refused, "a store that answered the delete did not refuse it");
        Assert.AreEqual(2L, outcome.Retained);
        Assert.AreEqual(2 * HeldLength, outcome.RetainedBytes, "the space the store kept, in bytes");
        Assert.IsFalse(await HoldsAsync(OldSnapshot));
        Assert.IsFalse(await HoldsAsync(OldOnlyBlob));
    }

    [TestMethod]
    public async Task Converge_ADestinationThatSoftDeletes_DropsWhatItsPolicyReleases_AndCountsItApartFromWhatAVersionKeeps()
    {
        // An account under soft delete answers each delete and keeps the
        // bytes until its own retention period ends, saying only that the
        // delete was not permanent. What it keeps it releases by itself, so
        // it is counted apart from what a version keeps, sized from the
        // listing the pass already took, and logged.
        var softDeleting = new SoftDeletingStore(_replica);
        var log = new RecordingLogger();

        var outcome = await StoreToStoreCopier.ConvergeAsync(
            _source, softDeleting, Keeps, CancellationToken.None, destinationName: "cloud", logger: log);

        Assert.AreEqual(2L, outcome.Deleted);
        Assert.AreEqual(0L, outcome.Refused, "a store that answered the delete did not refuse it");
        Assert.AreEqual(0L, outcome.Retained, "nothing was kept until a rule of the store's releases it");
        Assert.AreEqual(2L, outcome.SoftDeleted);
        Assert.AreEqual(2 * HeldLength, outcome.SoftDeletedBytes, "the space the account keeps for its period, in bytes");
        Assert.IsFalse(await HoldsAsync(OldSnapshot));
        Assert.IsFalse(await HoldsAsync(OldOnlyBlob));

        var said = log.Records.Where(record => record.EventId == SoftDeletedByStore).ToList();
        Assert.IsNotEmpty(said, "what the account keeps is said, if nowhere a notice would stand for it");
        Assert.AreEqual(2L, said.Sum(record => (long)record.Values.First(value => value.Key == "Count").Value!));
        Assert.AreEqual(2 * HeldLength, said.Sum(record => (long)record.Values.First(value => value.Key == "Bytes").Value!));
        Assert.IsTrue(said.All(record => record.Level == LogLevel.Information));
    }

    private async Task<bool> HoldsAsync(string key) =>
        (await _replica.GetMetadataAsync(ObjectKey.Parse(key), CancellationToken.None)).Found;

    /// <summary>
    /// A store that keeps versions: a delete hides the object, as a delete
    /// marker does, and says how many bytes it kept.
    /// </summary>
    private sealed class VersionKeepingStore(IObjectStore inner) : IObjectStore
    {
        public StoreCapabilities Capabilities => inner.Capabilities;

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, range, cancellationToken);

        public ValueTask<PutResult> PutAsync(
            ObjectKey key,
            Func<CancellationToken, ValueTask<Stream>> openContent,
            PutConditions conditions,
            CancellationToken cancellationToken) =>
            inner.PutAsync(key, openContent, conditions, cancellationToken);

        public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        public async ValueTask<DeleteResult> DeleteAsync(
            ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken)
        {
            var held = await inner.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
            var deleted = await inner.DeleteAsync(key, conditions, cancellationToken).ConfigureAwait(false);
            return deleted.Outcome == DeleteOutcome.Deleted ? deleted with { RetainedBytes = held.Metadata!.Length } : deleted;
        }
    }

    /// <summary>
    /// A store under soft delete: a delete hides the object and says it was
    /// not permanent, and says nothing of how many bytes it keeps.
    /// </summary>
    private sealed class SoftDeletingStore(IObjectStore inner) : IObjectStore
    {
        public StoreCapabilities Capabilities => inner.Capabilities;

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, range, cancellationToken);

        public ValueTask<PutResult> PutAsync(
            ObjectKey key,
            Func<CancellationToken, ValueTask<Stream>> openContent,
            PutConditions conditions,
            CancellationToken cancellationToken) =>
            inner.PutAsync(key, openContent, conditions, cancellationToken);

        public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        public async ValueTask<DeleteResult> DeleteAsync(
            ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken)
        {
            var deleted = await inner.DeleteAsync(key, conditions, cancellationToken).ConfigureAwait(false);
            return deleted.Outcome == DeleteOutcome.Deleted ? deleted with { SoftDeleted = true } : deleted;
        }
    }
}
