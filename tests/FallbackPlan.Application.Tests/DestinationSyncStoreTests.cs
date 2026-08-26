using FallbackPlan.Application;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The sync ledger is the one durable record of where each destination stands,
/// and three different callers write to it. These pin the properties that make
/// that safe: a write never loses a field another write had just set, every
/// mutator carries forward what it does not name, and a file written by another
/// build is recognised rather than silently read as defaults.
/// </summary>
[TestClass]
public sealed class DestinationSyncStoreTests
{
    private const string SetId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private string _state = null!;

    [TestInitialize]
    public void SetUp()
    {
        _state = Path.Combine(Path.GetTempPath(), $"fbp-ledger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_state);
    }

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    [TestMethod]
    public void ConcurrentSuccessAndVerification_LosesNeitherSideOfTheRow()
    {
        // The race the lock closes. Each mutator reads the row, changes some
        // fields, and writes the whole thing back; with the read outside the
        // lock, two writers each carry forward what they read and the loser's
        // change vanishes. Both sequences here only ever advance, so a lost
        // update shows up as a final value below the highest one written.
        var store = DestinationSyncStore.Open(_state);
        const int rounds = 300;

        var syncs = Task.Run(() =>
        {
            for (var round = 1; round <= rounds; round++)
            {
                store.RecordSuccess(SetId, "vault", objects: round, nowUnixMilliseconds: 1_000, syncedSequence: (ulong)round);
            }
        });

        var proofs = Task.Run(() =>
        {
            for (var round = 1; round <= rounds; round++)
            {
                store.RecordVerification(
                    SetId, "vault", objects: 4, population: 12, verifiedSequence: (ulong)round, sampleCursor: null,
                    nowUnixMilliseconds: 1_000);
            }
        });

        Task.WaitAll(syncs, proofs);

        var record = store.Find(SetId, "vault")!;
        Assert.AreEqual((ulong)rounds, record.SyncedSequence, "a verification must not roll back the synced sequence");
        Assert.AreEqual((ulong)rounds, record.VerifiedSequence, "a sync must not roll back the verified sequence");
    }

    [TestMethod]
    public void RecordVerification_LeavesTheSyncHalfOfTheRowAlone()
    {
        // Proving bytes says nothing about when they arrived, so a stamp must
        // carry the sync half forward untouched — the trim gate reads both.
        var store = DestinationSyncStore.Open(_state);
        store.RecordSuccess(SetId, "vault", objects: 7, nowUnixMilliseconds: 1_000, syncedSequence: 42);

        store.RecordVerification(
            SetId, "vault", objects: 4, population: 12, verifiedSequence: 42, sampleCursor: null,
            nowUnixMilliseconds: 2_000);

        var record = store.Find(SetId, "vault")!;
        Assert.AreEqual(DestinationSyncState.InSync, record.State);
        Assert.AreEqual(1_000UL, record.LastSuccessAt);
        Assert.AreEqual(7L, record.Objects);
        Assert.AreEqual(42UL, record.SyncedSequence);
        Assert.AreEqual(2_000UL, record.VerifiedAt);
    }

    [TestMethod]
    public void RecordFailure_KeepsTheStampsAndTheLastSuccess()
    {
        // A failed attempt does not un-prove bytes that were proven, nor
        // un-happen the last success.
        var store = DestinationSyncStore.Open(_state);
        store.RecordSuccess(SetId, "vault", objects: 7, nowUnixMilliseconds: 1_000, syncedSequence: 42);
        store.RecordVerification(
            SetId, "vault", objects: 4, population: 12, verifiedSequence: 42, sampleCursor: null,
            nowUnixMilliseconds: 1_500);

        store.RecordFailure(SetId, "vault", DestinationSyncState.Unavailable, "the drive is unplugged", 3_000);

        var record = store.Find(SetId, "vault")!;
        Assert.AreEqual(DestinationSyncState.Unavailable, record.State);
        Assert.AreEqual(1, record.ConsecutiveFailures);
        Assert.AreEqual("the drive is unplugged", record.LastError);
        Assert.AreEqual(1_000UL, record.LastSuccessAt);
        Assert.AreEqual(42UL, record.SyncedSequence);
        Assert.AreEqual(1_500UL, record.VerifiedAt);
        Assert.AreEqual(4, record.VerifiedObjects);
    }

    [TestMethod]
    public void RecordSuccess_ClearsTheLastError()
    {
        // Carried forward by `with` unless said otherwise — and `status`
        // repeats this string verbatim, so a resolved error must not outlive
        // its cause.
        var store = DestinationSyncStore.Open(_state);
        store.RecordFailure(SetId, "vault", DestinationSyncState.Failed, "the drive is unplugged", 1_000);

        store.RecordSuccess(SetId, "vault", objects: 3, nowUnixMilliseconds: 2_000, syncedSequence: 9);

        var record = store.Find(SetId, "vault")!;
        Assert.IsNull(record.LastError);
        Assert.AreEqual(0, record.ConsecutiveFailures);
    }

    [TestMethod]
    public void Open_APreVersioningBareArray_MigratesRatherThanDiscarding()
    {
        // The shape every install before this build wrote. The rows are
        // perfectly readable; quarantining them would silently restart every
        // destination's history and, worse, look like a healthy empty ledger.
        var path = Path.Combine(_state, "destinations.json");
        File.WriteAllText(path, """
            [ { "set": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "destination": "vault", "state": "InSync",
                "last_attempt_at": 1000, "last_success_at": 1000, "objects": 7, "synced_sequence": 42 } ]
            """);

        var record = DestinationSyncStore.Open(_state).Find(SetId, "vault");

        Assert.IsNotNull(record, "a pre-versioning ledger must be read, not discarded");
        Assert.AreEqual(42UL, record.SyncedSequence);
        Assert.IsFalse(File.Exists(path + ".corrupt"));
    }

    [TestMethod]
    public void Open_AFileFromANewerBuild_SetsItAsideRatherThanReadingItAsDefaults()
    {
        // A downgrade must not read a newer row as defaults and then overwrite
        // it: the bytes survive under .corrupt instead.
        var path = Path.Combine(_state, "destinations.json");
        File.WriteAllText(path, """
            { "schema_version": 99, "destinations": [
                { "set": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "destination": "vault", "state": "InSync",
                  "last_attempt_at": 1000, "synced_sequence": 42 } ] }
            """);

        var store = DestinationSyncStore.Open(_state);

        Assert.IsNull(store.Find(SetId, "vault"));
        Assert.IsTrue(File.Exists(path + ".corrupt"), "the newer file's bytes must be preserved");
    }

    [TestMethod]
    public void SaveThenOpen_RoundTripsThroughTheVersionedShape()
    {
        DestinationSyncStore.Open(_state)
            .RecordSuccess(SetId, "vault", objects: 7, nowUnixMilliseconds: 1_000, syncedSequence: 42);

        var text = File.ReadAllText(Path.Combine(_state, "destinations.json"));
        Assert.Contains("\"schema_version\": 2", text, StringComparison.Ordinal);

        var record = DestinationSyncStore.Open(_state).Find(SetId, "vault")!;
        Assert.AreEqual(42UL, record.SyncedSequence);
    }

    [TestMethod]
    public void Open_ASchemaOneLedger_SeedsBaselinesFromSuccesses()
    {
        // The migration rule that lets existing installs skip the full
        // re-seed: on the staging architecture every successful sync copied
        // the whole archive, so a row with a success IS a full replica — its
        // baseline is that success. A row that never succeeded seeds nothing.
        File.WriteAllText(
            Path.Combine(_state, "destinations.json"),
            """
            {
              "schema_version": 1,
              "destinations": [
                {
                  "set": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "destination": "vault",
                  "state": "InSync",
                  "last_attempt_at": 5000,
                  "last_success_at": 5000,
                  "objects": 12,
                  "synced_sequence": 9
                },
                {
                  "set": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "destination": "offsite",
                  "state": "Unavailable",
                  "last_attempt_at": 4000
                }
              ]
            }
            """);

        var store = DestinationSyncStore.Open(_state);

        var seeded = store.Find(SetId, "vault")!;
        Assert.AreEqual(5000UL, seeded.BaselineCompletedAt);
        Assert.IsFalse(seeded.NeedsFull);

        var never = store.Find(SetId, "offsite")!;
        Assert.IsNull(never.BaselineCompletedAt);
    }

    [TestMethod]
    public void RecordSuccess_TheFirstSuccess_EstablishesTheBaselineAndClearsNeedsFull()
    {
        var store = DestinationSyncStore.Open(_state);
        store.RecordNeedsFull(SetId, "vault", nowUnixMilliseconds: 500);
        Assert.IsTrue(store.Find(SetId, "vault")!.NeedsFull);

        store.RecordSuccess(SetId, "vault", objects: 3, nowUnixMilliseconds: 1_000, syncedSequence: 1);

        var first = store.Find(SetId, "vault")!;
        Assert.AreEqual(1_000UL, first.BaselineCompletedAt);
        Assert.IsFalse(first.NeedsFull);

        // A later success moves the sync marks, never the baseline: the
        // baseline records when this destination first held a full copy.
        store.RecordSuccess(SetId, "vault", objects: 1, nowUnixMilliseconds: 2_000, syncedSequence: 2);
        Assert.AreEqual(1_000UL, store.Find(SetId, "vault")!.BaselineCompletedAt);
    }
}
