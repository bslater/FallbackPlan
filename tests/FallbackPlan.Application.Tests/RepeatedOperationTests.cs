namespace FallbackPlan.Application.Tests;

/// <summary>
/// The same durable operation applied twice (FR-DEST-004, NFR-REL-005).
/// </summary>
/// <remarks>
/// <para>
/// The surveyed changelog records a shipped fix for transactions being
/// committed twice, and NFR-REL-005 already asks for the property in the
/// abstract: maintenance operations shall be "idempotent where practical, and
/// safe to repeat". Repetition is not hypothetical here — a sync that
/// completes and then loses its acknowledgement is retried, a crash between
/// writing and recording replays the write, and an operator who runs a verb
/// twice because the first looked stuck is doing the ordinary thing.
/// </para>
/// <para>
/// The failure this guards is quiet: nothing throws, and a counter is simply
/// wrong afterwards. A doubled object count makes a destination look like it
/// holds twice what it holds, which is an input to capacity and to whether
/// retention may trim.
/// </para>
/// </remarks>
[TestClass]
public sealed class RepeatedOperationTests
{
    private string _directory = null!;

    [TestInitialize]
    public void Initialize() =>
        _directory = Directory.CreateTempSubdirectory("fp-repeat-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// Recording the same successful sync twice leaves one row saying what one
    /// sync did — counts replaced, never accumulated.
    /// </summary>
    [TestMethod]
    public void RecordingTheSameSyncTwice_DoesNotDoubleItsCounts()
    {
        const ulong At = 1_755_000_000_000;
        var store = DestinationSyncStore.Open(_directory);

        store.RecordSuccess("set-1", "vault", objects: 1_000, At, syncedSequence: 42);
        var once = store.RecordSuccess("set-1", "vault", objects: 1_000, At, syncedSequence: 42);
        var twice = store.RecordSuccess("set-1", "vault", objects: 1_000, At, syncedSequence: 42);

        Assert.AreEqual(once, twice, "the second application changed the row");
        Assert.AreEqual(1_000L, twice.Objects, "the object count accumulated instead of being replaced");
        Assert.AreEqual(42UL, twice.SyncedSequence);
        Assert.ContainsSingle(DestinationSyncStore.Open(_directory).Records);
    }

    /// <summary>
    /// And a repeat that carries a lower sequence must not walk the record
    /// backwards — a retry arriving out of order is the ordinary case on a
    /// flaky link.
    /// </summary>
    [TestMethod]
    public void AReplayCarryingAnOlderSequence_DoesNotUnwindTheRecord()
    {
        var store = DestinationSyncStore.Open(_directory);

        store.RecordSuccess("set-1", "vault", objects: 10, 1_755_000_000_000, syncedSequence: 99);
        var replayed = store.RecordSuccess("set-1", "vault", objects: 10, 1_755_000_100_000, syncedSequence: 7);

        Assert.AreEqual(99UL, replayed.SyncedSequence, "a late replay unwound the synced sequence");
    }

    /// <summary>
    /// A verification recorded twice counts one pass, not two — the coverage
    /// figures it feeds are a denominator and a numerator, and doubling either
    /// misreports how much of the destination is proven.
    /// </summary>
    [TestMethod]
    public void RecordingTheSameVerificationTwice_CountsOnePass()
    {
        const ulong At = 1_755_000_600_000;
        var store = DestinationSyncStore.Open(_directory);
        store.RecordSuccess("set-1", "vault", objects: 500, 1_755_000_000_000);

        var once = store.RecordVerification(
            "set-1", "vault", objects: 16, population: 4_096, verifiedSequence: 5, sampleCursor: null, At);
        var twice = store.RecordVerification(
            "set-1", "vault", objects: 16, population: 4_096, verifiedSequence: 5, sampleCursor: null, At);

        Assert.AreEqual(once, twice);
        Assert.AreEqual(16, twice.VerifiedObjects);
        Assert.AreEqual(4_096, twice.VerifiedPopulation);
    }

    /// <summary>
    /// Raising the same notice twice leaves one notice. A store that appended
    /// would fill the operator's console with the same sentence once per
    /// scheduler pass, which is how a real warning becomes invisible.
    /// </summary>
    [TestMethod]
    public void RaisingTheSameNoticeTwice_LeavesOneNotice()
    {
        var store = NoticeStore.Open(_directory);

        var first = store.Raise("dest/vault/behind", "vault is behind", 1_755_000_000_000);
        var second = store.Raise("dest/vault/behind", "vault is behind", 1_755_000_060_000);

        Assert.AreEqual(first.Id, second.Id, "the same key produced a second notice");
        Assert.ContainsSingle(NoticeStore.Open(_directory).Notices);
    }

    /// <summary>
    /// Resolving a notice twice is not an error, and does not resurrect it.
    /// The second call is what a retry looks like.
    /// </summary>
    [TestMethod]
    public void ResolvingANoticeTwice_IsHarmless()
    {
        var store = NoticeStore.Open(_directory);
        store.Raise("dest/vault/behind", "vault is behind", 1_755_000_000_000);

        Assert.IsTrue(store.Resolve("dest/vault/behind", 1_755_000_060_000));
        Assert.IsFalse(store.Resolve("dest/vault/behind", 1_755_000_120_000), "the second resolve claimed work");
        Assert.IsEmpty(NoticeStore.Open(_directory).Unacknowledged);
    }

    /// <summary>
    /// Writing the same file twice through the atomic primitive leaves the
    /// content once and no temporary behind. The interesting half is the
    /// second call: it replaces an existing file rather than creating one, and
    /// that is the path a retry takes.
    /// </summary>
    [TestMethod]
    public void WritingTheSameFileTwice_LeavesOneFileAndNoTemporaries()
    {
        var path = Path.Combine(_directory, "state.json");

        AtomicFile.WriteAllText(path, "{\"schema_version\":1}");
        AtomicFile.WriteAllText(path, "{\"schema_version\":1}");

        Assert.AreEqual("{\"schema_version\":1}", File.ReadAllText(path));
        SequenceAssertHelper.AssertOnlyFile(_directory, "state.json");
    }

    /// <summary>
    /// And a second write with different content replaces rather than appends
    /// — the same primitive, the case where a doubled apply would be visible
    /// as duplicated JSON rather than as a wrong number.
    /// </summary>
    [TestMethod]
    public void WritingDifferentContentTwice_ReplacesRatherThanAppends()
    {
        var path = Path.Combine(_directory, "state.json");

        AtomicFile.WriteAllText(path, "first");
        AtomicFile.WriteAllText(path, "second");

        Assert.AreEqual("second", File.ReadAllText(path));
        SequenceAssertHelper.AssertOnlyFile(_directory, "state.json");
    }

    private static class SequenceAssertHelper
    {
        /// <summary>Asserts the directory holds exactly the one named file.</summary>
        internal static void AssertOnlyFile(string directory, string name)
        {
            var found = Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            Assert.AreEqual(
                name,
                string.Join(", ", found),
                "a temporary or a duplicate survived the write");
        }
    }
}
