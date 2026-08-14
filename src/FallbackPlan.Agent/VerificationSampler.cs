using Bodu;
using FallbackPlan.Protocol;
using FallbackPlan.Replication;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Agent;

/// <summary>
/// Chooses which keys and ranges a verification pass challenges
/// (specification peer-protocol 04 §3): a bounded random sample of what the
/// destination is expected to hold, with the newest snapshot always included
/// so the most recent recovery point is the best-verified one (FR-VER-002).
/// </summary>
/// <remarks>
/// The sample is drawn from the staging listing taken <b>before</b> the copy
/// begins, filtered by the destination's keep filter. Both halves matter: a
/// key published mid-sync may not have crossed yet, and a key the
/// destination's own retention policy drops is legitimately absent there —
/// sampling either would manufacture a verification failure out of a healthy
/// destination.
/// </remarks>
internal static class VerificationSampler
{
    /// <summary>Samples per pass — bounded so verification stays cheap enough to run every sync.</summary>
    public const int DefaultBudget = 16;

    /// <summary>The range length aimed for; shorter objects are challenged whole.</summary>
    private const uint PreferredLength = 4096;

    /// <summary>
    /// What one pass will challenge, and how much it could have: the
    /// population is the coverage denominator status reports (FR-VER-003).
    /// </summary>
    /// <param name="Samples">The keys and ranges to challenge.</param>
    /// <param name="Population">Objects that were eligible when the sample was drawn.</param>
    public sealed record SamplePlan(IReadOnlyList<VerificationSample> Samples, int Population);

    /// <summary>
    /// Draws up to <paramref name="budget"/> samples from the source listing.
    /// </summary>
    /// <param name="source">The staging archive's store.</param>
    /// <param name="keeps">
    /// The destination's keep filter, when it converges under a retention
    /// policy; null means the destination holds the whole archive.
    /// </param>
    /// <param name="newestSnapshotKey">
    /// The newest snapshot's store key, always sampled when present so the
    /// verification stamp genuinely covers the latest publication.
    /// </param>
    /// <param name="budget">The most samples to draw.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The samples — at most <paramref name="budget"/> — with the eligible population.</returns>
    public static async Task<SamplePlan> SampleAsync(
        IObjectStore source,
        Func<string, bool>? keeps,
        string? newestSnapshotKey,
        int budget,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfLessThan(budget, 1);

        VerificationSample? newest = null;
        var reservoir = new List<VerificationSample>();
        var seen = 0;

        await foreach (var entry in source.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            var key = entry.Key.Value;
            if (entry.Length <= 0 || IsStagingOnly(key) || (keeps is not null && !keeps(key)))
            {
                // Zero-length objects have no range to challenge; lifecycle
                // objects never replicate; dropped keys are legitimately
                // absent at this destination.
                continue;
            }

            if (string.Equals(key, newestSnapshotKey, StringComparison.Ordinal))
            {
                newest = RangeFor(key, entry.Length);
                continue;
            }

            // Reservoir sampling: every eligible key gets an equal chance
            // without holding the whole listing in memory.
            seen++;
            var slots = budget - (newestSnapshotKey is null ? 0 : 1);
            if (reservoir.Count < slots)
            {
                reservoir.Add(RangeFor(key, entry.Length));
            }
            else if (slots > 0 && Random.Shared.Next(seen) < slots)
            {
                reservoir[Random.Shared.Next(slots)] = RangeFor(key, entry.Length);
            }
        }

        if (newest is not null)
        {
            reservoir.Insert(0, newest);
        }

        return new SamplePlan(reservoir, seen + (newest is null ? 0 : 1));
    }

    /// <summary>A random range inside an object of the given length.</summary>
    /// <param name="key">The store key.</param>
    /// <param name="objectLength">The object's total length; must be positive.</param>
    public static VerificationSample RangeFor(string key, long objectLength)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(key);
        ThrowHelper.ThrowIfLessThan(objectLength, 1L);

        var length = (uint)Math.Min(Math.Min(objectLength, PreferredLength), VerificationChallenge.MaximumLength);
        var offset = (ulong)Random.Shared.NextInt64(0, objectLength - length + 1);
        return new VerificationSample(key, offset, length);
    }

    private static bool IsStagingOnly(string key) =>
        key.StartsWith("tombstones/", StringComparison.Ordinal)
        || key.StartsWith("leases/", StringComparison.Ordinal);
}
