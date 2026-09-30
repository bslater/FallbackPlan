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
    public void RecordSuccess_HandedTheProofOfTheCopy_CallsThePairInSyncOnlyAsProved()
    {
        // A pass that proved what it copied records both in the write that
        // calls the pair in sync. Written as two, the first said the
        // destination held a snapshot nothing had proved, and a reader
        // between them reported that snapshot durable (FR-SNP-003).
        var written = new List<DestinationSyncRecord>();
        var store = DestinationSyncStore.Open(_state, written.Add);

        store.RecordSuccess(
            SetId, "vault", objects: 7, nowUnixMilliseconds: 1_000, syncedSequence: 42,
            verified: new VerificationStamp(
                Objects: 4, Population: 12, VerifiedSequence: 42, SampleCursor: "blobs/7f", Sealed: 3, Digest: 1));

        var row = Assert.ContainsSingle(written);
        Assert.AreEqual(DestinationSyncState.InSync, row.State);
        Assert.AreEqual(42UL, row.SyncedSequence);
        Assert.AreEqual(1_000UL, row.VerifiedAt);
        Assert.AreEqual(42UL, row.VerifiedSequence);
        Assert.AreEqual(4, row.VerifiedObjects);
        Assert.AreEqual(12, row.VerifiedPopulation);
        Assert.AreEqual(3, row.VerifiedSealed);
        Assert.AreEqual(1, row.VerifiedDigest);
        Assert.AreEqual("blobs/7f", row.SampleCursor);
        Assert.AreEqual(row, DestinationSyncStore.Open(_state).Find(SetId, "vault"), "the row observed is the row saved");
    }

    [TestMethod]
    public void RecordSuccess_HandedAnOlderProof_KeepsTheNewerVerifiedSequence()
    {
        // Riding the success does not change the verification's own rule:
        // the verified sequence only advances, like the synced one beside it.
        var store = DestinationSyncStore.Open(_state);
        store.RecordVerification(
            SetId, "vault", objects: 4, population: 12, verifiedSequence: 50, sampleCursor: null,
            nowUnixMilliseconds: 1_000);

        store.RecordSuccess(
            SetId, "vault", objects: 7, nowUnixMilliseconds: 2_000, syncedSequence: 42,
            verified: new VerificationStamp(Objects: 2, Population: 12, VerifiedSequence: 42, SampleCursor: null));

        var record = store.Find(SetId, "vault")!;
        Assert.AreEqual(50UL, record.VerifiedSequence);
        Assert.AreEqual(2_000UL, record.VerifiedAt);
        Assert.AreEqual(2, record.VerifiedObjects);
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
    public void RecordDamage_AddsWhatWasFound_AndClearsWhatWasResolved()
    {
        // FR-VER-007. The keys a deep sweep found damaged and could not
        // repair outlive the segment that found them: the sync that follows
        // re-checks exactly these, and a restart must not forget them.
        var store = DestinationSyncStore.Open(_state);
        store.RecordSuccess(SetId, "vault", objects: 3, nowUnixMilliseconds: 1_000, syncedSequence: 9);

        store.RecordDamage(SetId, "vault", unrepaired: ["blobs/data/a", "blobs/data/b"], resolved: [], 2_000);
        CollectionAssert.AreEquivalent(
            new[] { "blobs/data/a", "blobs/data/b" },
            DestinationSyncStore.Open(_state).Find(SetId, "vault")!.DamagedKeys!.ToList());

        store.RecordDamage(SetId, "vault", unrepaired: ["blobs/data/b"], resolved: ["blobs/data/a"], 3_000);
        Assert.AreEqual("blobs/data/b", Assert.ContainsSingle(store.Find(SetId, "vault")!.DamagedKeys!));

        store.RecordDamage(SetId, "vault", unrepaired: [], resolved: ["blobs/data/b"], 4_000);
        Assert.IsNull(store.Find(SetId, "vault")!.DamagedKeys, "a row with nothing outstanding carries no list at all");
    }

    [TestMethod]
    public void RecordSuccess_OverUnrepairedDamage_KeepsThePairFailed_AndSaysWhy()
    {
        // A copy can succeed at a destination that still holds objects nobody
        // could repair — a capture ships its new blobs there and knows nothing
        // of the old ones. Whichever writer records that success, the pair
        // must not read as in sync while the ledger says part of what it holds
        // cannot be restored.
        var store = DestinationSyncStore.Open(_state);
        store.RecordSuccess(SetId, "vault", objects: 3, nowUnixMilliseconds: 1_000, syncedSequence: 9);
        store.RecordDamage(SetId, "vault", unrepaired: ["blobs/data/a"], resolved: [], 2_000);
        store.RecordFailure(SetId, "vault", DestinationSyncState.Failed, "deep verification found damage", 2_000);

        store.RecordSuccess(SetId, "vault", objects: 5, nowUnixMilliseconds: 3_000, syncedSequence: 12);

        var damaged = store.Find(SetId, "vault")!;
        Assert.AreEqual(DestinationSyncState.Failed, damaged.State);
        Assert.Contains("blobs/data/a", damaged.LastError!, StringComparison.Ordinal);
        Assert.Contains("no sound copy", damaged.LastError!, StringComparison.Ordinal);
        Assert.AreEqual(3_000UL, damaged.LastSuccessAt, "the copy itself did succeed, and the row says so");
        Assert.AreEqual(1, damaged.ConsecutiveFailures, "a success over known damage is not a recovery");

        store.RecordDamage(SetId, "vault", unrepaired: [], resolved: ["blobs/data/a"], 4_000);
        store.RecordSuccess(SetId, "vault", objects: 0, nowUnixMilliseconds: 5_000, syncedSequence: 12);

        var repaired = store.Find(SetId, "vault")!;
        Assert.AreEqual(DestinationSyncState.InSync, repaired.State);
        Assert.IsNull(repaired.LastError);
        Assert.AreEqual(0, repaired.ConsecutiveFailures);
    }

    [TestMethod]
    public void RecordFailure_ForTheDamageAlone_SaysSo_AndAFailureOfAnotherKindUnsaysIt()
    {
        // FR-VER-005 (schema 7): a pair failed for what it holds damaged, and
        // for nothing else, degrades only the snapshots that need those
        // objects. The row has to say which kind of failure is standing.
        var store = DestinationSyncStore.Open(_state);
        store.RecordSuccess(SetId, "vault", objects: 3, nowUnixMilliseconds: 1_000, syncedSequence: 9);
        store.RecordDamage(SetId, "vault", unrepaired: ["blobs/data/a"], resolved: [], 2_000);

        store.RecordFailure(SetId, "vault", DestinationSyncState.Failed, "deep verification found damage", 2_000, damageOnly: true);
        Assert.IsTrue(DestinationSyncStore.Open(_state).Find(SetId, "vault")!.DamageOnly, "and it survives a restart");

        store.RecordFailure(SetId, "vault", DestinationSyncState.Failed, "the destination refused the session", 3_000);
        Assert.IsFalse(store.Find(SetId, "vault")!.DamageOnly);
    }

    [TestMethod]
    public void RecordFailure_ForDamage_OverAFailureOfAnotherKind_DoesNotNarrowIt()
    {
        // The damage finding says nothing about what the earlier failure
        // found; only a success clears that, and until one the whole copy
        // stays suspect.
        var store = DestinationSyncStore.Open(_state);
        store.RecordFailure(SetId, "vault", DestinationSyncState.Failed, "verification failed: 2 of 16 objects", 1_000);
        store.RecordDamage(SetId, "vault", unrepaired: ["blobs/data/a"], resolved: [], 2_000);

        store.RecordFailure(SetId, "vault", DestinationSyncState.Failed, "deep verification found damage", 2_000, damageOnly: true);

        Assert.IsFalse(store.Find(SetId, "vault")!.DamageOnly);
    }

    [TestMethod]
    public void RecordSuccess_OverUnrepairedDamage_LeavesItFailedForTheDamageAlone()
    {
        // A success is the whole copy answering: whatever failure stood
        // before it, what is left standing after it is the damage.
        var store = DestinationSyncStore.Open(_state);
        store.RecordFailure(SetId, "vault", DestinationSyncState.Failed, "the drive was read-only", 1_000);
        store.RecordDamage(SetId, "vault", unrepaired: ["blobs/data/a"], resolved: [], 2_000);

        store.RecordSuccess(SetId, "vault", objects: 5, nowUnixMilliseconds: 3_000, syncedSequence: 12);
        var damaged = store.Find(SetId, "vault")!;
        Assert.AreEqual(DestinationSyncState.Failed, damaged.State);
        Assert.IsTrue(damaged.DamageOnly);

        store.RecordDamage(SetId, "vault", unrepaired: [], resolved: ["blobs/data/a"], 4_000);
        store.RecordSuccess(SetId, "vault", objects: 0, nowUnixMilliseconds: 5_000, syncedSequence: 12);
        Assert.IsFalse(store.Find(SetId, "vault")!.DamageOnly, "a pair in sync has no failure to scope");
    }

    [TestMethod]
    public void RecordHeldForDamage_ASyncTheCopyAnsweredButDamageRemains_IsFailedForTheDamageAlone()
    {
        // A sync that copied everything and re-checked its damaged objects
        // has shown the rest of the copy answers, whatever failure stood
        // before it: what holds the pair failed now is the damage, and the
        // back-off still counts it so the same blobs are not re-read every
        // pass (FR-VER-007).
        var store = DestinationSyncStore.Open(_state);
        store.RecordFailure(SetId, "vault", DestinationSyncState.Failed, "the copy broke off", 1_000);
        store.RecordDamage(SetId, "vault", unrepaired: ["blobs/data/a"], resolved: [], 2_000);

        var held = store.RecordHeldForDamage(SetId, "vault", "1 object(s) found damaged here have had no sound copy", 3_000);

        Assert.AreEqual(DestinationSyncState.Failed, held.State);
        Assert.IsTrue(held.DamageOnly);
        Assert.AreEqual(2, held.ConsecutiveFailures, "held back, and backed off");
        Assert.Contains("no sound copy", held.LastError!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Open_ASchemaSixLedger_ReadsEveryFailureAsOfTheWholeCopy()
    {
        // Schema 6 said nothing about why a pair was failed, and the old
        // reading of a failed pair — every snapshot degraded — is the one that
        // cannot understate damage.
        File.WriteAllText(Path.Combine(_state, "destinations.json"), """
            { "schema_version": 6, "destinations": [
                { "set": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "destination": "vault", "state": "Failed",
                  "last_attempt_at": 1000, "damaged_keys": [ "blobs/data/a" ] } ] }
            """);

        var record = DestinationSyncStore.Open(_state).Find(SetId, "vault")!;

        Assert.AreEqual(DestinationSyncState.Failed, record.State);
        Assert.IsFalse(record.DamageOnly);
    }

    [TestMethod]
    public void RecordSweepStall_ThatReadNothing_CountsAnotherStall_AndMovesNoCursor()
    {
        // FR-VER-007. A segment that stopped at a blob it could not read
        // counts a stall, and the stalls in a row set how long the next
        // attempt waits. Nothing read is nothing to move the circuit on by,
        // and "when the sweep last read anything" did not move either.
        var store = DestinationSyncStore.Open(_state);
        store.RecordSuccess(SetId, "vault", objects: 3, nowUnixMilliseconds: 1_000, syncedSequence: 9);
        store.RecordSweep(SetId, "vault", "blobs/data/a", examined: 1, completedCircuit: false, 2_000);

        store.RecordSweepStall(SetId, "vault", "blobs/data/a", examined: 0, stalledOn: "blobs/data/b", 3_000);
        var once = store.RecordSweepStall(SetId, "vault", "blobs/data/a", examined: 0, stalledOn: "blobs/data/b", 4_000);

        Assert.AreEqual(2, once.SweepStalls);
        Assert.AreEqual(4_000UL, once.SweepStalledAt);
        Assert.AreEqual("blobs/data/b", once.SweepStalledOn);
        Assert.AreEqual("blobs/data/a", once.SweepCursor);
        Assert.AreEqual(2_000UL, once.SweptAt, "a stall that read nothing is not a read");
        Assert.AreEqual(1, once.SweptThisCircuit);

        var reopened = DestinationSyncStore.Open(_state).Find(SetId, "vault")!;
        Assert.AreEqual(2, reopened.SweepStalls, "a restart must not reset the back-off");
        Assert.AreEqual("blobs/data/b", reopened.SweepStalledOn);
    }

    [TestMethod]
    public void RecordSweepStall_ThatReadSomething_KeepsItsProgress_AndCountsFromOne()
    {
        // A segment that got further before it stopped is a new stall, not
        // the old one again: whatever stopped the last attempt, this one read
        // past it.
        var store = DestinationSyncStore.Open(_state);
        store.RecordSuccess(SetId, "vault", objects: 3, nowUnixMilliseconds: 1_000, syncedSequence: 9);
        store.RecordSweepStall(SetId, "vault", cursor: null, examined: 0, stalledOn: "blobs/data/a", 2_000);
        store.RecordSweepStall(SetId, "vault", cursor: null, examined: 0, stalledOn: "blobs/data/a", 3_000);

        var further = store.RecordSweepStall(
            SetId, "vault", "blobs/data/b", examined: 2, stalledOn: "blobs/data/c", 4_000);

        Assert.AreEqual(1, further.SweepStalls);
        Assert.AreEqual("blobs/data/b", further.SweepCursor);
        Assert.AreEqual(4_000UL, further.SweptAt);
        Assert.AreEqual(2, further.SweptThisCircuit);
        Assert.AreEqual("blobs/data/c", further.SweepStalledOn);
    }

    [TestMethod]
    public void RecordSweep_ASegmentThatFinished_ClearsTheStall()
    {
        var store = DestinationSyncStore.Open(_state);
        store.RecordSuccess(SetId, "vault", objects: 3, nowUnixMilliseconds: 1_000, syncedSequence: 9);
        store.RecordSweepStall(SetId, "vault", cursor: null, examined: 0, stalledOn: "blobs/data/a", 2_000);

        var finished = store.RecordSweep(SetId, "vault", "blobs/data/a", examined: 1, completedCircuit: false, 3_000);

        Assert.AreEqual(0, finished.SweepStalls);
        Assert.IsNull(finished.SweepStalledAt);
        Assert.IsNull(finished.SweepStalledOn);
        Assert.DoesNotContain(
            "sweep_stall", File.ReadAllText(Path.Combine(_state, "destinations.json")), StringComparison.Ordinal,
            "a pair that is not stalled carries no stall columns");
    }

    [TestMethod]
    public void Open_ASchemaFiveLedger_ReadsNoOutstandingDamage()
    {
        // Schema 6 added the keys found damaged and not yet repaired, as a
        // plain additive column. A schema-5 row reads it as absent, which is
        // true of it: nothing had been found that nothing could repair.
        var path = Path.Combine(_state, "destinations.json");
        File.WriteAllText(path, """
            { "schema_version": 5, "destinations": [
                { "set": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "destination": "vault", "state": "InSync",
                  "last_attempt_at": 1000, "last_success_at": 1000, "synced_sequence": 42,
                  "swept_at": 2000, "sweep_completed_at": 2000 } ] }
            """);

        var record = DestinationSyncStore.Open(_state).Find(SetId, "vault");

        Assert.IsNotNull(record, "a schema-5 ledger must migrate, not quarantine");
        Assert.IsFalse(File.Exists(path + ".corrupt"));
        Assert.AreEqual(2_000UL, record.SweepCompletedAt);
        Assert.IsNull(record.DamagedKeys);
        Assert.AreEqual(0, record.SweepStalls, "nor had a sweep been stopped short by a blob that would not read");
        Assert.IsNull(record.SweepStalledOn);
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
    [FallbackPlan.TestSupport.PlatformCondition(FallbackPlan.TestSupport.TestPlatforms.Windows,
        "a rename holds the file it renamed open for deletion until it returns, and only Windows refuses "
        + "a reader that does not share deletion with that handle")]
    public void Open_WhileAWriteHoldsTheLedgerForDeletion_StillReadsIt()
    {
        // `status` given a repository opens the ledger without the writer
        // role, so a running service may be replacing the file as it reads.
        // A replace renames the new file into place and holds it open for
        // deletion until the rename returns, and an open in that moment must
        // still read the old rows or the new ones rather than fail the
        // command that asked.
        //
        // The handle below is that moment held still, as in AtomicFileTests;
        // delete-on-close is how .NET asks for deletion access. The first
        // assertion is the control: a read that does not share deletion is
        // refused under that handle, so the second one proves something.
        DestinationSyncStore.Open(_state)
            .RecordSuccess(SetId, "vault", objects: 7, nowUnixMilliseconds: 1_000, syncedSequence: 42);
        var path = Path.Combine(_state, "destinations.json");

        using (File.OpenHandle(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.DeleteOnClose))
        {
            Assert.Throws<IOException>(
                () => File.ReadAllText(path),
                "the held handle must refuse a reader that does not share deletion, or this test proves nothing");
            Assert.AreEqual(42UL, DestinationSyncStore.Open(_state).Find(SetId, "vault")?.SyncedSequence);
        }
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
        Assert.AreEqual(0, record.VerifiedChunk);
    }

    [TestMethod]
    public void Open_ASchemaThreeLedger_ReadsItsTiersAndZeroChunks()
    {
        // The same rule one schema on: a ledger written before the chunk
        // tier existed keeps the two tiers it counted and reads the third as
        // zero, which is true of it — nothing had asked a peer for a leaf.
        var path = Path.Combine(_state, "destinations.json");
        File.WriteAllText(path, """
            { "schema_version": 3, "destinations": [
                { "set": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "destination": "vault", "state": "InSync",
                  "last_attempt_at": 1000, "last_success_at": 1000, "synced_sequence": 42,
                  "verified_at": 1000, "verified_objects": 7, "verified_population": 12,
                  "verified_sealed": 4, "verified_digest": 3 } ] }
            """);

        var record = DestinationSyncStore.Open(_state).Find(SetId, "vault");

        Assert.IsNotNull(record, "a schema-3 ledger must migrate, not quarantine");
        Assert.IsFalse(File.Exists(path + ".corrupt"));
        Assert.AreEqual(4, record.VerifiedSealed);
        Assert.AreEqual(3, record.VerifiedDigest);
        Assert.AreEqual(0, record.VerifiedChunk);
    }

    [TestMethod]
    public void Open_ASchemaFourLedger_ReadsItsDrillsAndCountsNoneIncomplete()
    {
        // Schema 5 added the count of drills that did not complete, as a
        // plain additive column. A schema-4 row reads it as zero, which is
        // true of it: nothing counted them.
        var path = Path.Combine(_state, "destinations.json");
        File.WriteAllText(path, """
            { "schema_version": 4, "destinations": [
                { "set": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "destination": "vault", "state": "InSync",
                  "last_attempt_at": 1000, "last_success_at": 1000, "synced_sequence": 42,
                  "drilled_at": 2000, "drill_files": 0, "drill_bytes": 0,
                  "drill_failure": "the drill did not complete: an older fault" } ] }
            """);

        var record = DestinationSyncStore.Open(_state).Find(SetId, "vault");

        Assert.IsNotNull(record, "a schema-4 ledger must migrate, not quarantine");
        Assert.IsFalse(File.Exists(path + ".corrupt"));
        Assert.AreEqual(2_000UL, record.DrilledAt);
        Assert.AreEqual("the drill did not complete: an older fault", record.DrillFailure);
        Assert.AreEqual(0, record.ConsecutiveIncompleteDrills);
    }

    [TestMethod]
    public void RecordIncompleteDrill_StampsTheTry_CountsIt_AndACompletedDrillEndsTheCount()
    {
        // ADR-0054 Amendment 4. A drill that did not complete is a failed
        // drill on the row like any other, stamp and reason and all, and it
        // is counted: the count is what brings the next drill forward and
        // backs it off while the fault lasts, so it has to outlive a restart.
        var store = DestinationSyncStore.Open(_state);
        store.RecordSuccess(SetId, "vault", objects: 3, nowUnixMilliseconds: 1_000, syncedSequence: 1);

        store.RecordIncompleteDrill(SetId, "vault", "the drill did not complete: listing the snapshot was cancelled", 2_000);
        store.RecordIncompleteDrill(SetId, "vault", "the drill did not complete: restoring 'a' was cancelled", 3_000);

        var twice = DestinationSyncStore.Open(_state).Find(SetId, "vault")!;
        Assert.AreEqual(2, twice.ConsecutiveIncompleteDrills);
        Assert.AreEqual(3_000UL, twice.DrilledAt);
        Assert.AreEqual("the drill did not complete: restoring 'a' was cancelled", twice.DrillFailure);
        Assert.AreEqual(0, twice.DrillFiles);
        Assert.IsNull(twice.DrillLimit);
        Assert.AreEqual(1_000UL, twice.LastSuccessAt, "the sync half of the row is left alone");

        store.RecordDrill(SetId, "vault", files: 1, bytes: 10, failure: null, limit: null, nowUnixMilliseconds: 4_000);
        Assert.AreEqual(0, store.Find(SetId, "vault")!.ConsecutiveIncompleteDrills, "a drill that completes ends the count");
    }

    [TestMethod]
    public void Open_AFileOneSchemaAhead_IsSetAside()
    {
        // The downgrade rule, pinned at the edge rather than at 99: the very
        // next schema is already foreign to this build.
        var path = Path.Combine(_state, "destinations.json");
        File.WriteAllText(path, """
            { "schema_version": 8, "destinations": [
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
        Assert.Contains("\"schema_version\": 7", text, StringComparison.Ordinal);

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
