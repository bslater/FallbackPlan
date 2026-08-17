using FallbackPlan.Replication;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// The copier's ordering discipline under interruption (ADR-0012 Amendment 2,
/// ADR-0034 §2): killed after any number of puts, the destination is a
/// lagging-but-valid replica — nothing present references anything absent,
/// and a snapshot object's presence means everything else already arrived.
/// The copier moves opaque keys, so the archive here is synthetic: what is
/// under test is the order, not the bytes.
/// </summary>
[TestClass]
public sealed class StoreCopyOrderTests : IDisposable
{
    private static readonly string[] SourceKeys =
    [
        // In ordinal (store listing) order, which is NOT a safe copy order:
        // it would move index and journal records before the descriptor and
        // keys exist at the destination. The copier's phases must override it.
        "blobs/data/b1",
        "blobs/meta/m1",
        "index/checkpoint/c1",
        "index/delta/d1",
        "journal/w1/000001",
        "keys/current",
        "repository-format",
        "snapshots/s1",
        "snapshots/s2",
    ];

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-copy-order", Guid.NewGuid().ToString("n"));

    [TestMethod]
    public async Task Copy_KilledAfterEveryPossiblePutCount_LeavesALaggingButValidReplica()
    {
        var sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        var source = new LocalFileSystemObjectStore(sourceRoot);
        foreach (var key in SourceKeys)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes($"object {key}");
            await source.PutAsync(
                ObjectKey.Parse(key),
                _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes)),
                PutConditions.None,
                CancellationToken.None);
        }

        for (var budget = 0; budget <= SourceKeys.Length; budget++)
        {
            var destinationRoot = Path.Combine(_root, $"destination-{budget}");
            Directory.CreateDirectory(destinationRoot);
            var destination = new LocalFileSystemObjectStore(destinationRoot);
            var faulting = new FaultInjectingObjectStore(destination, budget);

            try
            {
                await StoreToStoreCopier.CopyAsync(source, faulting, CancellationToken.None);
                Assert.AreEqual(SourceKeys.Length, budget, "only the unbounded run should complete");
            }
            catch (IOException) when (budget < SourceKeys.Length)
            {
                // The injected kill. What survives is the claim.
            }

            var held = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var entry in destination.ListAsync(
                ObjectPrefix.All, ListOptions.Default, CancellationToken.None))
            {
                held.Add(entry.Key.Value);
            }

            // Identity first: a replica holding anything holds the descriptor.
            if (held.Count > 0)
            {
                Assert.Contains("repository-format", held, $"budget {budget}");
            }

            // Blobs before the metadata that references them: any journal,
            // index or snapshot object present means every blob arrived.
            if (held.Any(key => key.StartsWith("journal/", StringComparison.Ordinal)
                || key.StartsWith("index/", StringComparison.Ordinal)
                || key.StartsWith("snapshots/", StringComparison.Ordinal)))
            {
                Assert.IsTrue(held.IsSupersetOf(SourceKeys.Where(key =>
                    key.StartsWith("blobs/", StringComparison.Ordinal))), $"budget {budget}");
            }

            // Snapshots last: a snapshot's presence asserts completeness —
            // ADR-0011's per-replica commit rule, enforced by copy order.
            if (held.Any(key => key.StartsWith("snapshots/", StringComparison.Ordinal)))
            {
                Assert.IsTrue(held.IsSupersetOf(SourceKeys.Where(key =>
                    !key.StartsWith("snapshots/", StringComparison.Ordinal))), $"budget {budget}");
            }
        }
    }

    [TestMethod]
    public async Task Copy_RunAgainAfterAKill_ConvergesWithoutRecopyingWhatArrived()
    {
        var sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        var source = new LocalFileSystemObjectStore(sourceRoot);
        foreach (var key in SourceKeys)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes($"object {key}");
            await source.PutAsync(
                ObjectKey.Parse(key),
                _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes)),
                PutConditions.None,
                CancellationToken.None);
        }

        var destinationRoot = Path.Combine(_root, "destination-resume");
        Directory.CreateDirectory(destinationRoot);
        var destination = new LocalFileSystemObjectStore(destinationRoot);

        // Killed part-way…
        try
        {
            await StoreToStoreCopier.CopyAsync(
                source, new FaultInjectingObjectStore(destination, putBudget: 3), CancellationToken.None);
            Assert.Fail("the injected fault should have fired");
        }
        catch (IOException)
        {
        }

        // …the re-run finishes the job from the destination's own inventory:
        // no checkpoint, and exactly the missing objects move (03 §5's story,
        // as a store-to-store property).
        var outcome = await StoreToStoreCopier.CopyAsync(source, destination, CancellationToken.None);
        Assert.AreEqual(SourceKeys.Length - 3, outcome.Copied);
        Assert.AreEqual(3, outcome.AlreadyHeld);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
