using Bodu;
using FallbackPlan.Storage.Abstractions;

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

    /// <summary>Converges the destination to hold every object the source holds.</summary>
    /// <param name="source">The archive to read — a set's staging archive, ordinarily.</param>
    /// <param name="destination">The store to fill.</param>
    /// <param name="cancellationToken">Stops the copy; a re-run resumes from the destination's inventory.</param>
    /// <returns>What was copied and what was already there.</returns>
    public static async ValueTask<CopyOutcome> CopyAsync(
        IObjectStore source, IObjectStore destination, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(destination);

        // The destination's inventory, once: the diff that makes a catch-up
        // pass cost proportional to the gap rather than to the archive.
        var held = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in destination.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            held.Add(entry.Key.Value);
        }

        var copied = 0L;
        var alreadyHeld = 0L;
        var matched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var phase in PhasePrefixes)
        {
            await foreach (var entry in source.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
                .ConfigureAwait(false))
            {
                if (!InPhase(entry.Key.Value, phase) || !matched.Add(entry.Key.Value))
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
                }
            }
        }

        return new CopyOutcome(copied, alreadyHeld);
    }

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
