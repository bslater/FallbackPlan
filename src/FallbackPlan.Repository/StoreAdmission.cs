using Bodu;
using FallbackPlan.Repository.Resources;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>
/// What a caller opening a repository intends to do with its store, which is
/// what decides which capabilities it needs
/// (<see cref="StoreAdmission"/>).
/// </summary>
public enum StoreUse
{
    /// <summary>
    /// The caller may publish. The safe default, because a call site that
    /// forgot to say is refused on a degraded store rather than writing to
    /// one that cannot refuse a rewrite.
    /// </summary>
    Writing = 0,

    /// <summary>
    /// The caller only reads — a restore source, an adoption survey, a
    /// verification pass. A peer's replica is this by construction.
    /// </summary>
    ReadingOnly = 1,
}

/// <summary>
/// What the engine requires of a store before it will use one
/// ([ADR-0012](../../docs/adr/0012-storage-provider-contract.md);
/// docs/architecture/05-storage-providers.md §3).
/// </summary>
/// <remarks>
/// <para>
/// The contract has always said two capabilities change engine behaviour
/// rather than merely informing it, and until this existed nothing read
/// either: a provider declaring no conditional create would have been admitted
/// and would have answered <see cref="PutOutcome.Created"/> to a put that
/// overwrote, which breaks INV-BLOB-001 at the provider with nobody told. A
/// promise the code does not keep is worse than a stated incapacity, so the
/// engine states its requirement here and refuses by name.
/// </para>
/// <para>
/// This lives beside the engine rather than beside
/// <see cref="IObjectStore"/> on purpose. The requirement belongs to the
/// consumer: an abstraction that stated what its consumers need would have
/// stopped being one, and the next consumer's needs would have to go here too.
/// </para>
/// <para>
/// <b>Reading and writing ask different questions</b>, and conflating them
/// would refuse a store that is perfectly usable. A reader never puts, so
/// conditional create is nothing to it — and one real store in this product is
/// read-only by construction: a peer's replica over the retrieval session
/// (<c>Agent/PeerRetrievalObjectStore</c>, peer-protocol 07 §1), which is what
/// an adoption from a peer opens.
/// </para>
/// </remarks>
public static class StoreAdmission
{
    /// <summary>
    /// Why this store cannot back a repository the engine will write to, or
    /// null when it can.
    /// </summary>
    /// <param name="capabilities">What the provider declared.</param>
    public static string? RefuseForWriting(StoreCapabilities capabilities)
    {
        ThrowHelper.ThrowIfNull(capabilities);

        // Publication's every durable step is an if-absent put: it is how a
        // blob identifier collision is caught, how a resumed run avoids
        // writing twice, and how two writers in one repository stay apart
        // (ADR-0008). Without it "created" stops meaning "nothing was there".
        if (!capabilities.ConditionalCreate)
        {
            return Strings.StoreAdmission_NoConditionalCreate;
        }

        return RefuseForReading(capabilities);
    }

    /// <summary>
    /// Why this store cannot back a repository the engine will read, or null
    /// when it can.
    /// </summary>
    /// <param name="capabilities">What the provider declared.</param>
    public static string? RefuseForReading(StoreCapabilities capabilities)
    {
        ThrowHelper.ThrowIfNull(capabilities);

        // Every open begins with three ranged reads — the locator, the
        // footer, the envelope — and a restore reads records at offsets
        // inside a blob that may be half a gibibyte. Whole-object reads
        // instead is a behaviour ADR-0012 sketched and nothing implements;
        // until something does, a store without ranges is refused rather
        // than allowed to read a repository a record at a time.
        if (!capabilities.RangedReads)
        {
            return Strings.StoreAdmission_NoRangedReads;
        }

        return null;
    }
}
