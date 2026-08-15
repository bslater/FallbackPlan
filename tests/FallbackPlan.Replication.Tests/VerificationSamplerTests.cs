using FallbackPlan.Replication;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Replication.Tests;

/// <summary>
/// The rotation's two failure modes — skipping a key and re-asking about one
/// it already covered — are both invisible from outside: the pass still
/// reports a clean sweep of whatever it happened to ask for, and the ledger
/// still shows a verification stamp. Nothing downstream can catch either. So
/// they are caught here, by walking whole circuits and checking what was
/// asked (FR-VER-002).
/// </summary>
[TestClass]
public sealed class VerificationSamplerTests
{
    /// <summary>The wire's bound, passed in by the agent; any positive value exercises the same clamp.</summary>
    private const uint MaximumRangeLength = 65_536;

    [TestMethod]
    public async Task Sample_WithNoCursor_TakesTheSmallestKeysAndSaysWhereItStopped()
    {
        var store = ListingStore.Of(Keys(20));

        var plan = await SampleAsync(store, cursor: null, budget: 5);

        CollectionAssert.AreEqual(
            new[] { "blobs/k00", "blobs/k01", "blobs/k02", "blobs/k03", "blobs/k04" },
            plan.Samples.Select(sample => sample.Key).ToArray());
        Assert.AreEqual("blobs/k04", plan.NextCursor);
        Assert.AreEqual(20, plan.Population);
    }

    [TestMethod]
    public async Task Sample_TheListingOrder_DoesNotDecideTheRotation()
    {
        // StoreToStoreCopier says outright that listing order carries no
        // meaning. A rotation that trusted it would advance its cursor past
        // keys it never sampled — and because the cursor only ever moves
        // forward, those keys would never be challenged again.
        var shuffled = Keys(20).OrderByDescending(key => key, StringComparer.Ordinal).ToArray();
        var store = ListingStore.Of(shuffled);

        var plan = await SampleAsync(store, cursor: null, budget: 5);

        CollectionAssert.AreEqual(
            new[] { "blobs/k00", "blobs/k01", "blobs/k02", "blobs/k03", "blobs/k04" },
            plan.Samples.Select(sample => sample.Key).ToArray());
    }

    [TestMethod]
    public async Task Sample_AcrossSuccessivePasses_CoversEveryObjectExactlyOnce()
    {
        // The whole point of the cursor. A stateless sample of 5 from 20 would
        // in expectation still be missing objects after four passes, forever.
        var store = ListingStore.Of(Keys(20));
        var asked = new List<string>();

        string? cursor = null;
        for (var pass = 0; pass < 4; pass++)
        {
            var plan = await SampleAsync(store, cursor, budget: 5);
            asked.AddRange(plan.Samples.Select(sample => sample.Key));
            cursor = plan.NextCursor;
        }

        CollectionAssert.AreEquivalent(Keys(20), asked.ToArray());
        Assert.AreEqual(asked.Count, asked.Distinct(StringComparer.Ordinal).Count(), "no object was asked about twice");
        Assert.IsNull(cursor, "the fourth pass closed the circuit");
    }

    [TestMethod]
    public async Task Sample_WhenTheCursorHasRunOffTheEnd_WrapsWithinTheSamePass()
    {
        // Not a cosmetic nicety: a pass that returned nothing would write no
        // verification stamp, so the trim gate would find the destination
        // unproven and stop reclaiming space — over a rotation that had simply
        // finished a lap.
        var store = ListingStore.Of(Keys(20));

        var plan = await SampleAsync(store, cursor: "blobs/zzz", budget: 5);

        CollectionAssert.AreEqual(
            new[] { "blobs/k00", "blobs/k01", "blobs/k02", "blobs/k03", "blobs/k04" },
            plan.Samples.Select(sample => sample.Key).ToArray());
        Assert.AreEqual("blobs/k04", plan.NextCursor);
    }

    [TestMethod]
    public async Task Sample_ACursorNamingATrimmedObject_ResumesAfterWhereItWouldHaveBeen()
    {
        // The cursor is a key rather than an index precisely so that a blob
        // deleted between passes costs nothing: it is compared against, never
        // looked up.
        var store = ListingStore.Of(Keys(20).Where(key => key != "blobs/k05").ToArray());

        var plan = await SampleAsync(store, cursor: "blobs/k05", budget: 3);

        CollectionAssert.AreEqual(
            new[] { "blobs/k06", "blobs/k07", "blobs/k08" },
            plan.Samples.Select(sample => sample.Key).ToArray());
    }

    [TestMethod]
    public async Task Sample_APopulationThatFitsTheBudget_ReportsNoCursorAtAll()
    {
        var store = ListingStore.Of(Keys(3));

        var plan = await SampleAsync(store, cursor: null, budget: 5);

        Assert.HasCount(3, plan.Samples);
        Assert.IsNull(plan.NextCursor, "everything eligible was covered, so the next pass starts over");
    }

    [TestMethod]
    public async Task Sample_TheNewestSnapshot_IsAlwaysAskedAboutAndDoesNotStallTheRotation()
    {
        // FR-VER-002's forced half. It must not consume a rotation slot in a
        // way that leaves the rotation standing still, or the newest snapshot
        // would be the only object ever proven.
        var store = ListingStore.Of([.. Keys(20), "snapshots/newest"]);

        var first = await SampleAsync(store, cursor: null, budget: 5, newestSnapshotKey: "snapshots/newest");
        var second = await SampleAsync(
            store, first.NextCursor, budget: 5, newestSnapshotKey: "snapshots/newest");

        Assert.AreEqual("snapshots/newest", first.Samples[0].Key);
        Assert.AreEqual("snapshots/newest", second.Samples[0].Key);
        CollectionAssert.AreEqual(
            new[] { "snapshots/newest", "blobs/k00", "blobs/k01", "blobs/k02", "blobs/k03" },
            first.Samples.Select(sample => sample.Key).ToArray());
        CollectionAssert.AreEqual(
            new[] { "snapshots/newest", "blobs/k04", "blobs/k05", "blobs/k06", "blobs/k07" },
            second.Samples.Select(sample => sample.Key).ToArray());
        Assert.AreEqual(21, first.Population, "the newest snapshot counts toward what could have been sampled");
    }

    [TestMethod]
    public async Task Sample_APeersReservoirShare_LeavesTheRotationIntactAndNeverDoublesUp()
    {
        // A peer answers its own challenge, so part of the budget stays
        // unpredictable. That share comes out of the rotation's slots rather
        // than being added on top, and a reservoir pick that lands on a key
        // the rotation already holds is dropped instead of spending two slots
        // on one object.
        var store = ListingStore.Of(Keys(20));

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var plan = await VerificationSampler.SampleAsync(
                store, keeps: null, newestSnapshotKey: null, cursor: null, budget: 5, reservoirShare: 2,
                MaximumRangeLength, CancellationToken.None);

            var keys = plan.Samples.Select(sample => sample.Key).ToArray();
            CollectionAssert.AreEqual(
                new[] { "blobs/k00", "blobs/k01", "blobs/k02" },
                keys.Take(3).ToArray(),
                "the rotation keeps its slots");
            Assert.AreEqual(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
            Assert.IsLessThanOrEqualTo(5, keys.Length);
        }
    }

    [TestMethod]
    public async Task Sample_TheKeepFilter_HidesDroppedKeysFromTheRotationAsWellAsTheSample()
    {
        // A key the destination's retention policy drops is legitimately
        // absent there. It must not be sampled — and it must not be allowed to
        // set the cursor either, or the next pass would resume past keys the
        // destination does hold.
        var store = ListingStore.Of(Keys(20));

        var plan = await SampleAsync(
            store, cursor: null, budget: 3, keeps: key => !key.EndsWith('0'));

        CollectionAssert.AreEqual(
            new[] { "blobs/k01", "blobs/k02", "blobs/k03" },
            plan.Samples.Select(sample => sample.Key).ToArray());
        Assert.AreEqual(18, plan.Population);
    }

    [TestMethod]
    public async Task Sample_LifecycleObjectsAndEmptyOnes_AreNeverAskedAbout()
    {
        // Preserved from before the rotation existed: tombstones and leases
        // never replicate, and a zero-length object has no range to challenge.
        var store = new ListingStore(
        [
            new ObjectEntry(ObjectKey.Parse("blobs/k00"), 0, string.Empty),
            new ObjectEntry(ObjectKey.Parse("leases/one"), 4096, string.Empty),
            new ObjectEntry(ObjectKey.Parse("tombstones/one"), 4096, string.Empty),
            new ObjectEntry(ObjectKey.Parse("blobs/k01"), 4096, string.Empty),
        ]);

        var plan = await SampleAsync(store, cursor: null, budget: 8);

        CollectionAssert.AreEqual(new[] { "blobs/k01" }, plan.Samples.Select(sample => sample.Key).ToArray());
        Assert.AreEqual(1, plan.Population);
    }

    [TestMethod]
    public void RangeFor_AnObjectShorterThanThePreferredRange_IsChallengedWhole()
    {
        var sample = VerificationSampler.RangeFor("blobs/k00", objectLength: 10, MaximumRangeLength);

        Assert.AreEqual(0uL, sample.Offset);
        Assert.AreEqual(10u, sample.Length);
    }

    [TestMethod]
    public void RangeFor_TheWireBound_ClampsTheRange()
    {
        var sample = VerificationSampler.RangeFor("blobs/k00", objectLength: 1_000_000, maximumRangeLength: 512);

        Assert.AreEqual(512u, sample.Length);
        Assert.IsLessThanOrEqualTo(1_000_000uL - 512, sample.Offset);
    }

    private static Task<VerificationSampler.SamplePlan> SampleAsync(
        IObjectStore store,
        string? cursor,
        int budget,
        string? newestSnapshotKey = null,
        Func<string, bool>? keeps = null) =>
        VerificationSampler.SampleAsync(
            store, keeps, newestSnapshotKey, cursor, budget, reservoirShare: 0, MaximumRangeLength,
            CancellationToken.None);

    private static string[] Keys(int count) =>
        [.. Enumerable.Range(0, count).Select(index => $"blobs/k{index:D2}")];

    /// <summary>
    /// A store that answers a listing and nothing else, in exactly the order
    /// it was given — so a test can hand the sampler an order that is not
    /// sorted and see what it does with it.
    /// </summary>
    private sealed class ListingStore(IReadOnlyList<ObjectEntry> entries) : IObjectStore
    {
        public static ListingStore Of(IEnumerable<string> keys) =>
            new([.. keys.Select(key => new ObjectEntry(ObjectKey.Parse(key), 4096, string.Empty))]);

        public StoreCapabilities Capabilities => new();

        public async IAsyncEnumerable<ObjectEntry> ListAsync(
            ObjectPrefix prefix,
            ListOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OpenReadResult> OpenReadAsync(
            ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<PutResult> PutAsync(
            ObjectKey key,
            Func<CancellationToken, ValueTask<Stream>> openContent,
            PutConditions conditions,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DeleteResult> DeleteAsync(
            ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
