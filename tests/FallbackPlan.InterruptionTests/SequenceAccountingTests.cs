using FallbackPlan.Repository;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Domain;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// The writer's side of sequence accounting (specification 07 §4; ADR-0022
/// §Decision 7; ADR-0029 §4): a publication that ran to completion has an
/// accounting object for every number it allocated — so it owes nothing to
/// the next run — and a run that lost a number to a fault or a cancellation
/// has that number voided by the <em>next publication</em>, not the next
/// process restart.
/// </summary>
[TestClass]
public sealed class SequenceAccountingTests : InterruptionHarness
{
    [TestMethod]
    public async Task Publication_RunsToCompletion_AccountsForEveryNumberItAllocated()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        using (var source = new MemoryStream(BuildFile(seed: 1)))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        // Every number the run drew — journal records, blob counters, the
        // delta — has its accounting object (ADR-0022 §Decision 7), and the
        // standalone snapshot consumes no number of its own, hint-style. A
        // number still pending here would be voided by the next run: a void
        // delta falsely claiming a number a durable object embeds was skipped.
        Assert.AreEqual(string.Empty, await ReadPendingAsync());

        using (var source = new MemoryStream(BuildFile(seed: 2)))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None);
        }

        // The fresh process life found nothing to void: no interruption
        // happened, so no void delta may exist (07 §4 voids are for numbers
        // allocated and never used, and this world has none).
        SequenceAssert.AreEqual([], await ListVoidDeltaSequencesAsync(store, keys));
    }

    [TestMethod]
    public async Task Publication_LosesABlobToAStoreFault_TheSameProcessVoidsItsNumberOnTheNextRun()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var faulting = new PutFaultingObjectStore(store);

        // One orchestrator = one process life = one writer sequence — the
        // ServiceRuntime shape, where the sequence outlives the job. The
        // first run allocates: 1 the intent record, 2 the blob's counter,
        // 3 the covering extension; the blob's own put then dies, so number
        // 2 has no accounting object and never will.
        var orchestrator = CreateOrchestrator(faulting, keys, hierarchy);

        faulting.Arm(key => key.StartsWith("blobs/", StringComparison.Ordinal));
        using (var source = new MemoryStream(BuildFile(seed: 1, regions: 1)))
        {
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await orchestrator.PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None));
        }

        faulting.Heal();
        using (var source = new MemoryStream(BuildFile(seed: 2, regions: 1)))
        {
            await orchestrator.PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None);
        }

        // ADR-0029 §4: a cancelled or failed run's numbers "become void-delta
        // obligations discharged by the next publication, exactly as a
        // crash's would" — the next publication, not the next process. A
        // reader must find number 2 accounted without waiting for a restart
        // that a long-lived service may not have for weeks.
        Assert.Contains(2UL, await ListVoidDeltaSequencesAsync(store, keys),
            "the lost blob counter was not voided by the next publication in the same process — "
            + "only a restart would discharge it (ADR-0029 §4).");
    }

    private async Task<string> ReadPendingAsync()
    {
        var lines = await File.ReadAllLinesAsync(Path.Combine(SpoolDirectory, "sequence.txt"), CancellationToken.None);
        return lines.Single(line => line.StartsWith("pending=", StringComparison.Ordinal))["pending=".Length..];
    }

    private static async Task<List<ulong>> ListVoidDeltaSequencesAsync(Storage.Local.LocalFileSystemObjectStore store, RepositoryKeySet keys)
    {
        var sequences = new List<ulong>();

        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("index/"), ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(entry.Key, range: null, CancellationToken.None);
            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory);

            var record = StandaloneRecordFraming.Parse(memory.ToArray());
            var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, record.KeyGeneration);
            if (!StandaloneRecordCipher.TryOpen(record, Repo, metadataKey, out var plaintext))
            {
                continue;
            }

            DecodedDelta decoded;
            try
            {
                decoded = IndexDeltaCodec.Decode(plaintext);
            }
            catch (IndexFormatException)
            {
                // A checkpoint or another index-plane object, not a delta.
                continue;
            }

            if (decoded.Delta.IsVoid)
            {
                sequences.Add(decoded.Delta.Sequence);
            }
        }

        return sequences;
    }
}
