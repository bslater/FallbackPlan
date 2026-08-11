using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue;

namespace FallbackPlan.Repository;

/// <summary>
/// Whether an object the index already locates may be referenced instead of
/// written again. Asynchronous because the default domain answers it with a
/// read (<see cref="DedupTrustGate"/>).
/// </summary>
public delegate ValueTask<bool> ReusePredicate(ObjectId objectId, CancellationToken cancellationToken);

/// <summary>
/// The dedup trust domain applied at the reuse decision (specification
/// 09 §5; [ADR-0006](../../docs/adr/0006-object-identifiers-and-dedup-trust-domains.md);
/// FR-DED-002).
/// </summary>
/// <remarks>
/// <para>
/// Reuse used to be decided by index presence alone, which meant the
/// configured domain changed nothing and choosing the hardened one produced
/// the behaviour of not choosing it — silently. This is the thing ADR-0006
/// was written for: <b>a writer only trusts its own bytes for free</b>.
/// </para>
/// <para>
/// The gate is writer attribution first, domain second, and that ordering is
/// what makes the default affordable. A segment this writer wrote needs no
/// verification in any domain — it is the same device's own record — so a
/// single-writer repository performs zero verification reads, which is
/// exactly what FR-DED-002 demands of it. Attribution comes from
/// <c>writer_id</c> on the index entry, which is durable and recovered by a
/// rebuild, so this survives losing the catalogue.
/// </para>
/// <para>
/// What does not survive a rebuild is the memory of what has been verified.
/// That is accepted rather than solved: recording verification outcomes
/// durably would mean a new repository object, designed before anything
/// consumes it and frozen into format v1. Losing the catalogue therefore
/// re-imposes verification once, and
/// [PT-12](../../docs/review/2026-08-fix-pressure-test.md) says so.
/// </para>
/// </remarks>
internal sealed class DedupTrustGate(
    DedupTrustDomain domain,
    WriterId writerId,
    CatalogueGate catalogue,
    TargetedRecordReader reader)
{
    // The archive pipeline asks this question from several threads at once, so
    // both counters and the catalogue are shared state. The catalogue is
    // serialised by the gate; this is the only other thing to get right.
    private int _verificationReads;

    /// <summary>The number of verification reads this publication performed.</summary>
    public int VerificationReads => Volatile.Read(ref _verificationReads);

    /// <summary>Whether <paramref name="objectId"/> may be referenced rather than written again.</summary>
    public async ValueTask<bool> MayReuseAsync(ObjectId objectId, CancellationToken cancellationToken)
    {
        var candidate = catalogue.Read(c => c.FindReuseCandidate(objectId));

        if (candidate is null)
        {
            return false;
        }

        // Before any trust question: does the blob the row points at still
        // exist? The catalogue is a cache and its row can outlive the store's
        // object, and a reuse granted on the row's word alone commits a
        // manifest whose reference dangles — unrestorable, and undetected
        // until restore time. A memoized metadata probe per distinct blob is
        // what "trusts its own bytes for free" can afford while still being
        // about bytes rather than about a cache row.
        if (!await reader.IsBlobPresentAsync(objectId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // This device's own record. No domain requires a device to verify
        // bytes it wrote itself, and this branch is the whole of "a fresh
        // single-device repository performs no verification reads".
        if (candidate.WriterId.Equals(writerId))
        {
            return true;
        }

        switch (domain)
        {
            // Another writer's segment is not reused at all. The cost is
            // duplicate storage across a user's devices, which is the trade
            // this domain exists to offer.
            case DedupTrustDomain.Device:
                return false;

            // A uniformly trusted fleet, opting out of the read.
            case DedupTrustDomain.RepositoryUnverified:
                return true;

            case DedupTrustDomain.Repository:
                break;

            default:
                // An undefined domain is refused, not guessed. Reuse is the
                // permissive answer, so an unreadable policy must not produce
                // it.
                return false;
        }

        if (candidate.Verified)
        {
            return true;
        }

        Interlocked.Increment(ref _verificationReads);

        // Outside the catalogue gate on purpose: this is the one slow thing
        // here, and holding the connection across it would serialise every
        // other segment's lookup behind one fetch.
        var verification = await reader.VerifyAsync(objectId, cancellationToken).ConfigureAwait(false);

        if (verification == ReuseVerification.Verified)
        {
            catalogue.Write(c => c.RecordVerified(objectId));
            return true;
        }

        // A record that was read and did not verify is evidence, and this is
        // the moment ADR-0006 exists to create: detection at write time, while
        // the source data is still on the disk in front of us, rather than at
        // restore time when it is gone. A record that could not be reached is
        // not evidence of anything and is recorded as nothing.
        if (verification == ReuseVerification.Failed)
        {
            catalogue.Write(c => c.RecordFinding(
                new DamageFinding(
                    DamageKind.CorruptRecord,
                    $"Object {objectId}, stored by another writer, did not verify when offered for reuse; "
                    + "it was written again rather than referenced (ADR-0006)."),
                objectId));
        }

        return false;
    }
}
