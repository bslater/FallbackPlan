using Bodu;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Replication;

/// <summary>One key and range to challenge (specification peer-protocol 04).</summary>
/// <param name="Key">The store key.</param>
/// <param name="Offset">The range's byte offset.</param>
/// <param name="Length">The range's length.</param>
public sealed record VerificationSample(string Key, ulong Offset, uint Length);

/// <summary>What one verification run established.</summary>
/// <param name="Passed">Ranges the destination proved.</param>
/// <param name="Failed">Keys the destination could not prove, or proved wrongly.</param>
/// <remarks>
/// <see cref="Passed"/> is counted rather than inferred from the absence of
/// failures, because a sample whose ground truth this side could not read is
/// skipped — and a run where every sample skipped would otherwise be spelled
/// exactly like a run where every sample passed. Proving nothing and proving
/// everything must not look alike (FR-VER-003).
/// </remarks>
public sealed record VerificationOutcome(int Passed, IReadOnlyList<string> Failed)
{
    /// <summary>Whether this run established anything at all.</summary>
    public bool ProvedSomething => Passed > 0;
}

/// <summary>
/// Reads exactly one sampled range from a store — the single answer to "these
/// bytes, or nothing" that both destination kinds and both ends of the peer
/// challenge rest on.
/// </summary>
public static class RangeReader
{
    /// <summary>
    /// Reads exactly the sampled range, or null when the store cannot produce
    /// it — absent key, unsatisfiable range, a copy shorter than the range
    /// claims, or a read that faulted. What that null <b>means</b> is the
    /// caller's to decide: for a replica it is the finding; for the source it
    /// means there is no ground truth and the sample proves nothing either way.
    /// </summary>
    /// <param name="store">The store to read.</param>
    /// <param name="key">The store key.</param>
    /// <param name="offset">The range's byte offset.</param>
    /// <param name="length">The range's length.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Exactly <paramref name="length"/> bytes, or null.</returns>
    public static async Task<byte[]?> ReadAsync(
        IObjectStore store, string key, ulong offset, uint length, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNullOrWhiteSpace(key);

        if (!ObjectKey.TryParse(key, out var parsed))
        {
            return null;
        }

        try
        {
            using var read = await store.OpenReadAsync(
                parsed, new ObjectRange((long)offset, checked((long)length)), cancellationToken)
                .ConfigureAwait(false);
            if (read.Outcome != OpenReadOutcome.Found)
            {
                return null;
            }

            var bytes = new byte[length];
            var filled = 0;
            int got;
            while (filled < bytes.Length
                && (got = await read.Content!.ReadAsync(bytes.AsMemory(filled), cancellationToken)
                    .ConfigureAwait(false)) > 0)
            {
                filled += got;
            }

            // A short copy cannot prove the range — which is exactly what the
            // challenge exists to catch.
            return filled == bytes.Length ? bytes : null;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Reads exactly the sample's range, or null.</summary>
    /// <param name="store">The store to read.</param>
    /// <param name="sample">The key and range.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static Task<byte[]?> ReadAsync(
        IObjectStore store, VerificationSample sample, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(sample);
        return ReadAsync(store, sample.Key, sample.Offset, sample.Length, cancellationToken);
    }
}

/// <summary>
/// The local twin of the peer challenge (specification peer-protocol 04):
/// where a peer proves possession over the wire, a destination this side can
/// read is verified by reading the sampled ranges straight back off it and
/// comparing them to the source's own bytes. Both kinds earn the verified word
/// from bytes on the destination's disk, never from a copy having reported
/// success (FR-VER-001).
/// </summary>
public static class ReplicaVerifier
{
    /// <summary>
    /// Compares each sampled range at the replica against the source.
    /// </summary>
    /// <param name="source">The staging archive's store — the ground truth.</param>
    /// <param name="replica">The destination's store.</param>
    /// <param name="samples">The keys and ranges to check.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>
    /// What was proven and what was not. A range the <b>source</b> cannot read
    /// is skipped — it counts neither way — so a run that skipped everything
    /// answers <see cref="VerificationOutcome.ProvedSomething"/> false rather
    /// than passing vacuously. Absent, short, or differing bytes at the replica
    /// are findings.
    /// </returns>
    public static async Task<VerificationOutcome> VerifyAsync(
        IObjectStore source, IObjectStore replica,
        IReadOnlyList<VerificationSample> samples, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(replica);
        ThrowHelper.ThrowIfNull(samples);

        var passed = 0;
        var failed = new List<string>();
        foreach (var sample in samples)
        {
            var expected = await RangeReader.ReadAsync(source, sample, cancellationToken).ConfigureAwait(false);
            if (expected is null)
            {
                continue;
            }

            var actual = await RangeReader.ReadAsync(replica, sample, cancellationToken).ConfigureAwait(false);
            if (actual is not null && expected.AsSpan().SequenceEqual(actual))
            {
                passed++;
            }
            else
            {
                failed.Add(sample.Key);
            }
        }

        return new VerificationOutcome(passed, failed);
    }
}
