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
/// <param name="Sealed">
/// How many of <paramref name="Passed"/> were proved by opening a record's
/// AEAD tag rather than by comparing bytes against another copy. The strong
/// half: a tag was computed by the writer under a key the destination has
/// never held, so it cannot be forged and rot cannot survive it.
/// </param>
public sealed record VerificationOutcome(int Passed, IReadOnlyList<string> Failed, int Sealed = 0)
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
    /// <param name="repository">
    /// The opened repository, when the caller has one. Present, sampled
    /// blobs are proved at the replica by their own AEAD tags, which needs
    /// no independent copy; absent, every sample falls to the comparison,
    /// which proves nothing where the source is the replica.
    /// </param>
    /// <returns>
    /// What was proven and what was not. A range the <b>source</b> cannot read
    /// is skipped — it counts neither way — so a run that skipped everything
    /// answers <see cref="VerificationOutcome.ProvedSomething"/> false rather
    /// than passing vacuously. Absent, short, or differing bytes at the replica
    /// are findings.
    /// </returns>
    public static async Task<VerificationOutcome> VerifyAsync(
        IObjectStore source, IObjectStore replica,
        IReadOnlyList<VerificationSample> samples, CancellationToken cancellationToken,
        Repository.OpenedRepository? repository = null)
    {
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(replica);
        ThrowHelper.ThrowIfNull(samples);

        var passed = 0;
        var sealedProofs = 0;
        var failed = new List<string>();

        // The blob half, proved at the replica and against nothing else.
        // Comparison needs an independent copy, and a direct-ship set does not
        // have one: its working store reads blobs back from the destinations
        // themselves, so comparing would put a replica against itself. The
        // AEAD tag needs no second copy, because the destination never held
        // the key that computed it.
        var sealedKeys = repository is null
            ? []
            : await SealedProofAsync(replica, samples, repository, failed, cancellationToken).ConfigureAwait(false);
        passed += sealedKeys.Count;
        sealedProofs = sealedKeys.Count;

        foreach (var sample in samples)
        {
            if (sealedKeys.Contains(sample.Key) || failed.Contains(sample.Key))
            {
                continue;
            }

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

        return new VerificationOutcome(passed, failed, sealedProofs);
    }

    /// <summary>
    /// Proves blobs at a replica with no second copy to compare against: open
    /// each one where it sits and authenticate a record inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comparison half of <see cref="VerifyAsync"/> needs an independent
    /// copy and this needs none, which is the whole reason it exists: the
    /// AEAD tag was computed by the writer under a key the destination has
    /// never held, so the destination can neither forge it nor survive rot
    /// beneath it. It is what a set whose only destination is a peer has
    /// instead of a challenge ([ADR-0058](../../docs/adr/0058-peer-write-adapter.md) §8).
    /// </para>
    /// <para>
    /// Reads are targeted — the named blobs' footers and one record each — so
    /// this costs a handful of ranged reads per blob rather than a transfer,
    /// which is what makes it affordable over a peer's retrieval session.
    /// </para>
    /// <para>
    /// There is no comparison half behind this one, so the outcomes that fall
    /// through it are reported by nothing. That is deliberate and worth
    /// stating: a sealed data plane this service cannot open is not damage
    /// (ADR-0042 §5), and the two outcomes that mean the *plaintext* is wrong
    /// — a framing violation and a content-identifier mismatch — describe
    /// records whose tag verified, so they are damage the writer committed
    /// rather than damage the replica did. Blaming a destination for them
    /// would accuse the wrong party; they belong to the dedup trust gate at
    /// write time and to the local sweep.
    /// </para>
    /// </remarks>
    /// <param name="replica">The destination's store, however it is reached.</param>
    /// <param name="blobKeys">The blob store keys to prove.</param>
    /// <param name="repository">The opened repository, whose keys authenticate the records.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What was proven and what was not.</returns>
    public static async Task<VerificationOutcome> ProveSealedAsync(
        IObjectStore replica,
        IReadOnlyList<string> blobKeys,
        Repository.OpenedRepository repository,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(replica);
        ThrowHelper.ThrowIfNull(blobKeys);
        ThrowHelper.ThrowIfNull(repository);

        var failed = new List<string>();
        var samples = blobKeys.Select(key => new VerificationSample(key, 0, 0)).ToList();
        var proved = await SealedProofAsync(replica, samples, repository, failed, cancellationToken)
            .ConfigureAwait(false);

        return new VerificationOutcome(proved.Count, failed, proved.Count);
    }

    /// <summary>
    /// Proves sampled blobs at the replica by opening them: the footer
    /// authenticates the container, and a record read from it authenticates
    /// its own bytes (specification 04 §6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A blob the reader cannot open at all is a failure — its footer did not
    /// authenticate where it sits, which is exactly the damage a challenge
    /// exists to find.
    /// </para>
    /// <para>
    /// A blob that opens but whose records will not decrypt is <b>not</b>
    /// counted either way here. In a write-only repository (ADR-0042) the
    /// service holds the structure key and not the content key, so its data
    /// records are unreadable by construction rather than by damage; the
    /// container is still proved, and saying "proved" of the payloads would be
    /// a claim nobody checked. Those keys fall through to the comparison half,
    /// which reports honestly when it has no independent side.
    /// </para>
    /// </remarks>
    private static async Task<HashSet<string>> SealedProofAsync(
        IObjectStore replica,
        IReadOnlyList<VerificationSample> samples,
        Repository.OpenedRepository repository,
        List<string> failed,
        CancellationToken cancellationToken)
    {
        var blobKeys = samples
            .Select(sample => sample.Key)
            .Where(key => key.StartsWith("blobs/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(ObjectKey.Parse)
            .ToList();

        var proved = new HashSet<string>(StringComparer.Ordinal);
        if (blobKeys.Count == 0)
        {
            return proved;
        }

        using var reader = new Repository.RepositoryReader(
            repository.RepositoryId, repository.Keys, replica);
        await reader.LoadBlobsAsync(blobKeys, cancellationToken).ConfigureAwait(false);

        foreach (var skipped in reader.SkippedBlobs)
        {
            failed.Add(skipped.Key.Value);
        }

        foreach (var (storeKey, _, records) in reader.Blobs)
        {
            if (records.Count == 0)
            {
                continue;
            }

            // One record per blob, chosen at random rather than always the
            // first: rot is rarely at the front, and a fixed choice is a
            // choice a damaged replica could survive for ever.
            var record = records[System.Random.Shared.Next(records.Count)];
            var read = await reader.ReadSegmentAsync(record.ObjectId, cancellationToken).ConfigureAwait(false);

            if (read.Outcome == Repository.Packing.RecordReadOutcome.Ok)
            {
                proved.Add(storeKey.Value);
            }
            else if (read.Outcome == Repository.Packing.RecordReadOutcome.AuthenticationFailed)
            {
                failed.Add(storeKey.Value);
            }

            // Any other outcome — a sealed data plane this service cannot
            // open — leaves the key to the comparison half.
        }

        return proved;
    }
}
