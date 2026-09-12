using System.Globalization;
using Bodu;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository.Index;

/// <summary>
/// How far a writer had got, according to the repository rather than
/// according to the writer (NFR-SEC-005).
/// </summary>
/// <remarks>
/// <para>
/// A writer's <c>sequence</c> file records what this machine handed out, and
/// it is exactly as durable as the state directory holding it. Lose it,
/// restore an older copy of it, or point a rebuilt machine at a repository it
/// used to write, and the writer begins handing out numbers the repository
/// already spent. That is caught — the store is immutable, so a colliding put
/// is refused — but it is caught partway through a backup, as an I/O error,
/// and the operator is told a store failed rather than that their machine's
/// allocation state is behind its own history.
/// </para>
/// <para>
/// The repository holds the same fact in a form that survives the machine.
/// This reads it.
/// </para>
/// </remarks>
public static class ObservedHead
{
    /// <summary>
    /// The highest sequence number <paramref name="store"/> attests for
    /// <paramref name="writer"/>, across both planes that record one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two planes, because one sequence space feeds several kinds of object
    /// (08 §2) and neither plane sees all of them:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// the <b>index</b> — signed checkpoint watermarks and signed delta
    /// sequences, plus gaps still inside the bounded patience, all from
    /// <paramref name="index"/> (07 §§4–6);
    /// </description></item>
    /// <item><description>
    /// the <b>journal</b> — whose keys carry the sequence in the clear
    /// (<c>journal/&lt;writer&gt;/&lt;sequence&gt;</c>, 08 §1), so this costs
    /// a prefix listing and no decryption at all.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>The bound this does not reach, stated rather than implied.</b> Blob
    /// counters draw from the same space and are recorded only inside a write
    /// intent's encrypted payload and in each blob's envelope. For a run that
    /// completed they sit between its intent and its delta, so the attested
    /// head is above them; for a run interrupted after its blobs and before
    /// its delta, a counter may exceed everything here. That case is exactly
    /// an unaccounted obligation, which the index reports as a gap and this
    /// method folds in — and behind both, the store's refusal of a colliding
    /// put remains the last line of defence, as it was before this existed.
    /// </para>
    /// </remarks>
    /// <param name="store">The repository to ask.</param>
    /// <param name="writer">The writer to ask about.</param>
    /// <param name="index">The loaded index, for the plane whose sequences are inside signed objects.</param>
    /// <param name="cancellationToken">Cancels the journal listing.</param>
    /// <returns>The highest attested sequence, or zero when the repository attests none.</returns>
    public static async ValueTask<ulong> OfAsync(
        IObjectStore store,
        WriterId writer,
        IndexState index,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(index);

        var head = index.ObservedHeadFor(writer);

        // The journal key's last segment IS the sequence, zero-padded to a
        // fixed width so ordinal listing order is numeric order. Reading it
        // from the key rather than the record is what keeps this a listing.
        var prefix = ObjectPrefix.Parse(
            $"{MetadataStoreKeys.Journal(writer, 0).Value[..^MetadataStoreKeys.Decimal16Length]}");

        await foreach (var entry in store.ListAsync(prefix, ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            var key = entry.Key.Value;
            var separator = key.LastIndexOf('/');
            if (separator < 0)
            {
                continue;
            }

            // A key that does not parse is not this method's damage to
            // report; the index loader and the journal store both name it,
            // and guessing a sequence from it would be worse than ignoring it.
            if (ulong.TryParse(
                    key.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
            {
                head = Math.Max(head, sequence);
            }
        }

        return head;
    }
}
