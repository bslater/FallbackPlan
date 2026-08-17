using Bodu;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Replication;

/// <summary>
/// Chooses which keys and ranges a verification pass challenges
/// (specification peer-protocol 04 §3): the newest snapshot always, then a
/// rotation through the rest of the key space, so coverage accumulates across
/// passes instead of re-asking the same questions forever (FR-VER-002).
/// </summary>
/// <remarks>
/// <para>
/// The sample is drawn from the staging listing taken <b>before</b> the copy
/// begins, filtered by the destination's keep filter. Both halves matter: a
/// key published mid-sync may not have crossed yet, and a key the
/// destination's own retention policy drops is legitimately absent there —
/// sampling either would manufacture a verification failure out of a healthy
/// destination.
/// </para>
/// <para>
/// This sits beside <see cref="ReplicaVerifier"/> rather than in the agent
/// because the rotation is the kind of logic that fails silently: skipping a
/// key and repeating a key look identical from outside, and neither shows up
/// in an end-to-end sync that passes. It needs to be reachable by a test that
/// can watch a whole circuit. The one thing that kept it in the agent — the
/// wire's maximum range length — is now a parameter, which makes the coupling
/// visible rather than removing it.
/// </para>
/// </remarks>
public static class VerificationSampler
{
    /// <summary>Samples per pass — bounded so verification stays cheap enough to run every sync.</summary>
    public const int DefaultBudget = 16;

    /// <summary>
    /// How much of a peer's budget stays random rather than following the
    /// rotation.
    /// </summary>
    /// <remarks>
    /// A pure rotation is predictable, and a destination that can predict
    /// which objects it will be asked for can hold exactly those and discard
    /// the rest. That threat only exists for a peer: a local-path replica's
    /// bytes are read by this hub off its own disk, so there is nobody there
    /// to game the question, and the whole budget goes to the rotation.
    /// </remarks>
    public const int PeerReservoirShare = 4;

    /// <summary>The range length aimed for; shorter objects are challenged whole.</summary>
    private const uint PreferredLength = 4096;

    /// <summary>
    /// What one pass will challenge, how much it could have, and where the
    /// next pass resumes.
    /// </summary>
    /// <param name="Samples">The keys and ranges to challenge.</param>
    /// <param name="Population">Objects that were eligible when the sample was drawn.</param>
    /// <param name="NextCursor">
    /// The highest key the rotation reached, or null when the rotation covered
    /// everything eligible and the next pass should start over.
    /// </param>
    public sealed record SamplePlan(IReadOnlyList<VerificationSample> Samples, int Population, string? NextCursor);

    /// <summary>
    /// Draws up to <paramref name="budget"/> samples from the source listing,
    /// resuming the rotation after <paramref name="cursor"/>.
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
    /// <param name="cursor">
    /// The highest key the previous pass challenged, or null to start at the
    /// beginning of the key space. A cursor naming a key that has since been
    /// trimmed still resumes correctly: it is only ever compared against,
    /// never looked up.
    /// </param>
    /// <param name="budget">The most samples to draw.</param>
    /// <param name="reservoirShare">
    /// How many of those samples are drawn at random from the whole eligible
    /// population instead of the rotation — <see cref="PeerReservoirShare"/>
    /// for a peer, zero for a local path.
    /// </param>
    /// <param name="maximumRangeLength">
    /// The longest range the destination will answer — the wire's bound
    /// (<c>VerificationChallenge.MaximumLength</c>), passed in because this
    /// assembly does not speak the protocol.
    /// </param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The samples — at most <paramref name="budget"/> — the eligible population, and the next cursor.</returns>
    public static async Task<SamplePlan> SampleAsync(
        IObjectStore source,
        Func<string, bool>? keeps,
        string? newestSnapshotKey,
        string? cursor,
        int budget,
        int reservoirShare,
        uint maximumRangeLength,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfLessThan(budget, 1);
        ThrowHelper.ThrowIfLessThan(reservoirShare, 0);
        ThrowHelper.ThrowIfLessThan(maximumRangeLength, 1u);

        var forced = newestSnapshotKey is null ? 0 : 1;
        var reservoirSlots = Math.Min(reservoirShare, Math.Max(0, budget - forced));
        var rotationSlots = Math.Max(0, budget - forced - reservoirSlots);

        VerificationSample? newest = null;

        // Two bounded windows, filled in one listing pass. `ahead` is the
        // rotation proper; `start` is what the rotation wraps to when the
        // cursor has run off the end of the key space — computing it here
        // costs one more bounded insert per key and saves a whole pass that
        // would otherwise challenge nothing at all.
        var ahead = new SortedList<string, VerificationSample>(StringComparer.Ordinal);
        var start = new SortedList<string, VerificationSample>(StringComparer.Ordinal);
        var reservoir = new List<VerificationSample>(reservoirSlots);

        var eligible = 0;
        var aheadSeen = 0;

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
                newest = RangeFor(key, entry.Length, maximumRangeLength);
                continue;
            }

            eligible++;

            // Reservoir sampling over the whole eligible population: every
            // key gets an equal chance without holding the listing in memory.
            if (reservoirSlots > 0)
            {
                if (reservoir.Count < reservoirSlots)
                {
                    reservoir.Add(RangeFor(key, entry.Length, maximumRangeLength));
                }
                else if (Random.Shared.Next(eligible) < reservoirSlots)
                {
                    reservoir[Random.Shared.Next(reservoirSlots)] = RangeFor(key, entry.Length, maximumRangeLength);
                }
            }

            if (rotationSlots == 0)
            {
                continue;
            }

            Admit(start, key, entry.Length, rotationSlots, maximumRangeLength);
            if (cursor is null || string.CompareOrdinal(key, cursor) > 0)
            {
                aheadSeen++;
                Admit(ahead, key, entry.Length, rotationSlots, maximumRangeLength);
            }
        }

        // The wrap. Nothing left above the cursor means the rotation has been
        // all the way round, so it starts again in this pass rather than
        // spending it on the newest snapshot alone.
        var rotation = ahead;
        var rotationSeen = aheadSeen;
        if (ahead.Count == 0 && cursor is not null)
        {
            rotation = start;
            rotationSeen = eligible;
        }

        var samples = new List<VerificationSample>(budget);
        if (newest is not null)
        {
            samples.Add(newest);
        }

        samples.AddRange(rotation.Values);
        foreach (var sample in reservoir)
        {
            // The reservoir draws from the whole population, so it can land on
            // a key the rotation already holds. Spending two of a sixteen-slot
            // budget on the same range would quietly narrow coverage.
            if (!rotation.ContainsKey(sample.Key))
            {
                samples.Add(sample);
            }
        }

        return new SamplePlan(
            samples,
            eligible + (newest is null ? 0 : 1),
            // A rotation that saw no more than it could take has reached the
            // end of the key space: the next pass starts over.
            rotationSeen <= rotationSlots || rotation.Count == 0 ? null : rotation.Keys[^1]);
    }

    /// <summary>A random range inside an object of the given length.</summary>
    /// <param name="key">The store key.</param>
    /// <param name="objectLength">The object's total length; must be positive.</param>
    /// <param name="maximumRangeLength">The longest range the destination will answer.</param>
    public static VerificationSample RangeFor(string key, long objectLength, uint maximumRangeLength)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(key);
        ThrowHelper.ThrowIfLessThan(objectLength, 1L);
        ThrowHelper.ThrowIfLessThan(maximumRangeLength, 1u);

        var length = (uint)Math.Min(Math.Min(objectLength, PreferredLength), maximumRangeLength);
        var offset = (ulong)Random.Shared.NextInt64(0, objectLength - length + 1);
        return new VerificationSample(key, offset, length);
    }

    /// <summary>
    /// Keeps <paramref name="window"/> holding the <paramref name="slots"/>
    /// ordinally smallest keys offered to it.
    /// </summary>
    /// <remarks>
    /// The window is sorted rather than taken in listing order because
    /// <see cref="StoreToStoreCopier"/> says outright that listing order
    /// carries no meaning. A cursor built on listing order would advance past
    /// keys it never sampled and re-sample keys it had already passed, and
    /// both failures are invisible from outside — the pass still reports a
    /// clean sweep of whatever it happened to ask for.
    /// </remarks>
    private static void Admit(
        SortedList<string, VerificationSample> window, string key, long length, int slots, uint maximumRangeLength)
    {
        if (window.ContainsKey(key))
        {
            return;
        }

        window.Add(key, RangeFor(key, length, maximumRangeLength));
        if (window.Count > slots)
        {
            window.RemoveAt(window.Count - 1);
        }
    }

    private static bool IsStagingOnly(string key) =>
        key.StartsWith("tombstones/", StringComparison.Ordinal)
        || key.StartsWith("leases/", StringComparison.Ordinal);
}
