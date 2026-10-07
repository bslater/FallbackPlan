using Bodu;

namespace FallbackPlan.Repository;

/// <summary>
/// How many blob numbers a publication reserves at a time
/// ([ADR-0092](../../docs/adr/0092-a-backup-names-its-blobs-a-batch-at-a-time.md)):
/// its write intent names the first batch, and each extension names the
/// next, twice the size of the one before until <see cref="LargestBatch"/>.
/// </summary>
/// <remarks>
/// <para>
/// A blob must be named by a durable intent before it is uploaded (08 §3.1).
/// Naming each in an extension of its own made every blob two requests.
/// Naming them a batch at a time makes a small backup's covers free, because
/// its intent has to be written anyway, and a large one's an extension per
/// 64 blobs.
/// </para>
/// <para>
/// The cap bounds what a run that dies can owe: the numbers it reserved and
/// did not use, each a void delta on the next run (07 §4). Doubling from a
/// small first batch keeps that small for a small backup, and the cap keeps
/// it at most 63 for any backup.
/// </para>
/// </remarks>
public static class BlobCounterReservation
{
    /// <summary>The numbers a write intent names.</summary>
    public const int FirstBatch = 8;

    /// <summary>The most numbers one extension names.</summary>
    public const int LargestBatch = 64;

    /// <summary>The size of the batch reserved after one of <paramref name="previous"/> numbers runs out.</summary>
    /// <param name="previous">The size of the batch that ran out.</param>
    /// <returns>Twice <paramref name="previous"/>, but no more than <see cref="LargestBatch"/>.</returns>
    public static int After(int previous)
    {
        ThrowHelper.ThrowIfZeroOrNegative(previous);
        return previous >= LargestBatch / 2 ? LargestBatch : previous * 2;
    }

    /// <summary>The extensions a publication of <paramref name="blobs"/> blobs writes to name them all.</summary>
    /// <param name="blobs">How many blobs the publication writes.</param>
    /// <returns>None while the intent's batch holds them, then one for each batch after it.</returns>
    public static int ExtensionsFor(int blobs)
    {
        ThrowHelper.ThrowIfNegative(blobs);

        var extensions = 0;
        var batch = FirstBatch;
        var named = FirstBatch;
        while (named < blobs)
        {
            batch = After(batch);
            named += batch;
            extensions++;
        }

        return extensions;
    }
}
