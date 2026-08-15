using Bodu;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>What one segment of a replica sweep established.</summary>
/// <param name="Examined">Blobs this segment read.</param>
/// <param name="Findings">What was wrong, one line each; empty means nothing was.</param>
/// <param name="NextCursor">
/// Where the next segment resumes — the last key examined, or null when the
/// circuit closed and the next segment starts from the beginning again.
/// </param>
/// <param name="CompletedCircuit">
/// Whether this segment reached the end of the key space, so every blob has now
/// been examined at least once since the circuit began. This is the only fact
/// that supports "coverage is complete as of <i>T</i>" — a segment count cannot.
/// </param>
public sealed record ReplicaSweepResult(
    int Examined, IReadOnlyList<string> Findings, string? NextCursor, bool CompletedCircuit);

/// <summary>
/// Re-reads a replica's stored blobs and confirms they are still what was
/// sealed — the periodic half of destination verification, complementing the
/// range challenge that runs at sync time.
/// </summary>
/// <remarks>
/// <para>
/// The range challenge proves a destination holds the bytes it was sent, at the
/// moment it is asked. It says nothing between syncs, and it samples: on a large
/// archive the same handful of objects can be challenged forever while rot
/// spreads through everything else. This sweep is the other half — every blob,
/// eventually, checked against the digest sealed into its own footer.
/// </para>
/// <para>
/// Two checks, because one is demonstrably not enough.
/// <see cref="VerifyLevel.FooterAndDigest"/> proves the blob is <b>internally
/// consistent</b>: its bytes still hash to what its own footer says. That
/// catches bit-rot, which is the common case. It does <b>not</b> catch a
/// well-formed, correctly sealed blob stored under a <i>different</i> blob's
/// key — the reader authenticates bytes against their own footer and does not
/// bind the store key to the envelope's blob id, so such an object passes
/// completely. Nothing internal to it is wrong; it is simply not the object we
/// put there. Comparing each blob's length against the source's is what
/// notices, and <c>ReplicaSweepTests</c> pins exactly that: the digest check
/// passes the swap and the length check fails it. A key the source no longer
/// lists is skipped rather than blamed — that is a trimmed object whose only
/// remaining home may be this replica (ADR-0034 §6).
/// </para>
/// <para>
/// Segmented by design: the caller gives a budget and gets back a cursor. The
/// sweep runs on the transfer lane, which has one worker for the whole process,
/// so a pass that walked an entire archive would stall fan-out to every
/// destination of every set. The cursor is what makes a bounded segment add up
/// to full coverage over time.
/// </para>
/// <para>
/// The cursor is the <b>key</b>, and resumption takes the next key ordinally
/// greater — never an index. A blob trimmed since the last segment therefore
/// costs nothing: the cursor is only ever compared against, never looked up.
/// Candidates are sorted here rather than taken in listing order, because the
/// store contract does not promise an order (see
/// <c>StoreToStoreCopier</c>'s note that listing order "carries no meaning") and
/// a cursor resting on an unpromised order would silently skip and silently
/// repeat.
/// </para>
/// </remarks>
public static class ReplicaSweep
{
    /// <summary>Blobs examined per segment, when the caller states no preference.</summary>
    public const int DefaultBudget = 64;

    /// <summary>
    /// Examines the next <paramref name="budget"/> blobs after
    /// <paramref name="cursor"/>.
    /// </summary>
    /// <param name="repositoryId">The replica's repository identity — the same as the source's.</param>
    /// <param name="keys">The source's key set; a replica is a byte copy, so the same keys open it.</param>
    /// <param name="replica">The destination's store.</param>
    /// <param name="source">The staging archive's store, for the length comparison; null skips that half.</param>
    /// <param name="cursor">Where to resume, or null to start at the beginning.</param>
    /// <param name="budget">The most blobs to examine; must be positive.</param>
    /// <param name="cancellationToken">Cancels the segment; the cursor is not advanced.</param>
    public static async ValueTask<ReplicaSweepResult> RunAsync(
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        IObjectStore replica,
        IObjectStore? source,
        string? cursor,
        int budget,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(keys);
        ThrowHelper.ThrowIfNull(replica);
        ThrowHelper.ThrowIfLessThan(budget, 1);

        // The candidates: the `budget` smallest blob keys ordinally after the
        // cursor. Held in a bounded sorted set so a large archive costs the
        // budget in memory, not the archive.
        var candidates = new SortedList<string, long>(StringComparer.Ordinal);
        var seen = 0;
        await foreach (var entry in replica
            .ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            var key = entry.Key.Value;
            if (cursor is not null && string.CompareOrdinal(key, cursor) <= 0)
            {
                continue;
            }

            seen++;
            if (candidates.Count < budget)
            {
                candidates[key] = entry.Length;
            }
            else if (string.CompareOrdinal(key, candidates.Keys[^1]) < 0)
            {
                candidates.RemoveAt(candidates.Count - 1);
                candidates[key] = entry.Length;
            }
        }

        if (candidates.Count == 0)
        {
            // Nothing left after the cursor: the circuit is closed. An empty
            // replica closes it too — there is nothing to disprove.
            return new ReplicaSweepResult(0, [], null, CompletedCircuit: true);
        }

        var findings = new List<string>();
        using var verifier = new VerifyEngine(repositoryId, keys, replica);
        string? lastExamined = null;

        foreach (var (key, length) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var storeKey = ObjectKey.Parse(key);
            var result = await verifier
                .VerifyBlobAsync(storeKey, length, VerifyLevel.FooterAndDigest, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Ok)
            {
                findings.Add($"blob {key}: {result.Detail}");
            }
            else if (source is not null
                && await LengthAtAsync(source, storeKey, cancellationToken).ConfigureAwait(false) is { } sourceLength
                && sourceLength != length)
            {
                // Internally consistent and still not what we hold. A key the
                // source no longer lists is skipped above, not blamed.
                findings.Add(
                    $"blob {key}: the replica holds {length} byte(s) where the source holds {sourceLength}");
            }

            lastExamined = key;
        }

        // Fewer candidates than the budget means the listing ran out: this
        // segment reached the end, so the circuit is closed and the next one
        // starts over.
        var completed = seen <= budget;
        return new ReplicaSweepResult(
            candidates.Count, findings, completed ? null : lastExamined, completed);
    }

    private static async ValueTask<long?> LengthAtAsync(
        IObjectStore store, ObjectKey key, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await store.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
            return metadata.Metadata?.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The source's inability is not the replica's fault.
            return null;
        }
    }
}
