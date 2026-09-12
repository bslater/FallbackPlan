using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Index;
using FallbackPlan.TestSupport;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// The writer's sequence file regresses — the state a power loss leaves when
/// <c>sequence.txt</c> was replaced but the replacement never reached the
/// platter. Every number the previous run consumed is then handed out again:
/// the write intent's journal key, the blob counters, and the delta sequence
/// all collide with objects the store already holds (specification 08 §2;
/// architecture 04 §2 classifies the result as identity cloning).
/// </summary>
/// <remarks>
/// The store is immutable under a live key, so the colliding puts do not
/// corrupt the earlier run — they are answered <c>AlreadyExists</c> and the
/// earlier bytes survive (05 §5.1). The danger is the writer treating that
/// answer as durability for <em>its</em> bytes: it would then publish an
/// index delta and a snapshot describing records the store never received.
/// A writer that cannot establish upload success may only re-put
/// byte-identical content (05 §5.1); anything else must refuse, and these
/// hold that the refusal happens while the source data still exists.
/// </remarks>
[TestClass]
public sealed class SequenceRollbackTests : InterruptionHarness
{
    private string SequencePath => Path.Combine(SpoolDirectory, "sequence.txt");

    [TestMethod]
    public async Task Publication_TheSequenceFileRegressed_RefusesRatherThanRepublishingUnderUsedNumbers()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var first = BuildFile(seed: 1);
        using (var source = new MemoryStream(first))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        // The state a power loss preserves: the file as it was before the
        // second run's replacement reached the disk.
        var preSecondRun = await File.ReadAllBytesAsync(SequencePath);

        var second = BuildFile(seed: 2);
        using (var source = new MemoryStream(second))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None);
        }

        await File.WriteAllBytesAsync(SequencePath, preSecondRun);

        // The regressed writer's first allocation is the write intent, whose
        // journal key the second run already used. The store still holds the
        // second run's record there, so treating the put as success would
        // mean starting a publication whose intent was never written.
        var third = BuildFile(seed: 3);
        using (var source = new MemoryStream(third))
        {
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xC3), CancellationToken.None));
        }

        // The refusal must come before anything is published: no third
        // snapshot, and both committed snapshots still restore.
        Assert.AreEqual(2, CountUnder("snapshots"));
        SequenceAssert.AreEqual(first, await RestoreSnapshotAsync(store, keys, 0xA1));
        SequenceAssert.AreEqual(second, await RestoreSnapshotAsync(store, keys, 0xB2));
    }

    [TestMethod]
    public async Task Publication_TheSequenceRegressedAndTheJournalWasPruned_RefusesAtTheBlobUpload()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var first = BuildFile(seed: 1);
        using (var source = new MemoryStream(first))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        var preSecondRun = await File.ReadAllBytesAsync(SequencePath);

        var second = BuildFile(seed: 2);
        using (var source = new MemoryStream(second))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None);
        }

        await File.WriteAllBytesAsync(SequencePath, preSecondRun);

        // A future collector prunes retired journal records (07 §7 gives
        // deltas the same lifecycle), so the journal collision cannot be the
        // only tripwire. With the journal keys free again, the intent put
        // succeeds and the first collision is the blob itself: same writer,
        // same counter, a different salt — different bytes under a key the
        // store already holds.
        Directory.Delete(Path.Combine(StoreRoot, "journal"), recursive: true);

        var third = BuildFile(seed: 3);
        using (var source = new MemoryStream(third))
        {
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xC3), CancellationToken.None));
        }

        Assert.AreEqual(2, CountUnder("snapshots"));
        SequenceAssert.AreEqual(first, await RestoreSnapshotAsync(store, keys, 0xA1));
        SequenceAssert.AreEqual(second, await RestoreSnapshotAsync(store, keys, 0xB2));
    }

    [TestMethod]
    public async Task Publication_TheObservedHeadIsAdoptedFirst_RecoversInsteadOfColliding()
    {
        // The two tests above are the last line of defence, and they hold: a
        // writer that has silently regressed refuses rather than publishing
        // over history. But refusing is all they can do, because the collision
        // is discovered mid-publication, by a store answering AlreadyExists to
        // bytes it will not accept. Nothing had ever asked the repository what
        // sequence it attests.
        //
        // It does attest one. Every checkpoint carries a per-writer watermark
        // and every applied delta carries its sequence (07 §§5-6), all signed,
        // and all of it lives at every destination rather than in the state
        // directory that was lost. Consulting it before publishing turns the
        // collision into an adoption (NFR-SEC-005).
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var first = BuildFile(seed: 1);
        using (var source = new MemoryStream(first))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        var preSecondRun = await File.ReadAllBytesAsync(SequencePath);

        var second = BuildFile(seed: 2);
        using (var source = new MemoryStream(second))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None);
        }

        // The regression, and then the witness pass a caller makes before it
        // hands the sequence to a publication.
        await File.WriteAllBytesAsync(SequencePath, preSecondRun);

        var sequence = new WriterSequence(new FileSequenceStateStore(SequencePath));
        var index = await new IndexLoader(store, Repo, hierarchy).LoadAsync(
            currentGeneration: 0, gapPatienceGenerations: 0, isSequenceAccountedAsync: null,
            blobState: null, CancellationToken.None);
        var head = await ObservedHead.OfAsync(store, Writer, index, CancellationToken.None);

        Assert.IsGreaterThan(0UL, head, "the repository must attest a head for the writer that published twice");

        var adoption = sequence.AdoptObservedHead(head);

        Assert.IsInstanceOfType<SequenceAdoption.Adopted>(adoption, out var adopted);
        Assert.IsTrue(
            adopted.To > adopted.From,
            $"the head must move forward, not from {adopted.From} to {adopted.To}");

        // Having adopted, the writer publishes rather than colliding: the
        // numbers it hands out are above everything the repository holds.
        var third = BuildFile(seed: 3);
        using (var source = new MemoryStream(third))
        {
            await CreateOrchestrator(store, keys, hierarchy, sequence: sequence)
                .PublishAsync(Job(source, snapshotSeed: 0xC3), CancellationToken.None);
        }

        Assert.AreEqual(3, CountUnder("snapshots"));
        SequenceAssert.AreEqual(first, await RestoreSnapshotAsync(store, keys, 0xA1));
        SequenceAssert.AreEqual(second, await RestoreSnapshotAsync(store, keys, 0xB2));
        SequenceAssert.AreEqual(third, await RestoreSnapshotAsync(store, keys, 0xC3));
    }

    [TestMethod]
    public async Task ObservedHead_AWriterAlreadyAhead_IsLeftAlone()
    {
        // Adoption only ever raises. A writer whose local state is ahead of
        // what the repository attests is the ordinary case — numbers are
        // allocated before the objects accounting for them are published —
        // and lowering to the published head would hand out numbers that are
        // already in flight.
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        using (var source = new MemoryStream(BuildFile(seed: 1)))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        var sequence = new WriterSequence(new FileSequenceStateStore(SequencePath));
        var index = await new IndexLoader(store, Repo, hierarchy).LoadAsync(
            currentGeneration: 0, gapPatienceGenerations: 0, isSequenceAccountedAsync: null,
            blobState: null, CancellationToken.None);

        Assert.IsInstanceOfType<SequenceAdoption.AlreadyAhead>(
            sequence.AdoptObservedHead(await ObservedHead.OfAsync(store, Writer, index, CancellationToken.None)));

        // And a writer the repository has never heard of keeps its own state.
        var stranger = WriterId.FromBytes(Convert.FromHexString("b0b1b2b3b4b5b6b7b8b9babbbcbdbebf"));
        Assert.AreEqual(0UL, await ObservedHead.OfAsync(store, stranger, index, CancellationToken.None));
    }
}
