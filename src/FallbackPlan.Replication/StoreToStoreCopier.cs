using Bodu;
using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Replication;

/// <summary>What one convergence pass did.</summary>
/// <param name="Copied">Objects the destination lacked and now holds.</param>
/// <param name="AlreadyHeld">Objects the destination already held.</param>
public sealed record CopyOutcome(long Copied, long AlreadyHeld)
{
    /// <summary>Objects examined in total.</summary>
    public long Examined => Copied + AlreadyHeld;
}

/// <summary>
/// How much of what a destination is owed it holds, as a pass discovers it.
/// </summary>
/// <remarks>
/// <para>
/// The denominator a completion figure needs, produced as a by-product of
/// work the pass already does: converging a destination means listing both
/// sides, so the bytes are in hand and only need adding up. Measuring this
/// on a status poll instead would mean listing a whole replica to answer a
/// question nobody asked.
/// </para>
/// <para>
/// Reported as it goes rather than returned at the end, because the answer
/// matters most when the pass does <em>not</em> finish: a drive pulled
/// halfway leaves a destination genuinely part-full, and a figure that only
/// exists on success could never say so.
/// </para>
/// </remarks>
/// <param name="HeldBytes">Bytes the destination holds of what it is owed.</param>
/// <param name="OwedBytes">Bytes it is owed in total. Zero when the source has nothing for it.</param>
public sealed record CopyProgress(long HeldBytes, long OwedBytes);

/// <summary>
/// How much of the namespace a pass walks (ADR-0056): the dependency phases
/// alone, or those plus the catch-all sweep for objects under prefixes no
/// phase names.
/// </summary>
/// <remarks>
/// The catch-all phase is a correctness net for a repository written by a
/// version this one has never heard of, and it is the only part of a pass that
/// cannot be scoped to a prefix — "every key no named phase claims" has to be
/// found by walking everything. Paying that on every poll, for a set of objects
/// that is normally empty, is what made a pass cost the archive rather than the
/// change. An incremental pass leaves it out; a reconciling pass takes it, and
/// a reconciling pass is what runs on the reconciliation cadence.
/// </remarks>
public enum CopyScope
{
    /// <summary>The named dependency phases only.</summary>
    Incremental,

    /// <summary>Every phase, the catch-all sweep included.</summary>
    Reconcile,
}

/// <summary>What one filtered convergence did — the FR-GC-010 shape.</summary>
/// <param name="Copied">Objects the destination lacked and its policy keeps.</param>
/// <param name="AlreadyHeld">Objects the destination already held and keeps.</param>
/// <param name="Deleted">Objects the destination held and its policy no longer keeps.</param>
/// <param name="Spared">Objects the policy dropped but a sibling is still owed — held for now (FR-GC-009).</param>
public sealed record ConvergeOutcome(long Copied, long AlreadyHeld, long Deleted, long Spared = 0);

/// <summary>
/// Copies one repository archive's missing objects from any object store to
/// any other — the fan-out primitive of ADR-0034 §3, and the seam a cloud
/// provider later plugs into (ADR-0012 Amendment 2). It reads raw objects and
/// never decrypts one; what a wire cannot read, a copier cannot either.
/// </summary>
/// <remarks>
/// <para>
/// The ordering discipline is the caller obligation ADR-0012 Amendment 2
/// records, and it is this type's whole reason to exist over a naive loop:
/// objects copy in dependency phases — identity first, then blobs, then the
/// metadata that references them, snapshots last — so a copy interrupted at
/// any byte leaves the destination a <i>lagging but valid</i> replica. A
/// snapshot object's presence at the destination therefore means everything
/// it references is already there, which is ADR-0011's per-replica commit
/// rule enforced by copy order.
/// </para>
/// <para>
/// Interruption costs nothing but progress: every put is create-if-absent
/// (<see cref="PutOutcome.AlreadyExists"/> is the idempotent-retry answer),
/// so a re-run converges from the destination's own inventory with no
/// checkpoint to keep — the same resumption story peer replication proved
/// (peer-protocol 03 §5).
/// </para>
/// </remarks>
public static class StoreToStoreCopier
{
    /// <summary>
    /// The dependency phases, in copy order. Within a phase, order is the
    /// store's ordinal listing order and carries no meaning. The catch-all
    /// phase (<c>""</c>) exists so an object under a prefix this list has
    /// never heard of is copied rather than silently skipped — before
    /// snapshots, because only snapshots assert completeness.
    /// </summary>
    private static readonly string[] PhasePrefixes =
    [
        "repository-format",
        "keys/",
        "blobs/",
        "journal/",
        "index/delta/",
        "index/checkpoint/",

        // Named, not left to the catch-all: hints and audit records are part
        // of the namespace specification 01 §2 defines, so a pass that skipped
        // the catch-all sweep would leave real objects behind rather than
        // hypothetical ones (ADR-0056). The catch-all is for prefixes this
        // build has never heard of, and it is only correct to leave it out of
        // an incremental pass if everything the build DOES write is named here.
        "hints/",
        "audit/",
        "",
        "snapshots/",
    ];

    /// <summary>
    /// Prefixes that never leave the staging archive: lifecycle objects
    /// belong to the collector that runs there, and destinations are
    /// converged, never collected (ADR-0009 Amendment 4) — a tombstone or a
    /// lease at a replica would be an instruction nobody there may act on.
    /// </summary>
    private static readonly string[] StagingOnlyPrefixes = ["tombstones/", "leases/"];

    /// <summary>Converges the destination to hold every object the source holds.</summary>
    /// <param name="source">The archive to read — a set's staging archive, ordinarily.</param>
    /// <param name="destination">The store to fill.</param>
    /// <param name="cancellationToken">Stops the copy; a re-run resumes from the destination's inventory.</param>
    /// <param name="destinationName">The destination's configured name, for the log alone.</param>
    /// <param name="logger">Where the pass reports itself.</param>
    /// <param name="progress">
    /// Told how much of what the destination is owed it holds, as the pass
    /// discovers it. Reported while copying rather than returned at the end,
    /// so a pass that dies halfway still leaves the caller a true figure.
    /// </param>
    /// <param name="scope">
    /// Whether to walk the catch-all phase as well as the named ones. A pass
    /// on the reconciliation cadence takes it; the ones between do not.
    /// </param>
    /// <returns>What was copied and what was already there.</returns>
    public static async ValueTask<CopyOutcome> CopyAsync(
        IObjectStore source,
        IObjectStore destination,
        CancellationToken cancellationToken,
        string? destinationName = null,
        ILogger? logger = null,
        IProgress<CopyProgress>? progress = null,
        CopyScope scope = CopyScope.Reconcile)
    {
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(destination);

        var log = logger ?? NullLogger.Instance;
        var name = destinationName ?? "the destination";

        Log.ReplicationStarting(log, name, "copy");

        var copied = 0L;
        var alreadyHeld = 0L;

        // The completion figures. Owed grows as the phases reveal what the
        // source holds, so an early reading understates the denominator —
        // which is why the pair is only recorded once the pass ends, and why
        // both halves travel together rather than as two independent numbers
        // a reader could pair up out of step.
        var heldBytes = 0L;
        var owedBytes = 0L;

        foreach (var phase in PhasePrefixes)
        {
            if (Skip(phase, scope))
            {
                continue;
            }

            // The destination's inventory for THIS phase, and released with
            // it: the diff that makes a catch-up cost the gap rather than the
            // archive, without the whole archive's key set resident to answer
            // it. Null for the catch-all phase, which cannot be scoped and is
            // answered per candidate instead.
            var held = await HeldUnderAsync(destination, phase, cancellationToken).ConfigureAwait(false);

            await foreach (var entry in source.ListAsync(PrefixFor(phase), ListOptions.Default, cancellationToken)
                .ConfigureAwait(false))
            {
                if (StagingOnly(entry.Key.Value) || !InPhase(entry.Key.Value, phase))
                {
                    continue;
                }

                owedBytes += entry.Length;

                if (await HoldsAsync(destination, held, entry.Key, cancellationToken).ConfigureAwait(false))
                {
                    alreadyHeld++;
                    heldBytes += entry.Length;
                    progress?.Report(new CopyProgress(heldBytes, owedBytes));
                    continue;
                }

                var put = await destination.PutAsync(
                    entry.Key,
                    async token =>
                    {
                        var read = await source.OpenReadAsync(entry.Key, range: null, token).ConfigureAwait(false);
                        return read.Outcome == OpenReadOutcome.Found && read.Content is not null
                            ? read.Content
                            : throw new IOException(
                                $"Object {entry.Key.Value} listed but could not be read to copy.");
                    },
                    PutConditions.None,
                    cancellationToken).ConfigureAwait(false);

                if (put.Outcome == PutOutcome.AlreadyExists)
                {
                    alreadyHeld++;
                }
                else
                {
                    copied++;
                    Log.ObjectCopied(log, entry.Key);
                }

                // Counted on both answers: AlreadyExists means the
                // destination holds these bytes too, which is the
                // question this figure asks.
                heldBytes += entry.Length;
                progress?.Report(new CopyProgress(heldBytes, owedBytes));
            }
        }

        Log.ReplicationComplete(log, name, "copy", copied, alreadyHeld, deleted: 0);

        return new CopyOutcome(copied, alreadyHeld);
    }

    /// <summary>
    /// Converges the destination to hold exactly what its policy keeps
    /// (FR-GC-010, ADR-0009 Amendment 4): pushes kept objects the destination
    /// lacks in the usual dependency phases, then deletes objects the policy
    /// dropped in <b>reverse</b> phase order — snapshots first, blobs last —
    /// so an interrupted pass never leaves a snapshot present whose closure
    /// has already gone. The destination's own reachability is never
    /// consulted; the keep decision is entirely the caller's plan.
    /// </summary>
    /// <param name="source">The set's staging archive.</param>
    /// <param name="destination">The replica store to converge.</param>
    /// <param name="keeps">Whether this destination's policy keeps a key. Identity and infrastructure keys must answer true.</param>
    /// <param name="cancellationToken">Stops the pass; a re-run converges from the destination's inventory.</param>
    /// <param name="destinationName">The destination's configured name, for the log alone.</param>
    /// <param name="logger">Where the pass reports itself.</param>
    /// <param name="spares">
    /// The drop half's second veto, or null when nothing is owed: a key this
    /// destination's policy drops but a sibling destination is still owed
    /// stays put, because under direct-ship (ADR-0046) the replicas are the
    /// only holders and this may be the last copy (FR-GC-009). Never
    /// consulted by the push half — a spare is held where it already is, not
    /// propagated.
    /// </param>
    /// <param name="progress">
    /// Told how much of its own keep-set the destination holds, as the pass
    /// discovers it.
    /// </param>
    /// <param name="scope">
    /// Whether to walk the catch-all phase as well as the named ones. A pass
    /// on the reconciliation cadence takes it; the ones between do not.
    /// </param>
    /// <returns>What moved and what went.</returns>
    public static async ValueTask<ConvergeOutcome> ConvergeAsync(
        IObjectStore source,
        IObjectStore destination,
        Func<string, bool> keeps,
        CancellationToken cancellationToken,
        string? destinationName = null,
        ILogger? logger = null,
        Func<string, bool>? spares = null,
        IProgress<CopyProgress>? progress = null,
        CopyScope scope = CopyScope.Reconcile)
    {
        var log = logger ?? NullLogger.Instance;
        var name = destinationName ?? "the destination";

        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(destination);
        ThrowHelper.ThrowIfNull(keeps);

        Log.ReplicationStarting(log, name, "converge");

        var copied = 0L;
        var alreadyHeld = 0L;

        // Owed is what this destination's OWN policy keeps, not what the
        // source holds: a narrow override is complete when it holds its own
        // keep-set, and measuring it against a wide sibling's would leave it
        // permanently short of a hundred per cent for doing exactly as told.
        var heldBytes = 0L;
        var owedBytes = 0L;

        foreach (var phase in PhasePrefixes)
        {
            if (Skip(phase, scope))
            {
                continue;
            }

            var held = await HeldUnderAsync(destination, phase, cancellationToken).ConfigureAwait(false);

            await foreach (var entry in source.ListAsync(PrefixFor(phase), ListOptions.Default, cancellationToken)
                .ConfigureAwait(false))
            {
                if (StagingOnly(entry.Key.Value) || !keeps(entry.Key.Value)
                    || !InPhase(entry.Key.Value, phase))
                {
                    continue;
                }

                owedBytes += entry.Length;

                if (await HoldsAsync(destination, held, entry.Key, cancellationToken).ConfigureAwait(false))
                {
                    alreadyHeld++;
                    heldBytes += entry.Length;
                    progress?.Report(new CopyProgress(heldBytes, owedBytes));
                    continue;
                }

                var put = await destination.PutAsync(
                    entry.Key,
                    async token =>
                    {
                        var read = await source.OpenReadAsync(entry.Key, range: null, token).ConfigureAwait(false);
                        return read.Outcome == OpenReadOutcome.Found && read.Content is not null
                            ? read.Content
                            : throw new IOException(
                                $"Object {entry.Key.Value} listed but could not be read to copy.");
                    },
                    PutConditions.None,
                    cancellationToken).ConfigureAwait(false);

                if (put.Outcome == PutOutcome.AlreadyExists)
                {
                    alreadyHeld++;
                }
                else
                {
                    copied++;
                    Log.ObjectCopied(log, entry.Key);
                }

                heldBytes += entry.Length;
                progress?.Report(new CopyProgress(heldBytes, owedBytes));
            }
        }

        // The drop half, reverse dependency order: a snapshot object goes
        // before anything it references, so the replica is lagging-but-valid
        // at every interruption point, exactly as the push half guarantees.
        //
        // Each phase re-reads both sides under its own prefix. The source's
        // listing bounds what may be condemned: a key the destination holds
        // that staging no longer lists — a trimmed data blob — may be the only
        // copy left, and a filter computed from staging cannot vouch for it
        // either way. Unknown is kept (ADR-0034 §6's trim rests on this). Read
        // HERE, after the push half: an hours-long copy must not condemn on
        // the strength of a stale opening inventory (ADR-0029 Amendment 2).
        var deleted = 0L;
        var spared = 0L;
        foreach (var phase in PhasePrefixes.Reverse())
        {
            if (Skip(phase, scope))
            {
                continue;
            }

            var present = await HeldUnderAsync(destination, phase, cancellationToken).ConfigureAwait(false)
                ?? await EveryKeyUnderAsync(destination, phase, cancellationToken).ConfigureAwait(false);
            var sourceKeys = await EveryKeyUnderAsync(source, phase, cancellationToken).ConfigureAwait(false);

            foreach (var key in present)
            {
                // Identity and keys never go, whatever the filter says — a
                // replica without its descriptor is not a repository at all.
                // And nothing goes that the source does not list: a key only
                // the destination holds may be a trimmed object's last copy.
                if (!InPhase(key, phase) || keeps(key) || !sourceKeys.Contains(key)
                    || key is "repository-format" || key.StartsWith("keys/", StringComparison.Ordinal))
                {
                    continue;
                }

                // The spare: dropped by this destination's policy, still owed
                // to a sibling. Counted apart from the refused deletes because
                // it is a choice, not a store answering no.
                if (spares?.Invoke(key) ?? false)
                {
                    spared++;
                    continue;
                }

                var outcome = await destination.DeleteAsync(
                    ObjectKey.Parse(key), DeleteConditions.None, cancellationToken).ConfigureAwait(false);
                if (outcome.Outcome == DeleteOutcome.Deleted)
                {
                    deleted++;
                }
                else
                {
                    // Counted as not-deleted and, until now, said nowhere: a
                    // replica quietly keeping what its policy dropped.
                    Log.ConvergeDeleteRefused(log, name, ObjectKey.Parse(key), outcome.Outcome);
                }
            }
        }

        if (spared > 0)
        {
            Log.ConvergeSpared(log, name, spared);
        }

        Log.ReplicationComplete(log, name, "converge", copied, alreadyHeld, deleted);

        return new ConvergeOutcome(copied, alreadyHeld, deleted, spared);
    }

    /// <summary>The listing prefix a phase walks under.</summary>
    /// <remarks>
    /// The catch-all phase has no prefix to scope to — "every key no named
    /// phase claims" is not expressible as a prefix — so it walks the whole
    /// namespace. That is the one full walk a pass makes, and
    /// <see cref="CopyScope.Incremental"/> is what leaves it out.
    /// </remarks>
    /// <param name="phase">The phase prefix, empty for the catch-all.</param>
    private static ObjectPrefix PrefixFor(string phase) =>
        phase.Length == 0 ? ObjectPrefix.All : ObjectPrefix.Parse(phase);

    /// <summary>Whether this phase is left out under this scope.</summary>
    /// <param name="phase">The phase prefix.</param>
    /// <param name="scope">What the caller asked for.</param>
    private static bool Skip(string phase, CopyScope scope) =>
        phase.Length == 0 && scope == CopyScope.Incremental;

    /// <summary>
    /// What the destination already holds under one phase, or null for the
    /// catch-all phase, whose membership is answered one candidate at a time.
    /// </summary>
    /// <remarks>
    /// Per phase rather than per pass. The whole-archive inventory used to be
    /// read once and held for the length of the copy, which made a pass's peak
    /// memory a function of the archive's object count rather than of the work
    /// in front of it (NFR-PERF-008). The catch-all phase is excluded because
    /// listing it means listing everything, which is the same key set by
    /// another name — and the objects it finds are, by construction, ones no
    /// phase expects, so there are normally none to ask about.
    /// </remarks>
    /// <param name="destination">The store being filled.</param>
    /// <param name="phase">The phase prefix.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    private static async ValueTask<HashSet<string>?> HeldUnderAsync(
        IObjectStore destination, string phase, CancellationToken cancellationToken) =>
        phase.Length == 0 ? null : await EveryKeyUnderAsync(destination, phase, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Every key one store holds under one phase.</summary>
    /// <param name="store">The store to read.</param>
    /// <param name="phase">The phase prefix.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    private static async ValueTask<HashSet<string>> EveryKeyUnderAsync(
        IObjectStore store, string phase, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in store.ListAsync(PrefixFor(phase), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            if (InPhase(entry.Key.Value, phase))
            {
                keys.Add(entry.Key.Value);
            }
        }

        return keys;
    }

    /// <summary>
    /// Whether the destination already holds a key: from the phase's inventory
    /// where there is one, and by asking about that object alone where there
    /// is not.
    /// </summary>
    /// <param name="destination">The store being filled.</param>
    /// <param name="held">The phase's inventory, or null for the catch-all phase.</param>
    /// <param name="key">The object in question.</param>
    /// <param name="cancellationToken">Stops the probe.</param>
    private static async ValueTask<bool> HoldsAsync(
        IObjectStore destination, HashSet<string>? held, ObjectKey key, CancellationToken cancellationToken) =>
        held?.Contains(key.Value)
        ?? (await destination.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false)).Found;

    private static bool StagingOnly(string key) =>
        StagingOnlyPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));

    private static bool InPhase(string key, string phase) => phase switch
    {
        // The catch-all: anything no named phase claims.
        "" => !PhasePrefixes.Any(prefix => prefix.Length > 0 && Matches(key, prefix)),
        _ => Matches(key, phase),
    };

    private static bool Matches(string key, string prefix) =>
        prefix.EndsWith('/') ? key.StartsWith(prefix, StringComparison.Ordinal)
        : string.Equals(key, prefix, StringComparison.Ordinal);
}
