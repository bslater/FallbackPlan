using System.Security.Cryptography;
using Bodu;
using FallbackPlan.Domain.Identifiers;
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
/// <param name="Digest">
/// How many of <paramref name="Passed"/> were proved by hashing the whole
/// sealed blob at the replica against the digest the writer signed into the
/// index (07 §2.2) — the proof a write-only set's data plane has, since its
/// records are sealed to a key this side does not hold (FR-WOR-003). Weaker
/// than a tag only in cost: it reads the whole blob rather than one record.
/// </param>
public sealed record VerificationOutcome(int Passed, IReadOnlyList<string> Failed, int Sealed = 0, int Digest = 0)
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
    /// The most the digest tier will read in one run. A whole-blob read is
    /// the price of proving a payload nobody here can open, and a run that
    /// read every sampled blob could pull gigabytes; past the budget a blob
    /// is left unproved for a later cursor, never looped over.
    /// </summary>
    public const long DigestByteBudget = 256L * 1024 * 1024;

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
    /// <param name="signedDigestOf">
    /// The signed whole-blob digest for a blob id, or null when none is on
    /// record — the digest tier's input, read from the catalogue by the
    /// caller so this project stays free of it. Absent, sealed content is
    /// neither proved nor failed.
    /// </param>
    /// <param name="digestByteBudget">The most the digest tier may read this run.</param>
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
        Repository.OpenedRepository? repository = null,
        Func<BlobId, ReadOnlyMemory<byte>?>? signedDigestOf = null,
        long digestByteBudget = DigestByteBudget)
    {
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(replica);
        ThrowHelper.ThrowIfNull(samples);

        var passed = 0;
        var failed = new List<string>();

        // The blob half, proved at the replica and against nothing else.
        // Comparison needs an independent copy, and a direct-ship set does not
        // have one: its working store reads blobs back from the destinations
        // themselves, so comparing would put a replica against itself. The
        // AEAD tag needs no second copy, because the destination never held
        // the key that computed it.
        var sealedProof = repository is null
            ? new SealedProof([], 0, 0)
            : await SealedProofAsync(
                replica, samples, repository, failed, signedDigestOf, digestByteBudget, cancellationToken)
                .ConfigureAwait(false);
        var sealedKeys = sealedProof.Proved;
        passed += sealedKeys.Count;

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

        return new VerificationOutcome(passed, failed, sealedProof.ByTag, sealedProof.ByDigest);
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
    /// <param name="signedDigestOf">The signed whole-blob digest for a blob id, or null; see <see cref="VerifyAsync"/>.</param>
    /// <param name="digestByteBudget">The most the digest tier may read this run.</param>
    /// <returns>What was proven and what was not.</returns>
    public static async Task<VerificationOutcome> ProveSealedAsync(
        IObjectStore replica,
        IReadOnlyList<string> blobKeys,
        Repository.OpenedRepository repository,
        CancellationToken cancellationToken,
        Func<BlobId, ReadOnlyMemory<byte>?>? signedDigestOf = null,
        long digestByteBudget = DigestByteBudget)
    {
        ThrowHelper.ThrowIfNull(replica);
        ThrowHelper.ThrowIfNull(blobKeys);
        ThrowHelper.ThrowIfNull(repository);

        var failed = new List<string>();
        var samples = blobKeys.Select(key => new VerificationSample(key, 0, 0)).ToList();
        var proof = await SealedProofAsync(
            replica, samples, repository, failed, signedDigestOf, digestByteBudget, cancellationToken)
            .ConfigureAwait(false);

        return new VerificationOutcome(proof.Proved.Count, failed, proof.ByTag, proof.ByDigest);
    }

    /// <summary>What the sealed half proved, and by which tier.</summary>
    private sealed record SealedProof(HashSet<string> Proved, int ByTag, int ByDigest);

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
    /// A blob that opens but whose records are sealed is proved by the
    /// <b>digest tier</b> when the caller can name the digest the writer
    /// signed for it: the whole blob short of its locator is streamed through
    /// SHA-256 at the replica and compared in fixed time. A match proves the
    /// payload bytes are the ones the writer sealed; a mismatch is rot under
    /// a tag nobody here can open, and is a failure. Without a known digest,
    /// or once the byte budget is spent, the blob is counted neither way. In a
    /// write-only repository (ADR-0042) the service holds the structure key
    /// and not the content key, so its data records are unreadable by
    /// construction rather than by damage; saying "proved" of the payloads
    /// without the digest would be a claim nobody checked. Such keys fall
    /// through to the comparison half, which reports honestly when it has no
    /// independent side.
    /// </para>
    /// </remarks>
    private static async Task<SealedProof> SealedProofAsync(
        IObjectStore replica,
        IReadOnlyList<VerificationSample> samples,
        Repository.OpenedRepository repository,
        List<string> failed,
        Func<BlobId, ReadOnlyMemory<byte>?>? signedDigestOf,
        long digestByteBudget,
        CancellationToken cancellationToken)
    {
        var blobKeys = samples
            .Select(sample => sample.Key)
            .Where(key => key.StartsWith("blobs/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(ObjectKey.Parse)
            .ToList();

        var proved = new HashSet<string>(StringComparer.Ordinal);
        var byTag = 0;
        var byDigest = 0;
        if (blobKeys.Count == 0)
        {
            return new SealedProof(proved, 0, 0);
        }

        using var reader = new Repository.RepositoryReader(
            repository.RepositoryId, repository.Keys, replica);
        await reader.LoadBlobsAsync(blobKeys, cancellationToken).ConfigureAwait(false);

        foreach (var skipped in reader.SkippedBlobs)
        {
            failed.Add(skipped.Key.Value);
        }

        var budget = digestByteBudget;
        foreach (var (storeKey, blobId, records) in reader.Blobs)
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

            switch (read.Outcome)
            {
                case Repository.Packing.RecordReadOutcome.Ok:
                    proved.Add(storeKey.Value);
                    byTag++;
                    break;

                case Repository.Packing.RecordReadOutcome.AuthenticationFailed:
                    failed.Add(storeKey.Value);
                    break;

                case Repository.Packing.RecordReadOutcome.ContentSealed
                    when signedDigestOf?.Invoke(blobId) is { Length: SHA256.HashSizeInBytes } digest:
                    switch (await DigestProofAsync(replica, storeKey, digest, budget, cancellationToken).ConfigureAwait(false))
                    {
                        case (DigestVerdict.Proved, var spent):
                            proved.Add(storeKey.Value);
                            byDigest++;
                            budget -= spent;
                            break;

                        case (DigestVerdict.Failed, var spent):
                            failed.Add(storeKey.Value);
                            budget -= spent;
                            break;

                        // Over budget: left for a later run's cursor, and
                        // never blamed.
                    }

                    break;

                // Any other outcome — a sealed data plane with no digest to
                // check it against — leaves the key to the comparison half.
            }
        }

        return new SealedProof(proved, byTag, byDigest);
    }

    private enum DigestVerdict
    {
        Proved,
        Failed,
        OverBudget,
    }

    /// <summary>
    /// Hashes the blob at the replica, short of its sixteen-byte locator —
    /// the digest's preimage (07 §2.2) — and compares in fixed time. A blob
    /// the replica cannot produce whole is a failure, exactly as a short
    /// range is for the comparison half.
    /// </summary>
    /// <returns>The verdict and the bytes it cost.</returns>
    private static async Task<(DigestVerdict Verdict, long Spent)> DigestProofAsync(
        IObjectStore replica,
        ObjectKey storeKey,
        ReadOnlyMemory<byte> expected,
        long budget,
        CancellationToken cancellationToken)
    {
        const int LocatorLength = 16;

        var metadata = await replica.GetMetadataAsync(storeKey, cancellationToken).ConfigureAwait(false);
        if (metadata.Metadata is not { } found || found.Length <= LocatorLength)
        {
            return (DigestVerdict.Failed, 0);
        }

        var length = found.Length - LocatorLength;
        if (length > budget)
        {
            return (DigestVerdict.OverBudget, 0);
        }

        using var read = await replica.OpenReadAsync(storeKey, new ObjectRange(0, length), cancellationToken)
            .ConfigureAwait(false);
        if (read.Outcome != OpenReadOutcome.Found || read.Content is null)
        {
            return (DigestVerdict.Failed, 0);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var count = await read.Content.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return (DigestVerdict.Failed, length - remaining);
            }

            hash.AppendData(buffer, 0, count);
            remaining -= count;
        }

        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        hash.GetHashAndReset(actual);
        return (CryptographicOperations.FixedTimeEquals(actual, expected.Span) ? DigestVerdict.Proved : DigestVerdict.Failed, length);
    }
}
