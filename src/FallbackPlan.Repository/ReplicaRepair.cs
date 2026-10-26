using Bodu;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>What a store holds under one key, proved where it sits.</summary>
/// <param name="Held">Whether the store holds the key at all.</param>
/// <param name="Sound">Whether what it holds is the blob the key names, unaltered since it was sealed.</param>
/// <param name="Length">The held object's length; zero when nothing is held.</param>
/// <param name="Detail">Why what is held is not sound, in a person's words; empty when it is.</param>
public sealed record CopyProof(bool Held, bool Sound, long Length, string Detail);

/// <summary>What became of one damaged replica object.</summary>
/// <param name="Key">The object's store key.</param>
/// <param name="RepairedFrom">The source whose copy replaced it; null when none could.</param>
/// <param name="Detail">When no source could, why not, source by source; empty when one did.</param>
public sealed record ReplicaRepairOutcome(string Key, string? RepairedFrom, string Detail)
{
    /// <summary>Whether a copy proven sound replaced the object and was proven again where it landed.</summary>
    public bool Repaired => RepairedFrom is not null;

    /// <summary>
    /// Whether the target no longer holds the object at all, so there was
    /// no damage left there to repair — gone is not repaired, and what put
    /// it back, if anything does, is the next copy or push.
    /// </summary>
    public bool Gone { get; init; }
}

/// <summary>What a repair target did with a copy proven sound.</summary>
/// <param name="Installed">Whether the copy now stands in place of the damaged object.</param>
/// <param name="Gone">Whether the target held nothing under the key, so took nothing.</param>
/// <param name="Detail">Why it did not take the copy, in a person's words; empty when it did.</param>
public readonly record struct RepairInstall(bool Installed, bool Gone, string Detail)
{
    /// <summary>The copy stands in place of the damaged object.</summary>
    public static RepairInstall Done => new(Installed: true, Gone: false, string.Empty);

    /// <summary>The target holds nothing under the key, and a repair creates nothing.</summary>
    public static RepairInstall NoLongerHeld => new(Installed: false, Gone: true, "no longer held there");

    /// <summary>The target would not take the copy, for a reason about its own copy.</summary>
    /// <param name="detail">Why not, naming the target.</param>
    public static RepairInstall Refused(string detail) => new(Installed: false, Gone: false, detail);
}

/// <summary>
/// Where a repair puts a copy proven sound, and where it then proves it
/// again. A store this side writes is its own target; a peer's replica is
/// written through a repair session and read back through the retrieval
/// session (ADR-0100), so the two halves are the target's to say.
/// </summary>
public interface IRepairTarget
{
    /// <summary>
    /// Puts a proven copy in place of the damaged object, or says why the
    /// target will not. A refusal is about the target's own copy, so no
    /// other source is offered after one.
    /// </summary>
    /// <param name="key">The damaged object's store key.</param>
    /// <param name="candidate">A store holding the copy proven sound under <paramref name="key"/>.</param>
    /// <param name="length">The proven copy's length.</param>
    /// <param name="cancellationToken">Cancels the install.</param>
    ValueTask<RepairInstall> InstallAsync(
        ObjectKey key, IObjectStore candidate, long length, CancellationToken cancellationToken);

    /// <summary>The store a copy the target took is read back from to be proven where it landed.</summary>
    /// <param name="cancellationToken">Cancels opening it.</param>
    ValueTask<IObjectStore> LandedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Replaces a replica object the deep sweep found damaged with a copy proven
/// sound first (FR-VER-007, ADR-0035 Amendment 1).
/// </summary>
/// <remarks>
/// <para>
/// A copy is sound when two things hold where it sits: its bytes still hash to
/// the digest sealed into its own footer, and its envelope names the blob the
/// key derives from — the two checks the sweep makes of every blob. A
/// well-formed, correctly sealed blob stored under another blob's key passes
/// every check of its own bytes, and copying it over the damaged one would
/// replace one lost record with a whole wrong object.
/// </para>
/// <para>
/// Nothing at the replica is touched until a copy has been proven. A damaged
/// blob still restores every record the damage did not land in, because each
/// record authenticates on its own, so deleting it on the way to a
/// replacement that then turned out not to exist would lose them all.
/// </para>
/// <para>
/// A store's objects are immutable and a put never overwrites (specification
/// 01 §4), so the replacement is a delete and a put, and the copy is proven
/// again where it landed before the repair is claimed. Given a scratch store,
/// the source's copy is staged there first and the proof and the put both
/// read the staged bytes: the source is read once, which matters when it is
/// somebody else's link, and a source that drops partway fails before
/// anything at the replica has been deleted rather than after.
/// </para>
/// </remarks>
public static class ReplicaRepair
{
    /// <summary>
    /// Proves what <paramref name="store"/> holds under <paramref name="key"/>:
    /// that it is held, that its envelope names the blob the key derives from,
    /// and that every byte still hashes to its sealed digest.
    /// </summary>
    /// <param name="repositoryId">The repository the blob belongs to.</param>
    /// <param name="keys">The repository's keys: the key-ID key binds the store key, the class keys open the footer.</param>
    /// <param name="store">The store to read.</param>
    /// <param name="key">The blob's store key.</param>
    /// <param name="cancellationToken">Cancels the proof.</param>
    public static async ValueTask<CopyProof> ProveAsync(
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        IObjectStore store,
        ObjectKey key,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(keys);
        ThrowHelper.ThrowIfNull(store);

        var metadata = await store.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        if (metadata.Metadata is not { } found)
        {
            return new CopyProof(Held: false, Sound: false, Length: 0, "not held there");
        }

        var length = found.Length;
        using var verifier = new VerifyEngine(repositoryId, keys, store);
        var verdict = await verifier
            .VerifyBlobAsync(key, length, VerifyLevel.FooterAndDigest, bindKey: true, cancellationToken)
            .ConfigureAwait(false);
        return verdict.Ok
            ? new CopyProof(Held: true, Sound: true, length, string.Empty)
            : new CopyProof(Held: true, Sound: false, length, verdict.Detail ?? "it does not match what was sealed");
    }

    /// <summary>
    /// Replaces <paramref name="key"/> at <paramref name="replica"/> from the
    /// first source whose copy proves sound, reading the source directly.
    /// </summary>
    /// <inheritdoc cref="RepairAsync(RepositoryId, RepositoryKeySet, IObjectStore, ObjectKey, IReadOnlyList{CopySource}, IObjectStore?, CancellationToken)"/>
    public static ValueTask<ReplicaRepairOutcome> RepairAsync(
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        IObjectStore replica,
        ObjectKey key,
        IReadOnlyList<CopySource> sources,
        CancellationToken cancellationToken) =>
        RepairAsync(repositoryId, keys, replica, key, sources, scratch: null, cancellationToken);

    /// <summary>
    /// Replaces <paramref name="key"/> at <paramref name="replica"/> from the
    /// first source whose copy proves sound.
    /// </summary>
    /// <param name="repositoryId">The repository the blob belongs to.</param>
    /// <param name="keys">The repository's keys.</param>
    /// <param name="replica">The store holding the damaged object.</param>
    /// <param name="key">The damaged object's store key.</param>
    /// <param name="sources">Where a sound copy might be, in the order to try them.</param>
    /// <param name="scratch">
    /// Where to stage a source's copy before proving it, or null to prove and
    /// read the source where it sits. Whatever is staged is removed again.
    /// </param>
    /// <param name="cancellationToken">Cancels the repair; a cancelled repair has touched nothing it had not finished.</param>
    /// <returns>Which source served, or why none did.</returns>
    public static ValueTask<ReplicaRepairOutcome> RepairAsync(
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        IObjectStore replica,
        ObjectKey key,
        IReadOnlyList<CopySource> sources,
        IObjectStore? scratch,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(replica);
        return RepairAsync(repositoryId, keys, new StoreTarget(replica), key, sources, scratch, cancellationToken);
    }

    /// <summary>
    /// Replaces <paramref name="key"/> at <paramref name="target"/> from the
    /// first source whose copy proves sound, and proves it again where the
    /// target says it landed.
    /// </summary>
    /// <param name="repositoryId">The repository the blob belongs to.</param>
    /// <param name="keys">The repository's keys.</param>
    /// <param name="target">Where the proven copy is put, and read back from.</param>
    /// <param name="key">The damaged object's store key.</param>
    /// <param name="sources">Where a sound copy might be, in the order to try them.</param>
    /// <param name="scratch">
    /// Where to stage a source's copy before proving it, or null to prove and
    /// read the source where it sits. Whatever is staged is removed again.
    /// </param>
    /// <param name="cancellationToken">Cancels the repair; a cancelled repair has touched nothing it had not finished.</param>
    /// <returns>Which source served, or why none did.</returns>
    public static async ValueTask<ReplicaRepairOutcome> RepairAsync(
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        IRepairTarget target,
        ObjectKey key,
        IReadOnlyList<CopySource> sources,
        IObjectStore? scratch,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(keys);
        ThrowHelper.ThrowIfNull(target);
        ThrowHelper.ThrowIfNull(sources);

        var refusals = new List<string>();
        foreach (var source in sources)
        {
            var candidate = await CandidateAsync(source, key, scratch, refusals, cancellationToken)
                .ConfigureAwait(false);
            if (candidate is null)
            {
                continue;
            }

            try
            {
                var proof = await ProveAsync(repositoryId, keys, candidate, key, cancellationToken)
                    .ConfigureAwait(false);
                if (!proof.Sound)
                {
                    refusals.Add($"{source.Name}: {(proof.Held ? proof.Detail : "does not hold it")}");
                    continue;
                }

                // Proven: only now is the target asked to give up its copy.
                var install = await target.InstallAsync(key, candidate, proof.Length, cancellationToken)
                    .ConfigureAwait(false);
                if (install.Gone)
                {
                    return new ReplicaRepairOutcome(key.Value, RepairedFrom: null, install.Detail) { Gone = true };
                }

                if (!install.Installed)
                {
                    // About the target's own copy: every other source's copy,
                    // once proven, is these same bytes, and would be refused
                    // for the same reason.
                    refusals.Add(install.Detail);
                    break;
                }

                var landedAt = await target.LandedAsync(cancellationToken).ConfigureAwait(false);
                var landed = await ProveAsync(repositoryId, keys, landedAt, key, cancellationToken)
                    .ConfigureAwait(false);
                if (landed.Sound)
                {
                    return new ReplicaRepairOutcome(key.Value, source.Name, string.Empty);
                }

                refusals.Add($"{source.Name}: its copy did not prove sound where it landed ({landed.Detail})");
            }
            catch (Exception exception) when (exception is not OperationCanceledException && scratch is null)
            {
                // A source that fails partway has not served. Unstaged, that
                // can happen after the delete; the next source fills the key
                // if it can, and the outcome says so if none can. Staged, the
                // source was read in full before anything here began, so a
                // failure is the replica's or the scratch's own, the next
                // source would meet it too, and it is the caller's to report.
                refusals.Add($"{source.Name}: {exception.Message}");
            }
            finally
            {
                await DiscardAsync(scratch, key).ConfigureAwait(false);
            }
        }

        return new ReplicaRepairOutcome(
            key.Value, RepairedFrom: null,
            refusals.Count == 0 ? "no source to repair it from" : string.Join("; ", refusals));
    }

    /// <summary>
    /// Opens a source and, given a scratch store, stages its copy there; null,
    /// with the reason recorded, when the source cannot serve.
    /// </summary>
    private static async ValueTask<IObjectStore?> CandidateAsync(
        CopySource source, ObjectKey key, IObjectStore? scratch, List<string> refusals,
        CancellationToken cancellationToken)
    {
        IObjectStore? store;
        try
        {
            store = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A source that cannot be reached is one that did not serve, and
            // nothing at the replica has been touched.
            refusals.Add($"{source.Name}: {exception.Message}");
            return null;
        }

        if (store is null)
        {
            refusals.Add($"{source.Name}: could not be reached");
            return null;
        }

        if (scratch is null)
        {
            return store;
        }

        try
        {
            if (!(await store.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false)).Found)
            {
                refusals.Add($"{source.Name}: does not hold it");
                return null;
            }

            await scratch.DeleteAsync(key, DeleteConditions.None, cancellationToken).ConfigureAwait(false);
            await scratch.PutAsync(key, token => OpenAsync(store, key, token), PutConditions.None, cancellationToken)
                .ConfigureAwait(false);
            return scratch;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            refusals.Add($"{source.Name}: {exception.Message}");
            await DiscardAsync(scratch, key).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Removes a staged copy. One left behind is harmless — the next staging
    /// of that key deletes it first — so a scratch that will not let go must
    /// not replace the answer the caller is owed with its own complaint.
    /// </summary>
    private static async ValueTask DiscardAsync(IObjectStore? scratch, ObjectKey key)
    {
        if (scratch is null)
        {
            return;
        }

        try
        {
            await scratch.DeleteAsync(key, DeleteConditions.None, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for the next staging of this key to clear.
        }
    }

    /// <summary>
    /// A store this side writes: the damaged object deleted and the copy put
    /// in its place, since a put never overwrites (specification 01 §4).
    /// </summary>
    private sealed class StoreTarget(IObjectStore replica) : IRepairTarget
    {
        public async ValueTask<RepairInstall> InstallAsync(
            ObjectKey key, IObjectStore candidate, long length, CancellationToken cancellationToken)
        {
            // A delete that finds nothing is not a failure — the key is being
            // filled either way.
            await replica.DeleteAsync(key, DeleteConditions.None, cancellationToken).ConfigureAwait(false);
            await replica.PutAsync(key, token => OpenAsync(candidate, key, token), PutConditions.None, cancellationToken)
                .ConfigureAwait(false);
            return RepairInstall.Done;
        }

        public ValueTask<IObjectStore> LandedAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(replica);
    }

    /// <summary>Opens the proven copy for whatever puts it in place.</summary>
    /// <param name="store">The store holding the proven copy.</param>
    /// <param name="key">Its key.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    public static async ValueTask<Stream> OpenAsync(IObjectStore store, ObjectKey key, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);

        var read = await store.OpenReadAsync(key, range: null, cancellationToken).ConfigureAwait(false);
        return read.Outcome == OpenReadOutcome.Found && read.Content is { } content
            ? content
            : throw new IOException($"'{key.Value}' could not be read back from where it was proven");
    }
}
