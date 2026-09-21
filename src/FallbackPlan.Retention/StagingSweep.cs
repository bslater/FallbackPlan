using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Retention;

/// <summary>What one sweep pass did — and, as loudly, what it refused.</summary>
/// <param name="Deleted">Objects removed after the full 11 §3.2 gate.</param>
/// <param name="NotYetEligible">Tombstones whose grace generation has not arrived.</param>
/// <param name="TombstonesCleared">Tombstones removed one generation after their object went.</param>
/// <param name="Findings">
/// Security, damage and deferral findings: a signature that failed, a
/// tombstone for an object revalidation still reaches, a store that would not
/// release a file. Each one names why nothing was deleted.
/// </param>
/// <remarks>
/// The counters count what the store confirmed. A delete that threw, that
/// reported nothing to delete, or that was refused increments none of them and
/// leaves a finding instead — a report claiming reclaimed space is worth
/// nothing if a failed delete can produce one.
/// </remarks>
public sealed record SweepOutcome(
    int Deleted, int NotYetEligible, int TombstonesCleared, IReadOnlyList<string> Findings);

/// <summary>
/// The destructive half (architecture 07 §3 steps 10–13, deletion-only):
/// tombstone what the plan condemned, wait out the grace in generations,
/// revalidate against a world read after the generation check, and only then
/// delete — in that order, resumable at every step, with the first
/// production caller of <see cref="IObjectStore.DeleteAsync"/> at the end
/// (FR-GC-006, ADR-0012).
/// </summary>
public static class StagingSweep
{
    /// <summary>
    /// Writes one signed tombstone per condemned object (spec 11 §3). The
    /// grace is the next generation: a delete may proceed only after the
    /// repository has visibly advanced past the decision, which no clock can
    /// fake. Idempotent — a tombstone already present is a resume, not a
    /// conflict.
    /// </summary>
    /// <param name="store">The staging archive's store.</param>
    /// <param name="repository">The opened archive.</param>
    /// <param name="writerId">This device's writer identity.</param>
    /// <param name="plan">The pass's plan — must carry no veto.</param>
    /// <param name="survey">The survey the plan was built from, for snapshot identities.</param>
    /// <param name="currentPublicationSequence">
    /// The writer's highest journal sequence in the store now. A per-set
    /// staging archive is single-writer (ADR-0034), so this is the one
    /// per-publication monotonic every participant sees — the grace counts
    /// in it: the delete becomes eligible only after the writer has
    /// visibly published past the decision (spec 11 §3.1's posture,
    /// realised in the sequence space until multi-writer archives exist).
    /// </param>
    /// <param name="nowUnixMilliseconds">Informational stamp only (11 §3.1).</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <param name="reclaim">
    /// The run's authority to author deletions (ADR-0055 §6). A repository
    /// declaring <c>reclaim-authority</c> cannot derive it and will throw
    /// without one, which is the intended refusal: a service that cannot be
    /// granted the authority must not quietly fall back to the key it
    /// publishes with. Null only for a repository written before the feature,
    /// whose tombstones still sign under the signing key.
    /// </param>
    /// <returns>Tombstones written (or already present).</returns>
    public static async ValueTask<int> TombstoneAsync(
        IObjectStore store,
        OpenedRepository repository,
        WriterId writerId,
        CollectionPlan plan,
        SnapshotSurvey survey,
        ulong currentPublicationSequence,
        ulong nowUnixMilliseconds,
        CancellationToken cancellationToken,
        ReclaimAuthority? reclaim = null)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(repository);
        ThrowHelper.ThrowIfNull(plan);
        ThrowHelper.ThrowIfNull(survey);

        if (!plan.Deletable)
        {
            // A vetoed plan tombstones nothing: the vetoes say the object
            // graph cannot be trusted to name garbage.
            throw new InvalidOperationException("A vetoed collection plan authorises no tombstones.");
        }

        var eligible = currentPublicationSequence + 1;
        var written = 0;

        foreach (var blob in plan.DeletableBlobs)
        {
            var tombstone = new Tombstone(
                Tombstone.BlobTypeCode, blob.BlobId.ToArray(), blob.Reason,
                writerId.ToArray(), nowUnixMilliseconds, eligible);
            written += await WriteAsync(store, repository, writerId, tombstone, reclaim, cancellationToken)
                .ConfigureAwait(false);
        }

        var expiredKeys = plan.ExpiredSnapshotKeys.ToHashSet();
        foreach (var snapshot in survey.Snapshots.Where(candidate => expiredKeys.Contains(candidate.StoreKey)))
        {
            var tombstone = new Tombstone(
                (byte)ObjectType.SnapshotManifest, snapshot.ManifestObjectId.ToArray(),
                TombstoneReason.Unreferenced, writerId.ToArray(), nowUnixMilliseconds, eligible);
            written += await WriteAsync(store, repository, writerId, tombstone, reclaim, cancellationToken)
                .ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>
    /// The delete gate, run per tombstone (spec 11 §3.2): the signature
    /// verifies, the generation has arrived, and the object is revalidated as
    /// still condemned against the fresh plan the caller computed <b>after</b>
    /// this pass began. Deletes in bounded order; every refusal is a finding,
    /// including one the store itself raises — a single object the platform
    /// will not release defers that object and nothing else, because the walk
    /// is in key order and abandoning it would abandon everything sorting
    /// after it too.
    /// </summary>
    /// <param name="store">The staging archive's store.</param>
    /// <param name="repository">The opened archive.</param>
    /// <param name="freshPlan">A plan recomputed now — the revalidation world.</param>
    /// <param name="freshSurvey">The survey that fresh plan was built from.</param>
    /// <param name="currentPublicationSequence">The writer's highest journal sequence now — the grace clock.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <param name="reclaim">
    /// The run's authority to author deletions (ADR-0055 §6). A repository
    /// declaring <c>reclaim-authority</c> cannot derive it and will throw
    /// without one, which is the intended refusal: a service that cannot be
    /// granted the authority must not quietly fall back to the key it
    /// publishes with. Null only for a repository written before the feature,
    /// whose tombstones still sign under the signing key.
    /// </param>
    /// <returns>What was deleted, deferred, cleared and found.</returns>
    public static async ValueTask<SweepOutcome> SweepAsync(
        IObjectStore store,
        OpenedRepository repository,
        CollectionPlan freshPlan,
        SnapshotSurvey freshSurvey,
        ulong currentPublicationSequence,
        CancellationToken cancellationToken,
        ReclaimAuthority? reclaim = null)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(repository);
        ThrowHelper.ThrowIfNull(freshPlan);
        ThrowHelper.ThrowIfNull(freshSurvey);

        var deriver = new StoreBlobKeyDeriver(repository.Credential.KeyIdKey.ToArray());

        var condemnedBlobs = freshPlan.DeletableBlobs.Select(blob => blob.BlobId).ToHashSet();
        var condemnedSnapshots = freshPlan.ExpiredSnapshotKeys.ToHashSet();
        var snapshotsByObjectId = freshSurvey.Snapshots.ToDictionary(
            snapshot => snapshot.ManifestObjectId, snapshot => snapshot);

        var deleted = 0;
        var notYet = 0;
        var cleared = 0;
        var findings = new List<string>();

        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("tombstones/"), ListOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            var opened = await OpenTombstoneAsync(store, repository, entry.Key, cancellationToken, reclaim).ConfigureAwait(false);
            if (opened is not { } tombstone)
            {
                findings.Add($"security: tombstone {entry.Key} would not open or verify — nothing deleted for it");
                continue;
            }

            if (currentPublicationSequence < tombstone.Value.EligibleGeneration)
            {
                notYet++;
                continue;
            }

            if (tombstone.Value.ObjectTypeCode == Tombstone.BlobTypeCode)
            {
                var blobId = BlobId.FromBytes(tombstone.Value.ObjectId.Span);
                var dataKey = BlobStoreKeys.ForBlob(BlobClass.Data, deriver.Derive(blobId));
                var metaKey = BlobStoreKeys.ForBlob(BlobClass.Metadata, deriver.Derive(blobId));

                ObjectKey? present = await ExistsAsync(store, dataKey, cancellationToken).ConfigureAwait(false)
                    ? dataKey
                    : await ExistsAsync(store, metaKey, cancellationToken).ConfigureAwait(false)
                        ? metaKey
                        : null;

                if (present is null)
                {
                    // The object is gone; the tombstone outlives it one
                    // generation so a reader can tell a completed collection
                    // from a missing object (11 §3.2).
                    if (currentPublicationSequence >= tombstone.Value.EligibleGeneration + 1
                        && await TryDeleteAsync(
                            store, entry.Key, $"tombstone {entry.Key}", findings, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        cleared++;
                    }

                    continue;
                }

                if (!freshPlan.Deletable || !condemnedBlobs.Contains(blobId))
                {
                    // Revalidation disagrees with the tombstone: a snapshot
                    // published since the decision reaches this blob, or the
                    // fresh world is vetoed. Damage finding; never delete
                    // (11 §3.2 step 3).
                    findings.Add($"damage: tombstoned blob {blobId} is still reachable — not deleted");
                    continue;
                }

                if (await TryDeleteAsync(store, present.Value, $"blob {blobId}", findings, cancellationToken)
                    .ConfigureAwait(false))
                {
                    deleted++;
                }

                continue;
            }

            if (tombstone.Value.ObjectTypeCode == (byte)ObjectType.SnapshotManifest)
            {
                var objectId = ObjectId.FromBytes(tombstone.Value.ObjectId.Span);
                if (!snapshotsByObjectId.TryGetValue(objectId, out var snapshot))
                {
                    if (currentPublicationSequence >= tombstone.Value.EligibleGeneration + 1
                        && await TryDeleteAsync(
                            store, entry.Key, $"tombstone {entry.Key}", findings, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        cleared++;
                    }

                    continue;
                }

                if (!freshPlan.Deletable || !condemnedSnapshots.Contains(snapshot.StoreKey))
                {
                    findings.Add(
                        $"damage: tombstoned snapshot {snapshot.Fact.SnapshotId[..12]}… is protected again — not deleted");
                    continue;
                }

                if (await TryDeleteAsync(
                    store, snapshot.StoreKey, $"snapshot {snapshot.Fact.SnapshotId[..12]}…", findings, cancellationToken)
                    .ConfigureAwait(false))
                {
                    deleted++;
                }

                continue;
            }

            findings.Add($"damage: tombstone {entry.Key} names an object type this pass does not collect");
        }

        return new SweepOutcome(deleted, notYet, cleared, findings);
    }

    /// <summary>
    /// Deletes one object, reporting rather than propagating whatever stops
    /// it, and returning true only when the store says the object went.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two failures hide here and neither is exotic. A store that throws —
    /// Windows holding a sharing violation over a blob something is reading,
    /// a permission changed under a running collector — would otherwise
    /// abandon every object sorting after this one in the listing, and take
    /// the pass's counters and findings with it. And a store answering
    /// <see cref="DeleteOutcome.NotFound"/> or
    /// <see cref="DeleteOutcome.PreconditionFailed"/> has not deleted
    /// anything, so counting the call as a deletion produces a report that
    /// claims space nobody reclaimed.
    /// </para>
    /// <para>
    /// A refusal is a deferral, never a withdrawal: the tombstone stands, its
    /// grace clock keeps running, and the next pass re-plans and tries again.
    /// Cancellation is deliberately <em>not</em> absorbed — a stop request
    /// must not read as a completed pass.
    /// </para>
    /// </remarks>
    private static async ValueTask<bool> TryDeleteAsync(
        IObjectStore store,
        ObjectKey key,
        string what,
        List<string> findings,
        CancellationToken cancellationToken)
    {
        DeleteResult result;
        try
        {
            result = await store.DeleteAsync(key, DeleteConditions.None, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Any fault type, not a list of today's: IObjectStore leaves fault
            // exceptions implementation-defined, and a catch sized to the
            // local filesystem would let the first remote store's timeout
            // escape the loop and abort the pass. A refusal is always
            // deferrable — the tombstone stands — so deferring is safe for
            // every fault, while cancellation must keep escaping.
            findings.Add(
                $"deferred: {what} could not be deleted — {exception.Message}; the tombstone stands for the next pass");
            return false;
        }

        switch (result.Outcome)
        {
            case DeleteOutcome.Deleted:
                return true;

            case DeleteOutcome.NotFound:
                // Already gone is the goal, and a concurrent pass or an
                // interrupted earlier one reaches here honestly. Nothing was
                // deleted by this call, so nothing is counted.
                return false;

            default:
                findings.Add($"deferred: {what} was refused by the store ({result.Outcome}); not deleted");
                return false;
        }
    }

    private static async ValueTask<bool> ExistsAsync(
        IObjectStore store, ObjectKey key, CancellationToken cancellationToken)
    {
        var metadata = await store.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        return metadata.Metadata is not null;
    }

    private static ulong SealingGeneration(OpenedRepository repository) =>
        Math.Max(repository.CurrentDataGeneration.Value, repository.CurrentMetadataGeneration.Value);

    /// <summary>
    /// Whether a grant is this repository's, proved against a tombstone it
    /// already holds ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no stored public key to check a granted seed against, so the
    /// proof is by use. A tombstone already on disk was signed under the real
    /// reclaim key; a grant that verifies it is the real one. A repository
    /// holding none yet has nothing to disagree with, and answers true — the
    /// first tombstone it writes is what every later grant is measured
    /// against.
    /// </para>
    /// <para>
    /// Checked before the run authors anything, because the alternative
    /// failure is silent and late: a wrong grant would write tombstones
    /// nothing can verify, and the next sweep would report them as forgeries —
    /// an alarm about an attack that never happened, raised at whoever reads
    /// the notices rather than at whoever sent the wrong envelope.
    /// </para>
    /// </remarks>
    /// <param name="store">The repository's store.</param>
    /// <param name="repository">The opened repository.</param>
    /// <param name="reclaim">The grant to prove.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async ValueTask<bool> GrantProvesOutAsync(
        IObjectStore store, OpenedRepository repository, ReclaimAuthority reclaim,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(repository);
        ThrowHelper.ThrowIfNull(reclaim);

        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("tombstones/"), ListOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            var opened = await OpenTombstoneAsync(
                store, repository, entry.Key, cancellationToken, reclaim, verify: false).ConfigureAwait(false);
            if (opened is null)
            {
                // Unreadable for some other reason — damage, a key generation
                // this service cannot open. Not this grant's fault, so keep
                // looking rather than condemning it.
                continue;
            }

            return reclaim.Verifies(
                opened.SignedBytes.Span, opened.Signature.Span,
                new KeyGeneration((uint)SealingGeneration(repository)));
        }

        return true;
    }

    /// <summary>
    /// The key a tombstone signs and verifies under
    /// ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §1, §4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tombstone's signature is the <em>authorisation</em> to delete
    /// (specification 11 §3), which is a different claim from a publication's
    /// signature and now carries a different key. A repository that declares
    /// <c>reclaim-authority</c> signs here with the reclaim key; one that does
    /// not keeps the signing key, and that branch is the compatibility rule the
    /// whole migration rests on — every repository written before this decision
    /// has tombstones on disk that must still verify.
    /// </para>
    /// <para>
    /// The descriptor decides, never the tombstone. A per-object discriminator
    /// would let whoever writes the object choose the weaker key.
    /// </para>
    /// </remarks>
    private static RepositorySigner TombstoneSigner(
        OpenedRepository repository, KeyGeneration generation, ReclaimAuthority? granted)
    {
        if (!repository.Descriptor.RequiredFeatures.Contains(
            RepositoryDescriptorCodec.FeatureReclaimAuthority))
        {
            return RepositorySigner.Create(repository.Credential, generation);
        }

        // The grant is the only source of this key: a repository's credential
        // cannot derive it at all (ADR-0055 §2, §6). A collection reaching
        // here without one is a caller bug — the handler refuses an apply
        // without a grant by name before a sweep starts — and is refused
        // again here rather than allowed to sign under the wrong key.
        if (granted is null)
        {
            throw new InvalidOperationException(
                "A tombstone needs the reclaim authority, which only a grant supplies for this run (ADR-0055 §6).");
        }

        var seed = granted.SeedFor(generation);
        try
        {
            return RepositorySigner.FromSeed(seed, generation);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(seed);
        }
    }

    private static async ValueTask<int> WriteAsync(
        IObjectStore store,
        OpenedRepository repository,
        WriterId writerId,
        Tombstone tombstone,
        ReclaimAuthority? reclaim,
        CancellationToken cancellationToken)
    {
        // The SEALING generation stays the key generation — that is what
        // derives the metadata and signing keys. Only the grace arithmetic
        // lives in the writer's sequence space.
        var keyGeneration = new KeyGeneration((uint)SealingGeneration(repository));
        byte[] encoded;
        using (var signer = TombstoneSigner(repository, keyGeneration, reclaim))
        {
            encoded = TombstoneCodec.Encode(
                tombstone, signer.Sign(TombstoneCodec.EncodeForSigning(tombstone)));
        }

        var contentIdKey = repository.Credential.ContentIdKey.ToArray();
        var objectId = new ObjectIdDeriver(contentIdKey).Derive(
            ObjectType.Tombstone, ContentHasher.Hash(encoded));

        var metadataKey = repository.Credential.DeriveMetadataKey(keyGeneration);
        byte[] sealedObject;
        try
        {
            sealedObject = StandaloneRecordCipher.Seal(
                repository.RepositoryId, metadataKey, keyGeneration, writerId,
                counter: 0, ObjectType.Tombstone, objectId, encoded);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(metadataKey);
        }

        var key = ObjectKey.Parse(
            $"tombstones/{tombstone.ObjectTypeCode:x2}/{Base32.Encode(tombstone.ObjectId.ToArray())}");
        var put = await store.PutAsync(
            key,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(sealedObject, writable: false)),
            PutConditions.IfNotExists,
            cancellationToken).ConfigureAwait(false);

        // Already present is a resume: the earlier pass's decision stands and
        // its grace clock, already running, is the honest one.
        return put.Outcome is PutOutcome.Created or PutOutcome.PreconditionFailed ? 1 : 0;
    }

    private static async ValueTask<DecodedTombstone?> OpenTombstoneAsync(
        IObjectStore store, OpenedRepository repository, ObjectKey key, CancellationToken cancellationToken,
        ReclaimAuthority? reclaim = null, bool verify = true)
    {
        byte[] bytes;
        using (var read = await store.OpenReadAsync(key, range: null, cancellationToken).ConfigureAwait(false))
        {
            if (read.Outcome != OpenReadOutcome.Found)
            {
                return null;
            }

            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            bytes = memory.ToArray();
        }

        try
        {
            var record = StandaloneRecordFraming.Parse(bytes);
            var metadataKey = repository.Credential.DeriveMetadataKey(record.KeyGeneration);
            try
            {
                if (!StandaloneRecordCipher.TryOpen(record, repository.RepositoryId, metadataKey, out var plaintext))
                {
                    return null;
                }

                var decoded = TombstoneCodec.Decode(plaintext);

                // The signature is the authorisation (11 §3): verified against
                // the signing key for the generation the record was sealed
                // under, and a failure is a security finding the caller
                // reports — an unsigned tombstone is an attempt to have
                // someone else delete data.
                if (!verify)
                {
                    // The grant proof reads a tombstone to check the grant
                    // AGAINST it, so it must not first ask the grant to
                    // authorise the read — that would answer its own question.
                    return decoded;
                }

                using var signer = TombstoneSigner(repository, record.KeyGeneration, reclaim);
                return signer.Verify(decoded.SignedBytes.Span, decoded.Signature.Span) ? decoded : null;
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(metadataKey);
            }
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
