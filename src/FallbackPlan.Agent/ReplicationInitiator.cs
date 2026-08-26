using Bodu;
using FallbackPlan.Protocol;
using FallbackPlan.Replication;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Agent;

/// <summary>
/// The source half of replication (specification peer-protocol 03): over an open
/// peer session, offer a repository, learn what the destination already holds,
/// and stream the objects it lacks. It reads raw objects and never decrypts one
/// — replication forwards ciphertext (03 §8).
/// </summary>
internal static class ReplicationInitiator
{
    /// <summary>The repository format capability this build speaks (03 §3.1).</summary>
    public const uint FormatCapability = 1;

    /// <summary>Pushes every object the destination lacks, and returns how many it committed.</summary>
    /// <param name="source">The raw object store to read from.</param>
    /// <param name="repositoryId">The repository the objects belong to (16 bytes).</param>
    /// <param name="stream">The open session stream.</param>
    /// <param name="cancellationToken">Cancels the push.</param>
    /// <returns>The count the destination acknowledged committing.</returns>
    public static async Task<long> PushAllAsync(
        IObjectStore source, ReadOnlyMemory<byte> repositoryId, Stream stream, CancellationToken cancellationToken)
    {
        var outcome = await PushAndConvergeAsync(
            source, repositoryId, stream, keeps: null, armClaim: null, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Committed;
    }

    /// <summary>What one push-and-converge session moved and removed.</summary>
    /// <param name="Committed">Objects the spoke acknowledged committing.</param>
    /// <param name="Deleted">Objects the spoke acknowledged deleting under the retention instruction.</param>
    /// <param name="HeldAtStart">
    /// How many keys the spoke declared holding before the push — its own
    /// account of itself, and the only evidence a source gets that a
    /// destination has quietly lost what it was given. A spoke that answers
    /// zero here after a recorded success has been emptied.
    /// </param>
    /// <param name="Headroom">
    /// Bytes the spoke could still accept under its quota when the inventory
    /// was taken, or null when no quota bounds it — or when the spoke speaks
    /// a build that does not say. Null therefore means "not told", never "no
    /// room": the two must not be spelled the same.
    /// </param>
    public sealed record PushOutcome(long Committed, long Deleted, long HeldAtStart, ulong? Headroom = null);

    /// <summary>
    /// Pushes the objects the destination lacks and the policy keeps, then —
    /// when a keep filter is given — instructs the spoke to drop what its
    /// policy no longer keeps (peer-protocol 06). The drop-list is computed
    /// from the spoke's own inventory, so an instruction can only name keys
    /// the spoke itself declared, and it is ordered snapshots-first so an
    /// interruption leaves the replica lagging-but-valid.
    /// </summary>
    /// <param name="source">The staging archive's store.</param>
    /// <param name="repositoryId">The repository the objects belong to (16 bytes).</param>
    /// <param name="stream">The open session stream.</param>
    /// <param name="keeps">The destination's keep filter, or null to push whole and instruct nothing.</param>
    /// <param name="armClaim">
    /// Given the destination's freshly minted claim token, the 32-byte public
    /// half of the credential its passphrase reproduces (03 §3.2.1) — or null
    /// to decline. Declining is a first-class answer: a source that cannot
    /// reach the passphrase-derived root must send nothing rather than
    /// register something derived from other material.
    /// </param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>What moved and what went.</returns>
    public static async Task<PushOutcome> PushAndConvergeAsync(
        IObjectStore source, ReadOnlyMemory<byte> repositoryId, Stream stream,
        Func<string, bool>? keeps,
        Func<ReadOnlyMemory<byte>, byte[]?>? armClaim,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(stream);

        try
        {
            await PeerFrame.WriteAsync(
                stream, new ReplicationOffer(repositoryId, FormatCapability, "all"), cancellationToken)
                .ConfigureAwait(false);

            var (held, headroom, claimToken) = await ReadInventoryAsync(stream, cancellationToken)
                .ConfigureAwait(false);

            // A token offered means the destination holds no claim credential
            // for this repository yet, and is asking for one before the first
            // object crosses (03 §3.2.1). It is offered at most once per
            // replica, so the Argon2id pass this costs is paid once per
            // destination — not once per sync.
            if (claimToken is { } token && armClaim is not null && armClaim(token) is { } claimPublicKey)
            {
                await PeerFrame.WriteAsync(
                    stream, new ClaimRegister(repositoryId, claimPublicKey), cancellationToken)
                    .ConfigureAwait(false);
            }

            var sent = 0L;
            await foreach (var entry in source.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
                .ConfigureAwait(false))
            {
                if (held.Contains(entry.Key.Value)
                    || IsStagingOnly(entry.Key.Value)
                    || (keeps is not null && !keeps(entry.Key.Value)))
                {
                    continue;
                }

                await SendObjectAsync(source, entry, stream, cancellationToken).ConfigureAwait(false);
                sent++;
            }

            await PeerFrame.WriteAsync(stream, new ReplicationComplete((ulong)sent), cancellationToken)
                .ConfigureAwait(false);

            var ack = await ReplicationWire.ReadAsync(
                stream, PeerMessageType.ReplicationAck, ReplicationAck.Read, cancellationToken).ConfigureAwait(false);

            // The two counts must agree. A spoke commits each object whole or
            // refuses (03 §5) — a quota trip throws rather than under-counting
            // — so a spoke that acknowledges fewer than it was sent, without
            // refusing, is either buggy or the stream has desynchronised, and
            // in both cases the objects we believe are there may not be. This
            // is a hard fault, not a soft warning: a warning here would be
            // recorded beside a successful sync and learned to be ignored,
            // while the ledger went on claiming a coverage nobody has.
            if ((long)ack.Count < sent)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed,
                    $"The destination acknowledged committing {ack.Count} object(s) of the {sent} sent.");
            }

            if (keeps is null)
            {
                return new PushOutcome((long)ack.Count, 0, held.Count, headroom);
            }

            // The drop half (06 §2): inventory minus keep-closure, snapshots
            // before anything they reference — and only keys STAGING STILL
            // LISTS. A key the spoke holds that staging no longer does is a
            // trimmed object whose only remaining home may be that spoke;
            // a filter computed from staging cannot vouch for it, and the
            // unknown is kept (ADR-0034 §6). Staging is listed afresh HERE,
            // after the push: an hours-long exchange must not condemn on the
            // strength of a stale opening walk (ADR-0029 Amendment 2).
            var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var entry in source.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
                .ConfigureAwait(false))
            {
                sourceKeys.Add(entry.Key.Value);
            }

            var drops = held
                .Where(key => sourceKeys.Contains(key) && (!keeps(key) || IsStagingOnly(key)))
                .OrderBy(DropRank)
                .ThenBy(key => key, StringComparer.Ordinal)
                .ToList();
            if (drops.Count == 0)
            {
                return new PushOutcome((long)ack.Count, 0, held.Count, headroom);
            }

            for (var offset = 0; offset < drops.Count; offset += RetentionOffer.MaximumKeys)
            {
                var page = drops.Skip(offset).Take(RetentionOffer.MaximumKeys).ToList();
                await PeerFrame.WriteAsync(
                    stream,
                    new RetentionOffer(repositoryId, page, More: offset + RetentionOffer.MaximumKeys < drops.Count),
                    cancellationToken).ConfigureAwait(false);
            }

            var retentionAck = await ReplicationWire.ReadAsync(
                stream, PeerMessageType.RetentionAck, RetentionAck.Read, cancellationToken).ConfigureAwait(false);
            return new PushOutcome((long)ack.Count, (long)retentionAck.Deleted, held.Count, headroom);
        }
        catch (PeerProtocolException exception)
        {
            if (!exception.ReceivedFromPeer)
            {
                await ReplicationWire.TryRefuseAsync(stream, exception).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Issues keyed random-range challenges after the exchange (peer-protocol
    /// 04): for each sample, the expected proof is computed from this side's
    /// own bytes and compared against the destination's answer. A wrong or
    /// missing proof is a FINDING for the caller to record, never a protocol
    /// error — the session continues to the next sample.
    /// </summary>
    /// <param name="source">The staging archive's store — the ground truth.</param>
    /// <param name="repositoryId">The repository the keys belong to (16 bytes).</param>
    /// <param name="stream">The open session stream, post-acknowledgement.</param>
    /// <param name="samples">What to challenge.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What was proven and what was not.</returns>
    public static async Task<VerificationOutcome> ChallengeAsync(
        IObjectStore source, ReadOnlyMemory<byte> repositoryId, Stream stream,
        IReadOnlyList<VerificationSample> samples, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(stream);
        ThrowHelper.ThrowIfNull(samples);

        try
        {
            var passed = 0;
            var failed = new List<string>();
            foreach (var sample in samples)
            {
                var expected = await RangeReader.ReadAsync(source, sample, cancellationToken)
                    .ConfigureAwait(false);
                if (expected is null)
                {
                    // Staging itself could not read the range — there is no
                    // ground truth to compare against, so the sample proves
                    // nothing either way.
                    continue;
                }

                var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(
                    VerificationChallenge.NonceLength);
                var challengeKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(
                    VerificationChallenge.ChallengeKeyLength);

                await PeerFrame.WriteAsync(
                    stream,
                    new VerificationChallenge(
                        repositoryId, sample.Key, sample.Offset, sample.Length, nonce, challengeKey),
                    cancellationToken).ConfigureAwait(false);

                var proof = await ReplicationWire.ReadAsync(
                    stream, PeerMessageType.VerificationProof, VerificationProof.Read, cancellationToken)
                    .ConfigureAwait(false);

                var want = RangeChallenge.Compute(
                    challengeKey, nonce, sample.Key, sample.Offset, sample.Length, expected);
                if (proof.Held
                    && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(proof.Proof.Span, want))
                {
                    passed++;
                }
                else
                {
                    failed.Add(sample.Key);
                }
            }

            return new VerificationOutcome(passed, failed);
        }
        catch (PeerProtocolException exception)
        {
            if (!exception.ReceivedFromPeer)
            {
                await ReplicationWire.TryRefuseAsync(stream, exception).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Lifecycle objects never replicate: destinations are converged, never collected.</summary>
    private static bool IsStagingOnly(string key) =>
        key.StartsWith("tombstones/", StringComparison.Ordinal)
        || key.StartsWith("leases/", StringComparison.Ordinal);

    /// <summary>Reverse dependency order for deletion: snapshots first, blobs last.</summary>
    private static int DropRank(string key) =>
        key.StartsWith("snapshots/", StringComparison.Ordinal) ? 0
        : key.StartsWith("index/checkpoint/", StringComparison.Ordinal) ? 2
        : key.StartsWith("index/delta/", StringComparison.Ordinal) ? 3
        : key.StartsWith("journal/", StringComparison.Ordinal) ? 4
        : key.StartsWith("blobs/", StringComparison.Ordinal) ? 5
        : 1;

    private static async Task<(HashSet<string> Held, ulong? Headroom, ReadOnlyMemory<byte>? ClaimToken)>
        ReadInventoryAsync(Stream stream, CancellationToken cancellationToken)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        ulong? headroom = null;
        ReadOnlyMemory<byte>? claimToken = null;
        while (true)
        {
            var page = await ReplicationWire.ReadAsync(
                stream, PeerMessageType.ReplicationInventory, ReplicationInventory.Read, cancellationToken)
                .ConfigureAwait(false);

            foreach (var objectKey in page.Keys)
            {
                held.Add(objectKey);
            }

            // Every page carries it, and a destination that speaks an older
            // build carries it on none — so the last one seen wins and null
            // means "not told", never "no room".
            headroom = page.Headroom ?? headroom;

            // The claim token rides the final page alone (03 §3.2.1), so the
            // last one seen is the only one there ever was.
            claimToken = page.ClaimToken ?? claimToken;

            if (!page.More)
            {
                return (held, headroom, claimToken);
            }
        }
    }

    private static async Task SendObjectAsync(
        IObjectStore source, ObjectEntry entry, Stream stream, CancellationToken cancellationToken)
    {
        using var read = await source.OpenReadAsync(entry.Key, range: null, cancellationToken).ConfigureAwait(false);
        if (read.Outcome != OpenReadOutcome.Found || read.Content is null)
        {
            // Immutable objects are never deleted in this build, so a key that
            // just listed must still read; treating the impossible as a fault is
            // honest rather than silently shipping a short object.
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"Object {entry.Key.Value} listed but could not be read to send.");
        }

        await PeerFrame.WriteAsync(
            stream, new ReplicationObject(entry.Key.Value, (ulong)entry.Length), cancellationToken)
            .ConfigureAwait(false);

        var buffer = new byte[ReplicationChunk.MaximumBytes];
        var offset = 0UL;
        int got;
        while ((got = await read.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await PeerFrame.WriteAsync(
                stream, new ReplicationChunk(offset, buffer.AsMemory(0, got)), cancellationToken).ConfigureAwait(false);
            offset += (ulong)got;
        }
    }
}
