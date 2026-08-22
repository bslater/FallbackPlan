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

/// <summary>What one filtered convergence did — the FR-GC-010 shape.</summary>
/// <param name="Copied">Objects the destination lacked and its policy keeps.</param>
/// <param name="AlreadyHeld">Objects the destination already held and keeps.</param>
/// <param name="Deleted">Objects the destination held and its policy no longer keeps.</param>
public sealed record ConvergeOutcome(long Copied, long AlreadyHeld, long Deleted);

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
    /// <returns>What was copied and what was already there.</returns>
    public static async ValueTask<CopyOutcome> CopyAsync(
        IObjectStore source,
        IObjectStore destination,
        CancellationToken cancellationToken,
        string? destinationName = null,
        ILogger? logger = null)
    {
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(destination);

        var log = logger ?? NullLogger.Instance;
        var name = destinationName ?? "the destination";

        // The destination's inventory, once: the diff that makes a catch-up
        // pass cost proportional to the gap rather than to the archive.
        var held = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in destination.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            held.Add(entry.Key.Value);
        }

        Log.ReplicationStarting(log, name, held.Count);

        var copied = 0L;
        var alreadyHeld = 0L;
        var matched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var phase in PhasePrefixes)
        {
            await foreach (var entry in source.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
                .ConfigureAwait(false))
            {
                if (StagingOnly(entry.Key.Value)
                    || !InPhase(entry.Key.Value, phase) || !matched.Add(entry.Key.Value))
                {
                    continue;
                }

                if (held.Contains(entry.Key.Value))
                {
                    alreadyHeld++;
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
    /// <returns>What moved and what went.</returns>
    public static async ValueTask<ConvergeOutcome> ConvergeAsync(
        IObjectStore source,
        IObjectStore destination,
        Func<string, bool> keeps,
        CancellationToken cancellationToken,
        string? destinationName = null,
        ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        var name = destinationName ?? "the destination";

        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(destination);
        ThrowHelper.ThrowIfNull(keeps);

        var held = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in destination.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            held.Add(entry.Key.Value);
        }

        Log.ReplicationStarting(log, name, held.Count);

        var copied = 0L;
        var alreadyHeld = 0L;
        var matched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var phase in PhasePrefixes)
        {
            await foreach (var entry in source.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
                .ConfigureAwait(false))
            {
                if (StagingOnly(entry.Key.Value) || !keeps(entry.Key.Value)
                    || !InPhase(entry.Key.Value, phase) || !matched.Add(entry.Key.Value))
                {
                    continue;
                }

                if (held.Contains(entry.Key.Value))
                {
                    alreadyHeld++;
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
            }
        }

        // The source's own listing bounds what the drop half may condemn: a
        // key the destination holds that staging no longer lists — a trimmed
        // data blob — may be the only copy left, and a filter computed from
        // staging cannot vouch for it either way. Unknown is kept (ADR-0034
        // §6's trim rests on this). Listed HERE, after the push half: an
        // hours-long copy must not condemn on the strength of a stale
        // opening inventory (ADR-0029 Amendment 2).
        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in source.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            sourceKeys.Add(entry.Key.Value);
        }

        // The drop half, reverse dependency order: a snapshot object goes
        // before anything it references, so the replica is lagging-but-valid
        // at every interruption point, exactly as the push half guarantees.
        var deleted = 0L;
        foreach (var phase in PhasePrefixes.Reverse())
        {
            foreach (var key in held)
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

        Log.ReplicationComplete(log, name, "converge", copied, alreadyHeld, deleted);

        return new ConvergeOutcome(copied, alreadyHeld, deleted);
    }

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
