namespace FallbackPlan.Storage.Abstractions;

/// <summary>
/// A store whose keys live under a prefix of a larger namespace — a folder of
/// an object store's bucket or container — and which can name the folders
/// directly beneath that prefix (ADR-0091, ADR-0093): the repositories a
/// destination holds side by side, for a restore or an adoption that has to
/// find its own among them.
/// </summary>
public interface IPrefixedObjectStore : IObjectStore
{
    /// <summary>The folders directly under this store's prefix, each named once, in ordinal order.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    IAsyncEnumerable<string> ListChildrenAsync(CancellationToken cancellationToken);
}
