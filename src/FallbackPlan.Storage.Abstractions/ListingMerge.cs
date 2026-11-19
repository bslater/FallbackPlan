using System.Runtime.CompilerServices;

namespace FallbackPlan.Storage.Abstractions;

/// <summary>
/// One listing made of several, for a store that stands in front of others
/// (ADR-0012 Amendment 8). Each part lists in ordinal key order, as every
/// store does, and the whole must too
/// (docs/architecture/05-storage-providers.md §2.3): a reader that resumes
/// after a key is listed only what sorts after it, so a key a later part
/// lists, sorting before one an earlier part listed, is never seen.
/// </summary>
public static class ListingMerge
{
    /// <summary>
    /// Lists every key the listings hold, in ordinal key order. A key more
    /// than one listing holds is listed once, as the earliest of them lists
    /// it, so a store puts first the part that answers for a key.
    /// </summary>
    /// <param name="listings">The parts, each in ordinal key order, in the order they answer for a key.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The merged listing.</returns>
    public static async IAsyncEnumerable<ObjectEntry> MergeAsync(
        IReadOnlyList<IAsyncEnumerable<ObjectEntry>> listings,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listings);

        // A part is held until it ends or the reader stops: an early stop
        // lets go of every part, not only the one that was being read.
        var parts = new IAsyncEnumerator<ObjectEntry>?[listings.Count];
        try
        {
            for (var index = 0; index < parts.Length; index++)
            {
                parts[index] = listings[index].GetAsyncEnumerator(cancellationToken);
                await AdvanceAsync(parts, index).ConfigureAwait(false);
            }

            while (true)
            {
                var first = -1;
                for (var index = 0; index < parts.Length; index++)
                {
                    if (parts[index] is { } part
                        && (first < 0 || string.CompareOrdinal(part.Current.Key.Value, parts[first]!.Current.Key.Value) < 0))
                    {
                        first = index;
                    }
                }

                if (first < 0)
                {
                    yield break;
                }

                var entry = parts[first]!.Current;
                for (var index = first; index < parts.Length; index++)
                {
                    if (parts[index] is { } part && string.Equals(part.Current.Key.Value, entry.Key.Value, StringComparison.Ordinal))
                    {
                        await AdvanceAsync(parts, index).ConfigureAwait(false);
                    }
                }

                yield return entry;
            }
        }
        finally
        {
            foreach (var part in parts)
            {
                if (part is not null)
                {
                    await part.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static async ValueTask AdvanceAsync(IAsyncEnumerator<ObjectEntry>?[] parts, int index)
    {
        var part = parts[index]!;
        if (!await part.MoveNextAsync().ConfigureAwait(false))
        {
            parts[index] = null;
            await part.DisposeAsync().ConfigureAwait(false);
        }
    }
}
