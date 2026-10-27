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
    /// The keys whose copy at the source proved unsound where the replica's
    /// is the blob its key names: the source's findings, never the
    /// replica's, for whatever heals the source (ADR-0095).
    /// </summary>
    public IReadOnlyList<string> SourceDamagedKeys { get; init; } = [];

    /// <summary>What was wrong with each of <see cref="SourceDamagedKeys"/>, one line each, in the same order.</summary>
    public IReadOnlyList<string> SourceFindings { get; init; } = [];

    /// <summary>
    /// Every key this segment read, damaged or not — what a caller holding an
    /// earlier finding needs to know which of its keys have now been read
    /// again, and a key the segment did not reach must not be taken for one
    /// that is sound.
    /// </summary>
    public IReadOnlyList<string> ExaminedKeys { get; init; } = [];

    /// <summary>
    /// The blobs this segment passed over because the store holds them only
    /// in an archival tier, which serves nothing until they are rehydrated
    /// (ADR-0104). Not read, so neither sound nor damaged, and not a stall:
    /// the next attempt would meet the same refusal, and stopping there would
    /// leave everything after them unread for good.
    /// </summary>
    public IReadOnlyList<string> ArchivedKeys { get; init; } = [];

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
/// Two checks of each blob, because one is demonstrably not enough.
/// <see cref="VerifyLevel.FooterAndDigest"/> proves the blob is <b>internally
/// consistent</b>: its bytes still hash to what its own footer says. That
/// catches bit-rot, which is the common case. It does <b>not</b> catch a
/// well-formed, correctly sealed blob stored under a <i>different</i> blob's
/// key: the bytes authenticate against their own footer, so such an object
/// passes completely. Nothing internal to it is wrong; it is simply not the
/// object its key names, and its envelope says so. Each blob is therefore
/// bound to its key as well, which needs nothing beside the store being read
/// — the staging archive is swept with no source at all (ADR-0095), and
/// <c>ReplicaSweepTests</c> pins exactly that: the digest passes the swap and
/// the key does not.
/// </para>
/// <para>
/// A blob that passes both is the blob its key names, unaltered, so a source
/// listing another length under that key holds something else. Its copy is
/// proven where it sits, and one that does not prove sound is the source's
/// finding, never the replica's: repairing the replica would replace a sound
/// object and leave the damaged one unsaid. Nothing else about a length is
/// anyone's finding — a source copy that will not be read has not been shown
/// to be altered, and one that proves sound is a second blob written under
/// the same identity, which replacing either would lose. A key the source no
/// longer lists is skipped too — that is a trimmed object whose only
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
/// The store contract lists in ordinal key order and resumes after any key
/// (ADR-0012 Amendment 5), so a segment asks the store for the keys after its
/// cursor and reads only as far as it needs: its budget and one key past, to
/// learn whether the circuit goes on. Listing every blob to find its place, as
/// a segment once did, made a circuit over a store cost the square of the
/// archive in requests.
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
    /// <param name="source">The staging archive's store, for the length comparison; null when there is none, or it is the store being swept.</param>
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
    /// <param name="source">The staging archive's store, for the length comparison; null when there is none, or it is the store being swept.</param>
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

        // The candidates: the `budget` blob keys ordinally after the cursor,
        // which the listing gives first. One more key, if the store holds
        // one, says the circuit goes on past this segment; the listing stops
        // there rather than read the rest.
        var candidates = new List<(string Key, long Length)>(budget);
        var more = false;
        await foreach (var entry in replica
            .ListAsync(
                ObjectPrefix.Parse("blobs/"),
                new ListOptions { ResumeAfter = cursor, PageSizeHint = budget + 1 },
                cancellationToken)
            .ConfigureAwait(false))
        {
            if (candidates.Count == budget)
            {
                more = true;
                break;
            }

            candidates.Add((entry.Key.Value, entry.Length));
        }

        if (candidates.Count == 0)
        {
            // Nothing left after the cursor: the circuit is closed. An empty
            // replica closes it too — there is nothing to disprove.
            return new ReplicaSweepResult(0, [], null, CompletedCircuit: true);
        }

        var findings = new List<string>();
        var damaged = new List<string>();
        var sourceDamaged = new List<string>();
        var sourceFindings = new List<string>();
        var read = new List<string>();
        var archived = new List<string>();
        using var verifier = new VerifyEngine(repositoryId, keys, replica);
        string? lastExamined = null;
        var examined = 0;
        var bytesRead = 0L;

        foreach (var (key, length) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (examined > 0 && bytesRead + length > byteBudget)
            {
                break;
            }

            examined++;
            bytesRead += length;

            var storeKey = ObjectKey.Parse(key);
            BlobVerifyResult result;
            try
            {
                result = await verifier
                    .VerifyBlobAsync(storeKey, length, VerifyLevel.FooterAndDigest, bindKey: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (StoreObjectArchivedException)
            {
                // Passed over, not stopped at: the next attempt would meet the
                // same refusal until a person rehydrates it.
                examined--;
                bytesRead -= length;
                archived.Add(key);
                lastExamined = key;
                continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Stopped here, keeping what was read before: the next attempt
                // begins with this blob instead of re-reading the run up to it.
                return new ReplicaSweepResult(examined - 1, findings, lastExamined ?? cursor, CompletedCircuit: false)
                {
                    DamagedKeys = damaged,
                    SourceDamagedKeys = sourceDamaged,
                    SourceFindings = sourceFindings,
                    ExaminedKeys = read,
                    ArchivedKeys = archived,
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
                && sourceLength != length
                && await UnsoundAtAsync(repositoryId, keys, source, storeKey, cancellationToken).ConfigureAwait(false)
                    is { } defect)
            {
                sourceFindings.Add($"blob {key}: {defect}");
                sourceDamaged.Add(key);
            }

            // Only a blob whose read completed is one this segment read.
            read.Add(key);
            lastExamined = key;
        }

        // Every key after the cursor examined means the listing ran out: this
        // segment reached the end, so the circuit is closed and the next one
        // starts over. Fewer — the count or the bytes ended the segment — and
        // the next resumes after the last blob read.
        var completed = examined + archived.Count == candidates.Count && !more;
        return new ReplicaSweepResult(examined, findings, completed ? null : lastExamined, completed)
        {
            DamagedKeys = damaged,
            SourceDamagedKeys = sourceDamaged,
            SourceFindings = sourceFindings,
            ExaminedKeys = read,
            ArchivedKeys = archived,
        };
    }

    /// <summary>
    /// Why what <paramref name="store"/> holds under <paramref name="key"/> is
    /// not the blob, unaltered; null when it is, when nothing is held there,
    /// and when it could not be read.
    /// </summary>
    private static async ValueTask<string?> UnsoundAtAsync(
        RepositoryId repositoryId, RepositoryKeySet keys, IObjectStore store, ObjectKey key,
        CancellationToken cancellationToken)
    {
        try
        {
            var proof = await ReplicaRepair.ProveAsync(repositoryId, keys, store, key, cancellationToken)
                .ConfigureAwait(false);
            return proof is { Held: true, Sound: false } ? proof.Detail : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not read, so not shown to be altered.
            return null;
        }
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
