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
    int Examined, IReadOnlyList<string> Findings, string? NextCursor, bool CompletedCircuit)
{
    /// <summary>
    /// The store keys behind <see cref="Findings"/>, one per damaged blob, for
    /// whatever acts on them — a repair must not have to parse a sentence back.
    /// </summary>
    public IReadOnlyList<string> DamagedKeys { get; init; } = [];

    /// <summary>
    /// The blob this segment stopped at because it could not be read; null
    /// when the segment was not stopped short. The segment ends there, and
    /// <see cref="NextCursor"/> is the last blob read before it, so the next
    /// attempt begins with this one.
    /// </summary>
    /// <remarks>
    /// Not a finding. A disk gone from under the segment fails every read the
    /// same way one bad sector fails one, and nothing within one attempt tells
    /// the two apart; the caller decides what a stall repeated means.
    /// </remarks>
    public string? StalledOn { get; init; }

    /// <summary>Why the read of <see cref="StalledOn"/> failed; null when the segment did not stall.</summary>
    public string? Stall { get; init; }
}

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
    /// Bytes read per segment when the caller states no preference: sixty-four
    /// blobs at the default 64 MiB target, so on an ordinary archive the count
    /// is what ends a segment and this bites only on larger blobs.
    /// </summary>
    public const long DefaultByteBudget = 4L * 1024 * 1024 * 1024;

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
    public static ValueTask<ReplicaSweepResult> RunAsync(
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        IObjectStore replica,
        IObjectStore? source,
        string? cursor,
        int budget,
        CancellationToken cancellationToken) =>
        RunAsync(repositoryId, keys, replica, source, cursor, budget, DefaultByteBudget, cancellationToken);

    /// <summary>
    /// Examines the next <paramref name="budget"/> blobs after
    /// <paramref name="cursor"/>, stopping early once they have read
    /// <paramref name="byteBudget"/> bytes.
    /// </summary>
    /// <remarks>
    /// The byte bound is what keeps a segment short when reading is slow: the
    /// segment holds the process's one transfer worker for as long as it
    /// reads, and sixty-four blobs through a slow limit is hours. A blob is
    /// read whole or not at all, so the segment ends at the blob that spends
    /// the budget — and never before the first, or a blob larger than the
    /// budget would park the circuit on itself for ever.
    /// </remarks>
    /// <param name="repositoryId">The replica's repository identity — the same as the source's.</param>
    /// <param name="keys">The source's key set; a replica is a byte copy, so the same keys open it.</param>
    /// <param name="replica">The destination's store.</param>
    /// <param name="source">The staging archive's store, for the length comparison; null skips that half.</param>
    /// <param name="cursor">Where to resume, or null to start at the beginning.</param>
    /// <param name="budget">The most blobs to examine; must be positive.</param>
    /// <param name="byteBudget">The most bytes to read before stopping at a blob boundary; must be positive.</param>
    /// <param name="cancellationToken">Cancels the segment; the cursor is not advanced.</param>
    public static async ValueTask<ReplicaSweepResult> RunAsync(
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        IObjectStore replica,
        IObjectStore? source,
        string? cursor,
        int budget,
        long byteBudget,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(keys);
        ThrowHelper.ThrowIfNull(replica);
        ThrowHelper.ThrowIfLessThan(budget, 1);
        ThrowHelper.ThrowIfLessThan(byteBudget, 1L);

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
        var damaged = new List<string>();
        using var verifier = new VerifyEngine(repositoryId, keys, replica);
        string? lastExamined = null;
        var examined = 0;
        var read = 0L;

        foreach (var (key, length) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (examined > 0 && read + length > byteBudget)
            {
                break;
            }

            examined++;
            read += length;

            var storeKey = ObjectKey.Parse(key);
            BlobVerifyResult result;
            try
            {
                result = await verifier
                    .VerifyBlobAsync(storeKey, length, VerifyLevel.FooterAndDigest, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Stopped here, keeping what was read before: the next attempt
                // begins with this blob instead of re-reading the run up to it.
                return new ReplicaSweepResult(examined - 1, findings, lastExamined ?? cursor, CompletedCircuit: false)
                {
                    DamagedKeys = damaged,
                    StalledOn = key,
                    Stall = exception.Message,
                };
            }

            if (!result.Ok)
            {
                findings.Add($"blob {key}: {result.Detail}");
                damaged.Add(key);
            }
            else if (source is not null
                && await LengthAtAsync(source, storeKey, cancellationToken).ConfigureAwait(false) is { } sourceLength
                && sourceLength != length)
            {
                // Internally consistent and still not what we hold. A key the
                // source no longer lists is skipped above, not blamed.
                findings.Add(
                    $"blob {key}: the replica holds {length} byte(s) where the source holds {sourceLength}");
                damaged.Add(key);
            }

            lastExamined = key;
        }

        // Every key after the cursor examined means the listing ran out: this
        // segment reached the end, so the circuit is closed and the next one
        // starts over. Fewer — the count or the bytes ended the segment — and
        // the next resumes after the last blob read.
        var completed = examined == seen;
        return new ReplicaSweepResult(examined, findings, completed ? null : lastExamined, completed)
        {
            DamagedKeys = damaged,
        };
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
