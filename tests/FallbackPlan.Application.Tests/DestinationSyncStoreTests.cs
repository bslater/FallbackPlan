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

        // The bare array is older than schema 1, so it must ride the same
        // baseline seeding the schema-1 migration gets: a row with a success
        // IS a full replica (ADR-0047). Skipping it here would leave the very
        // oldest installs re-shipping everything a direct-ship flip touches.
        Assert.AreEqual(1000UL, record.BaselineCompletedAt, "the legacy row's success must seed its baseline");
        Assert.IsFalse(record.NeedsFull);
    }

    [TestMethod]
    public void Open_UnreadableGarbage_SetsTheFileAsideAndStartsEmpty()
    {
        // Neither the versioned shape nor the legacy bare array: the ledger
        // is sacrificial (its header says so), so the service must start —
        // with the bytes preserved for a person, never silently overwritten.
        var path = Path.Combine(_state, "destinations.json");
        File.WriteAllText(path, "{{{ this was never JSON");

        var store = DestinationSyncStore.Open(_state);

        Assert.IsNull(store.Find(SetId, "vault"));
        Assert.IsTrue(File.Exists(path + ".corrupt"), "the unreadable bytes must be preserved for inspection");

        // And the empty store is a working one: the next write starts a fresh
        // versioned file in the vacated spot.
        store.RecordSuccess(SetId, "vault", objects: 1, nowUnixMilliseconds: 1_000, syncedSequence: 1);
        Assert.IsNotNull(DestinationSyncStore.Open(_state).Find(SetId, "vault"));
    }

    [TestMethod]
    public void RecordVerification_CarriesTheTiers_AndTheyRoundTrip()
    {
        // Schema 3: which proof the pass rested on rides beside the count, so
        // a write-only set's "proven" can say it was the digest that proved
        // the data plane and not a tag nobody could open.
        DestinationSyncStore.Open(_state).RecordVerification(
            SetId, "vault", objects: 7, population: 20, verifiedSequence: 3, sampleCursor: null,
            nowUnixMilliseconds: 1_000, @sealed: 4, digest: 3);

        var reopened = DestinationSyncStore.Open(_state).Find(SetId, "vault")!;
        Assert.AreEqual(7, reopened.VerifiedObjects);
        Assert.AreEqual(4, reopened.VerifiedSealed);
        Assert.AreEqual(3, reopened.VerifiedDigest);
    }

    [TestMethod]
    public void Open_ASchemaTwoLedger_ReadsItsRowsWithZeroTiers()
    {
        // A ledger written before the tiers were counted: the row is kept
        // whole and the tiers read as zero — honest, since nobody counted
        // them — rather than the file being set aside as foreign.
        var path = Path.Combine(_state, "destinations.json");
        File.WriteAllText(path, """
            { "schema_version": 2, "destinations": [
                { "set": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "destination": "vault", "state": "InSync",
                  "last_attempt_at": 1000, "last_success_at": 1000, "synced_sequence": 42,
                  "verified_at": 1000, "verified_objects": 4, "verified_population": 12 } ] }
            """);

        var record = DestinationSyncStore.Open(_state).Find(SetId, "vault");

        Assert.IsNotNull(record, "a schema-2 ledger must migrate, not quarantine");
        Assert.IsFalse(File.Exists(path + ".corrupt"));
        Assert.AreEqual(4, record.VerifiedObjects);
        Assert.AreEqual(0, record.VerifiedSealed);
        Assert.AreEqual(0, record.VerifiedDigest);
    }

    [TestMethod]
    public void Open_AFileOneSchemaAhead_IsSetAside()
    {
        // The downgrade rule, pinned at the edge rather than at 99: the very
        // next schema is already foreign to this build.
        var path = Path.Combine(_state, "destinations.json");
        File.WriteAllText(path, """
            { "schema_version": 4, "destinations": [
                { "set": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "destination": "vault", "state": "InSync",
                  "last_attempt_at": 1000, "synced_sequence": 42 } ] }
            """);

        var store = DestinationSyncStore.Open(_state);

        Assert.IsNull(store.Find(SetId, "vault"));
        Assert.IsTrue(File.Exists(path + ".corrupt"), "the newer file's bytes must be preserved");
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
        Assert.Contains("\"schema_version\": 3", text, StringComparison.Ordinal);

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
    public void EveryStampMutator_CarriesTheBaselineForward()
    {
        // A2's run-scope check reads BaselineCompletedAt, so a mutator that
        // dropped it would silently evict a healthy destination from every
        // later run. Failure, verification and sweep all touch the row after
        // a baseline exists; none of them may move or lose it.
        var store = DestinationSyncStore.Open(_state);
        store.RecordSuccess(SetId, "vault", objects: 3, nowUnixMilliseconds: 1_000, syncedSequence: 1);

        store.RecordFailure(SetId, "vault", DestinationSyncState.Unavailable, "unplugged", 2_000);
        store.RecordVerification(
            SetId, "vault", objects: 2, population: 8, verifiedSequence: 1, sampleCursor: null,
            nowUnixMilliseconds: 3_000);
        store.RecordSweep(SetId, "vault", cursor: "blobs/aa", examined: 4, completedCircuit: false, 4_000);

        var record = store.Find(SetId, "vault")!;
        Assert.AreEqual(1_000UL, record.BaselineCompletedAt, "a failure must not erase a baseline");
        Assert.IsFalse(record.NeedsFull);

        // And the debt side survives the same traffic: a pair owed its seed
        // stays owed through a failed attempt.
        store.RecordNeedsFull(SetId, "offsite", nowUnixMilliseconds: 500);
        store.RecordFailure(SetId, "offsite", DestinationSyncState.Failed, "refused", 1_000);
        Assert.IsTrue(store.Find(SetId, "offsite")!.NeedsFull);
    }

    [TestMethod]
    public void RecordNeedsFull_OnAPairHoldingABaseline_IsANoOp()
    {
        // Re-adding a destination a set already seeded must not send it back
        // to full-backup purgatory: the debt exists only while no baseline
        // does (ADR-0047 §5).
        var store = DestinationSyncStore.Open(_state);
        store.RecordSuccess(SetId, "vault", objects: 3, nowUnixMilliseconds: 1_000, syncedSequence: 1);

        store.RecordNeedsFull(SetId, "vault", nowUnixMilliseconds: 2_000);

        var record = store.Find(SetId, "vault")!;
        Assert.IsFalse(record.NeedsFull);
        Assert.AreEqual(1_000UL, record.BaselineCompletedAt);
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

    [TestMethod]
    public void RecordCompleteness_OnAPairThatHasNeverSucceeded_DoesNotCallItInSync()
    {
        // The figures are written from the copy's `finally`, so on a pair's
        // very first copy they reach the ledger BEFORE any success does. The
        // row they create must not say the destination is in sync: nothing
        // has yet held anything, and `in sync` is the one word a person reads
        // as "my backup is there".
        var store = DestinationSyncStore.Open(_state);

        store.RecordCompleteness(SetId, "vault", heldBytes: 10, owedBytes: 100, nowUnixMilliseconds: 1_000);

        var record = store.Find(SetId, "vault")!;
        Assert.AreNotEqual(DestinationSyncState.InSync, record.State);
        Assert.IsNull(record.LastSuccessAt, "no success has happened, and the seed must not invent one");
        Assert.AreEqual(10L, record.HeldBytes);
        Assert.AreEqual(100L, record.OwedBytes);
    }

    [TestMethod]
    public void RecordNeedsFull_OnAPairNeverSyncedAtAll_DoesNotCallItInSync()
    {
        // Same trap, the other pre-success writer: a set that has just gained
        // a destination owes it everything, and the row that records the debt
        // must not simultaneously claim the debt is paid.
        var store = DestinationSyncStore.Open(_state);

        store.RecordNeedsFull(SetId, "vault", nowUnixMilliseconds: 1_000);

        var record = store.Find(SetId, "vault")!;
        Assert.AreNotEqual(DestinationSyncState.InSync, record.State);
        Assert.IsTrue(record.NeedsFull);
    }

    [TestMethod]
    public void RecordCompleteness_OnAPairAlreadyInSync_LeavesTheStateAlone()
    {
        // The seed is only for a pair with no row. A pair that earned in-sync
        // keeps it: counting bytes is not an attempt and says nothing about
        // whether the last copy succeeded.
        var store = DestinationSyncStore.Open(_state);
        store.RecordSuccess(SetId, "vault", objects: 3, nowUnixMilliseconds: 1_000, syncedSequence: 1);

        store.RecordCompleteness(SetId, "vault", heldBytes: 100, owedBytes: 100, nowUnixMilliseconds: 2_000);

        var record = store.Find(SetId, "vault")!;
        Assert.AreEqual(DestinationSyncState.InSync, record.State);
        Assert.AreEqual(1_000UL, record.LastSuccessAt);
    }
}
