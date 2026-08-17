using FallbackPlan.Domain;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// A crash's void-delta obligations are discharged exactly once
/// (specification 07 §4): the numbers a dead run allocated and never
/// accounted for get one void delta each, not one per subsequent
/// publication. The long-lived service holds a single writer sequence for
/// its process lifetime, so an obligation that never leaves the recovered
/// list is republished by every run the process ever makes — junk a store
/// nothing collects yet keeps forever.
/// </summary>
[TestClass]
public sealed class VoidObligationTests : InterruptionHarness
{
    [TestMethod]
    public async Task Publication_TwoRunsInOneProcess_DischargeEachCrashObligationOnce()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        // The durable state a crashed run leaves: sequence 5 allocated,
        // nothing published for it.
        Directory.CreateDirectory(SpoolDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(SpoolDirectory, "sequence.txt"),
            "next=6\npending=5\nlast-delta=\n");

        // One orchestrator = one process life = one writer sequence, which
        // is the ServiceRuntime shape: the sequence lives as long as the
        // service, not as long as a job.
        var orchestrator = CreateOrchestrator(store, keys, hierarchy);

        using (var source = new MemoryStream(BuildFile(seed: 1)))
        {
            await orchestrator.PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        using (var source = new MemoryStream(BuildFile(seed: 2)))
        {
            await orchestrator.PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None);
        }

        Assert.AreEqual(1, await CountVoidDeltasAsync(store, keys, sequence: 5),
            "an obligation discharged by the first run must not be republished by the second");
    }

    private static async Task<int> CountVoidDeltasAsync(LocalFileSystemObjectStore store, RepositoryKeySet keys, ulong sequence)
    {
        var count = 0;

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

            if (decoded.Delta.IsVoid && decoded.Delta.Sequence == sequence)
            {
                count++;
            }
        }

        return count;
    }
}
