namespace FallbackPlan.Repository;

/// <summary>
/// A publication's serialised view of the catalogue.
/// </summary>
/// <remarks>
/// <para>
/// The catalogue is one SQLite connection and a connection is not
/// thread-safe. That cost nothing while the only caller was the tree walk,
/// which is sequential — but verify-on-reuse
/// ([ADR-0006](../../docs/adr/0006-object-identifiers-and-dedup-trust-domains.md))
/// made the archive pipeline ask the catalogue questions too, and that
/// pipeline runs at the configured concurrency. Two threads then share one
/// connection, and the failure is not a clean exception at the boundary: it
/// is a corrupted internal collection inside the provider, surfacing
/// somewhere else entirely.
/// </para>
/// <para>
/// Every catalogue call made <em>while the pipeline is running</em> goes
/// through here. The projection that follows the walk does not: by then the
/// session is drained and there is one thread again.
/// </para>
/// <para>
/// A plain lock rather than a semaphore, because every catalogue operation
/// is a synchronous local query. Nothing is awaited while it is held — the
/// verification read that motivated all this deliberately sits
/// <em>outside</em> the lock, so one slow fetch cannot stall every other
/// segment's lookup behind it.
/// </para>
/// </remarks>
internal sealed class CatalogueGate(Catalogue.Catalogue catalogue)
{
    private readonly Lock _gate = new();

    /// <summary>Runs <paramref name="query"/> against the catalogue, alone.</summary>
    public T Read<T>(Func<Catalogue.Catalogue, T> query)
    {
        lock (_gate)
        {
            return query(catalogue);
        }
    }

    /// <summary>Runs <paramref name="mutation"/> against the catalogue, alone.</summary>
    public void Write(Action<Catalogue.Catalogue> mutation)
    {
        lock (_gate)
        {
            mutation(catalogue);
        }
    }
}
