using System.Text;
using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Retention.Resources;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Retention;

/// <summary>What one request wrote.</summary>
/// <param name="Generation">
/// The sequence the request counts from: its tombstones' eligible generation,
/// which every copy has to have converged at or past before staging lets the
/// snapshots go. For a request that named nothing new, the latest of the
/// standing requests it named.
/// </param>
/// <param name="AuditSequence">
/// The journal sequence of the audit record published for it, which is also
/// the publication the request's grace waits for; zero when every snapshot
/// named was already requested and audited.
/// </param>
/// <param name="SnapshotIds">The snapshots this call newly requested, as lowercase hex.</param>
public sealed record SnapshotDeletionRequest(ulong Generation, ulong AuditSequence, IReadOnlyList<string> SnapshotIds);

/// <summary>
/// A person's request that snapshots be deleted (FR-GC-013,
/// [ADR-0080](../../docs/adr/0080-a-person-deletes-a-snapshot.md)): the
/// snapshot manifest's tombstone with reason <c>requested</c>, and the journal's
/// audit record naming who asked and for what.
/// </summary>
/// <remarks>
/// <para>
/// The tombstone is the request, not a note about one. Retention re-plans on
/// every pass, so a decision taken once would be undone by the next plan of a
/// policy that keeps the snapshot. Every survey reads the request instead
/// (<see cref="StagingMark.SurveyAsync"/>), and every plan built from a survey,
/// destination keep-sets included, expires the snapshot.
/// </para>
/// <para>
/// A request is honoured only once its signature verifies, and a survey has no
/// grant to verify one with. It verifies against the reclaim public key the
/// write credential carries, which is generation zero's, so a scheduled sync
/// honours a request without the passphrase. A request that will not verify
/// that way changes nothing anyone keeps, and the sweep reports it as it
/// reports any tombstone that will not verify.
/// </para>
/// </remarks>
public static class SnapshotDeletion
{
    /// <summary>
    /// Whether deleting <paramref name="named"/> would leave the set nothing
    /// to restore from: no complete snapshot, when it has one, or no snapshot
    /// at all, when none of its snapshots is complete. A snapshot whose
    /// deletion is already requested counts as gone.
    /// </summary>
    /// <remarks>
    /// The one rule a person's deletion does not override (ADR-0080 §1). A
    /// partial capture cannot stand in for a complete one, as the floor's rule
    /// says, so a set that has a complete snapshot keeps one. A set whose every
    /// capture is partial, from a source that always holds a file it cannot
    /// read, has none to keep, and keeps its last snapshot of any kind instead.
    /// </remarks>
    /// <param name="snapshots">Every snapshot the set's archive holds, as the survey found them.</param>
    /// <param name="named">The ids asked for, in any case.</param>
    /// <returns>True when the request must be refused.</returns>
    public static bool WouldLeaveNothing(IReadOnlyList<SnapshotFact> snapshots, IReadOnlyCollection<string> named)
    {
        ThrowHelper.ThrowIfNull(snapshots);
        ThrowHelper.ThrowIfNull(named);

        var asked = new HashSet<string>(named, StringComparer.OrdinalIgnoreCase);
        var standing = snapshots.Where(snapshot => snapshot.DeletionRequest is null).ToList();
        var complete = standing.Where(snapshot => snapshot.IsComplete).ToList();
        var guarded = complete.Count > 0 ? complete : standing;
        return guarded.Count > 0 && guarded.All(snapshot => asked.Contains(snapshot.SnapshotId));
    }

    /// <summary>
    /// Requests the deletion of <paramref name="snapshots"/>: one
    /// <c>requested</c> tombstone each, replacing any tombstone already at the
    /// snapshot's key, then the audit record. Writes nothing for a snapshot
    /// already requested, and publishes a record only for the snapshots no
    /// record names yet.
    /// </summary>
    /// <remarks>
    /// The audit record is published after the tombstones, and that order is
    /// what makes the request eligible: a tombstone's grace is the next
    /// publication past the decision (11 §3.1), and the record is that
    /// publication. Run it where nothing else publishes for the archive (the
    /// service's writer lane), or a backup publishing between the journal read
    /// and the tombstones would end the grace before the request existed.
    /// </remarks>
    /// <param name="store">The staging archive's store.</param>
    /// <param name="repository">The opened archive.</param>
    /// <param name="writerId">This device's writer identity.</param>
    /// <param name="sequence">The writer's sequence, which the audit record draws its number from.</param>
    /// <param name="snapshots">The snapshots asked for, as the survey found them.</param>
    /// <param name="actor">Who asked: the account signed in, or the client that has none.</param>
    /// <param name="nowUnixMilliseconds">Informational stamps only.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <param name="reclaim">
    /// The authority to author deletions (ADR-0055 §6). A repository declaring
    /// <c>reclaim-authority</c> needs one; null only for a repository written
    /// before the feature.
    /// </param>
    /// <returns>What was written.</returns>
    /// <exception cref="ArgumentException">The actor is empty or too long for the audit record to carry.</exception>
    /// <exception cref="IOException">The store would not hold a request.</exception>
    public static async ValueTask<SnapshotDeletionRequest> RequestAsync(
        IObjectStore store,
        OpenedRepository repository,
        WriterId writerId,
        WriterSequence sequence,
        IReadOnlyList<SurveyedSnapshot> snapshots,
        string actor,
        ulong nowUnixMilliseconds,
        CancellationToken cancellationToken,
        ReclaimAuthority? reclaim = null)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(repository);
        ThrowHelper.ThrowIfNull(sequence);
        ThrowHelper.ThrowIfNull(snapshots);
        ThrowHelper.ThrowIfNullOrWhiteSpace(actor);

        // Checked before anything is written: an audit record that would not
        // encode, found after the tombstones, would leave requests nobody is
        // named as having made.
        if (Encoding.UTF8.GetByteCount(actor) > JournalRecordCodec.MaximumActorBytes)
        {
            throw new ArgumentException(
                Strings.FormatSnapshotDeletion_ActorTooLong(JournalRecordCodec.MaximumActorBytes), nameof(actor));
        }

        var named = snapshots.DistinctBy(snapshot => snapshot.ManifestObjectId).ToList();
        var records = await LoadJournalAsync(store, repository, cancellationToken).ConfigureAwait(false);
        var head = records.Count == 0 ? 0 : records.Max(record => record.Sequence);
        var standing = await ReadRequestsAsync(store, repository, cancellationToken).ConfigureAwait(false);

        // A request that stands is a resume, and its grace is already running.
        var fresh = named.Where(snapshot => !standing.ContainsKey(snapshot.ManifestObjectId)).ToList();
        var generation = head + 1;
        foreach (var snapshot in fresh)
        {
            await WriteRequestAsync(store, repository, writerId, snapshot, generation, nowUnixMilliseconds, reclaim, cancellationToken)
                .ConfigureAwait(false);
        }

        // A request with no audit record is one a run stopped between the two
        // writes. It is attributed now, to whoever asked again.
        var audited = records
            .Select(record => record.Payload)
            .OfType<JournalPayload.Audit>()
            .Where(audit => audit.Action == AuditAction.BulkSnapshotDeletion)
            .SelectMany(audit => audit.Snapshots)
            .Select(snapshot => Convert.ToHexStringLower(snapshot.Span))
            .ToHashSet(StringComparer.Ordinal);
        var unaudited = named
            .Where(snapshot => fresh.Contains(snapshot) || !audited.Contains(snapshot.Fact.SnapshotId))
            .ToList();

        var auditSequence = 0UL;
        if (unaudited.Count > 0)
        {
            using var journal = new JournalPublisher(store, repository.RepositoryId, writerId, repository.Credential, sequence);
            auditSequence = await journal.PublishAsync(
                JournalRecordKind.Audit,
                new JournalPayload.Audit(AuditAction.BulkSnapshotDeletion, actor, (ulong)unaudited.Count)
                {
                    Snapshots = [.. unaudited.Select(snapshot => (ReadOnlyMemory<byte>)snapshot.Manifest.SnapshotId.ToArray())],
                },
                nowUnixMilliseconds,
                SealingGeneration(repository),
                cancellationToken).ConfigureAwait(false);
        }

        return new SnapshotDeletionRequest(
            fresh.Count > 0
                ? generation
                : named.Select(snapshot => standing.GetValueOrDefault(snapshot.ManifestObjectId)).DefaultIfEmpty(0UL).Max(),
            auditSequence,
            [.. fresh.Select(snapshot => snapshot.Fact.SnapshotId)]);
    }

    /// <summary>
    /// Every request in the store that verifies, by the snapshot manifest it
    /// names, with the generation it counts from.
    /// </summary>
    /// <param name="store">The archive's store.</param>
    /// <param name="repository">The opened archive.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The verified requests; empty when there are none.</returns>
    public static async ValueTask<IReadOnlyDictionary<ObjectId, ulong>> ReadRequestsAsync(
        IObjectStore store, OpenedRepository repository, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(repository);

        var requests = new Dictionary<ObjectId, ulong>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse($"tombstones/{(byte)ObjectType.SnapshotManifest:x2}/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            if (await StagingSweep.ReadTombstoneAsync(store, repository, entry.Key, cancellationToken).ConfigureAwait(false)
                    is not { } read
                || read.Tombstone.Value.Reason != TombstoneReason.Requested
                || !Verifies(repository, read.Tombstone, read.Generation))
            {
                continue;
            }

            requests[ObjectId.FromBytes(read.Tombstone.Value.ObjectId.Span)] = read.Tombstone.Value.EligibleGeneration;
        }

        return requests;
    }

    /// <summary>
    /// The writer's highest journal sequence in the store: the clock a
    /// request's grace and generation count in, and what a converge records as
    /// the point it began at.
    /// </summary>
    /// <param name="store">The staging archive's store.</param>
    /// <param name="repository">The opened archive.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async ValueTask<ulong> PublicationSequenceAsync(
        IObjectStore store, OpenedRepository repository, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(repository);

        var records = await LoadJournalAsync(store, repository, cancellationToken).ConfigureAwait(false);
        return records.Count == 0 ? 0 : records.Max(record => record.Sequence);
    }

    /// <summary>
    /// Whether a request's signature verifies without a grant. A repository
    /// written before the reclaim decision signs tombstones with the key it
    /// publishes with. One that declares <c>reclaim-authority</c> signs with
    /// the reclaim key, whose public half the credential carries for
    /// generation zero only.
    /// </summary>
    private static bool Verifies(OpenedRepository repository, DecodedTombstone tombstone, KeyGeneration generation)
    {
        if (!repository.Descriptor.RequiredFeatures.Contains(RepositoryDescriptorCodec.FeatureReclaimAuthority))
        {
            using var signer = RepositorySigner.Create(repository.Credential, generation);
            return signer.Verify(tombstone.SignedBytes.Span, tombstone.Signature.Span);
        }

        return generation.Value == 0
            && !repository.Credential.ReclaimPublicKey.IsEmpty
            && RepositorySigner.VerifyWithPublicKey(
                repository.Credential.ReclaimPublicKey, tombstone.SignedBytes.Span, tombstone.Signature.Span);
    }

    private static async ValueTask WriteRequestAsync(
        IObjectStore store,
        OpenedRepository repository,
        WriterId writerId,
        SurveyedSnapshot snapshot,
        ulong generation,
        ulong nowUnixMilliseconds,
        ReclaimAuthority? reclaim,
        CancellationToken cancellationToken)
    {
        var objectId = snapshot.ManifestObjectId.ToArray();
        var key = StagingSweep.KeyOf((byte)ObjectType.SnapshotManifest, objectId);

        // What stands at the key is an expiry's tombstone, or one that will not
        // verify. Either way the request takes its place: an expiry is a
        // decision the next plan may reverse, and the request is not.
        var removed = await store.DeleteAsync(key, DeleteConditions.None, cancellationToken).ConfigureAwait(false);
        if (removed.Outcome is not (DeleteOutcome.Deleted or DeleteOutcome.NotFound))
        {
            throw new IOException(Strings.FormatSnapshotDeletion_TombstoneWouldNotBeReplaced(
                snapshot.Fact.SnapshotId, removed.Outcome));
        }

        var tombstone = new Tombstone(
            (byte)ObjectType.SnapshotManifest, objectId, TombstoneReason.Requested,
            writerId.ToArray(), nowUnixMilliseconds, generation);
        await StagingSweep.WriteAsync(store, repository, writerId, tombstone, reclaim, cancellationToken)
            .ConfigureAwait(false);

        // Read back, because a put the store refused and one it found already
        // done look alike from here, and only one of them is the request.
        if (await StagingSweep.ReadTombstoneAsync(store, repository, key, cancellationToken).ConfigureAwait(false)
            is not { Tombstone.Value.Reason: TombstoneReason.Requested })
        {
            throw new IOException(Strings.FormatSnapshotDeletion_RequestNotHeld(snapshot.Fact.SnapshotId));
        }
    }

    private static async ValueTask<IReadOnlyList<JournalRecord>> LoadJournalAsync(
        IObjectStore store, OpenedRepository repository, CancellationToken cancellationToken)
    {
        using var journal = new JournalReader(store, repository.RepositoryId, repository.Credential);
        var (records, _, _) = await journal.LoadAsync(SealingGeneration(repository), cancellationToken)
            .ConfigureAwait(false);
        return records;
    }

    private static uint SealingGeneration(OpenedRepository repository) =>
        Math.Max(repository.CurrentDataGeneration.Value, repository.CurrentMetadataGeneration.Value);
}
