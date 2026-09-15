using System.Security.Cryptography;
using Bodu;
using FallbackPlan.Protocol;
using FallbackPlan.Replication;
using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
        var outcome = await PushAndConvergeAsync(source, repositoryId, stream, keeps: null, cancellationToken)
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
    /// <param name="BytesSent">
    /// Object bytes this push put on the wire. The count a resumed transfer
    /// exists to reduce, and the only number that can tell "the object crossed
    /// again" from "its tail crossed" — the object counts cannot, because both
    /// commit exactly one object ([ADR-0057](../../docs/adr/0057-resumable-object-transfer.md)).
    /// </param>
    /// <param name="ResumedObjects">How many objects began at a non-zero offset.</param>
    /// <param name="HeldKeys">
    /// The keys the spoke declared holding, when the caller asked for them —
    /// the same account <paramref name="HeldAtStart"/> counts, kept rather
    /// than discarded so a verifier can draw a sample from it.
    /// </param>
    /// <remarks>
    /// Sampling from the spoke's own declaration sounds like letting the
    /// examined party choose the questions, and is not: a key it omits to
    /// avoid being asked about is a key this same session re-ships, because
    /// the declaration is also the push's diff. Hiding a loss repairs it.
    /// </remarks>
    public sealed record PushOutcome(
        long Committed,
        long Deleted,
        long HeldAtStart,
        ulong? Headroom = null,
        long BytesSent = 0,
        long ResumedObjects = 0,
        IReadOnlyCollection<string>? HeldKeys = null);

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
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <param name="reclaimPublicKey">
    /// The repository's reclaim public key
    /// ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §5), published on
    /// the offer so the destination can record it at first attribution; empty
    /// when this source has none. Public by nature and authorising nothing —
    /// what it buys is a keyless destination that can tell a real deletion
    /// instruction from a forged one.
    /// </param>
    /// <param name="signer">
    /// Signs each retention page's canonical bytes under the repository's
    /// reclaim key (ADR-0055 §5), or null when this commander holds none —
    /// a write-only set without a grant, or a build that publishes no key.
    /// </param>
    /// <param name="sessionBinding">
    /// This session's identifier (02 §3.5), covered by each retention
    /// signature so that a recording of the exchange cannot be replayed into a
    /// later one. Empty signs the older unbound encoding, which is what a
    /// spoke too old to verify the bound one expects — the caller decides from
    /// the negotiated features, because that is a question about what the
    /// other side can understand.
    /// </param>
    /// <param name="resumeNegotiated">
    /// Whether the session's features admit a transfer beginning part-way
    /// through an object (03 §5; [ADR-0057](../../docs/adr/0057-resumable-object-transfer.md)).
    /// Without it the destination declares nothing and every object starts at
    /// zero, which is what every build before this one did.
    /// </param>
    /// <param name="logger">Where a resumed — or refused — prefix is reported.</param>
    /// <returns>What moved and what went.</returns>
    public static async Task<PushOutcome> PushAndConvergeAsync(
        IObjectStore source, ReadOnlyMemory<byte> repositoryId, Stream stream,
        Func<string, bool>? keeps, CancellationToken cancellationToken,
        ReadOnlyMemory<byte> reclaimPublicKey = default,
        Func<byte[], byte[]>? signer = null,
        bool resumeNegotiated = false,
        ILogger? logger = null,
        ReadOnlyMemory<byte> sessionBinding = default)
    {
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(stream);

        var log = logger ?? NullLogger.Instance;

        try
        {
            await PeerFrame.WriteAsync(
                stream,
                new ReplicationOffer(repositoryId, FormatCapability, "all", reclaimPublicKey),
                cancellationToken)
                .ConfigureAwait(false);

            var (held, headroom) = await ReadInventoryAsync(stream, cancellationToken).ConfigureAwait(false);

            // What the destination part holds, when both sides agreed a cut
            // object may be finished rather than started again (ADR-0057). A
            // claim, not an instruction: each one is checked against this
            // source's own bytes before anything is skipped.
            var partials = resumeNegotiated
                ? await ReadPartialsAsync(stream, cancellationToken).ConfigureAwait(false)
                : [];

            var sent = 0L;
            var bytesSent = 0L;
            var resumed = 0L;
            await foreach (var entry in source.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
                .ConfigureAwait(false))
            {
                if (held.Contains(entry.Key.Value)
                    || IsStagingOnly(entry.Key.Value)
                    || (keeps is not null && !keeps(entry.Key.Value)))
                {
                    continue;
                }

                var from = await SendObjectAsync(
                    source, entry, stream, partials, log, cancellationToken).ConfigureAwait(false);
                sent++;
                bytesSent += entry.Length - (long)from;
                if (from > 0)
                {
                    resumed++;
                }
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
                return new PushOutcome((long)ack.Count, 0, held.Count, headroom, bytesSent, resumed, held);
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
                return new PushOutcome((long)ack.Count, 0, held.Count, headroom, bytesSent, resumed, held);
            }

            for (var offset = 0; offset < drops.Count; offset += RetentionOffer.MaximumKeys)
            {
                var page = drops.Skip(offset).Take(RetentionOffer.MaximumKeys).ToList();
                var instruction = new RetentionOffer(
                    repositoryId, page, More: offset + RetentionOffer.MaximumKeys < drops.Count);

                // Signed under the reclaim key (ADR-0055 §5), so a spoke can
                // tell an instruction authorised by whoever holds that key
                // from one sent by whoever merely holds this session. A
                // commander with no signer makes none, and a spoke that
                // recorded this repository's reclaim key refuses such a page
                // rather than acting on it — whatever the session negotiated,
                // because a check the sender can opt out of is not a check.
                if (signer is not null)
                {
                    instruction = instruction with
                    {
                        Signature = signer(instruction.EncodeForSigning(sessionBinding.Span)),
                    };
                }

                await PeerFrame.WriteAsync(stream, instruction, cancellationToken).ConfigureAwait(false);
            }

            var retentionAck = await ReplicationWire.ReadAsync(
                stream, PeerMessageType.RetentionAck, RetentionAck.Read, cancellationToken).ConfigureAwait(false);
            return new PushOutcome(
                (long)ack.Count, (long)retentionAck.Deleted, held.Count, headroom, bytesSent, resumed, held);
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

    /// <summary>Reads the destination's partial declaration (03 §3.4).</summary>
    /// <param name="stream">The session stream.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Staged length and prefix digest per object key.</returns>
    private static async Task<Dictionary<string, (ulong Staged, ReadOnlyMemory<byte> Digest)>> ReadPartialsAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        var declaration = await ReplicationWire.ReadAsync(
            stream, PeerMessageType.ReplicationPartial, ReplicationPartial.Read, cancellationToken)
            .ConfigureAwait(false);

        var partials = new Dictionary<string, (ulong, ReadOnlyMemory<byte>)>(StringComparer.Ordinal);
        for (var index = 0; index < declaration.Keys.Count; index++)
        {
            // Last wins rather than refusing a repeated key: a duplicate is a
            // peer being untidy, and both entries get checked against the same
            // bytes anyway.
            partials[declaration.Keys[index]] = (declaration.StagedLengths[index], declaration.Digests[index]);
        }

        return partials;
    }

    /// <summary>
    /// Where this object's transfer may begin: the destination's claim, once
    /// this source has checked it against its own bytes.
    /// </summary>
    /// <remarks>
    /// The check is the decision (ADR-0057). The destination cannot verify what
    /// it staged — it holds no repository keys and the store key is a keyed
    /// rendering of an identifier rather than of the bytes — so the only side
    /// that can tell a good prefix from a rotted one is the side that has the
    /// object. A mismatch is not a refusal: the transfer simply starts at zero,
    /// which is what it would have done anyway.
    /// </remarks>
    /// <param name="source">The store holding the object.</param>
    /// <param name="entry">Its listing entry.</param>
    /// <param name="partials">What the destination declared.</param>
    /// <param name="log">Where the decision is reported.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    private static async Task<ulong> ResumePointAsync(
        IObjectStore source,
        ObjectEntry entry,
        IReadOnlyDictionary<string, (ulong Staged, ReadOnlyMemory<byte> Digest)> partials,
        ILogger log,
        CancellationToken cancellationToken)
    {
        if (!partials.TryGetValue(entry.Key.Value, out var claim)
            || claim.Staged == 0
            || claim.Staged >= (ulong)entry.Length)
        {
            return 0;
        }

        // Below one chunk there is nothing worth the hash: the saving is
        // bounded by what a single frame would have carried anyway.
        if (claim.Staged < ReplicationChunk.MaximumBytes)
        {
            return 0;
        }

        using var read = await source.OpenReadAsync(
            entry.Key, new ObjectRange(0, (long)claim.Staged), cancellationToken).ConfigureAwait(false);
        if (read.Outcome != OpenReadOutcome.Found || read.Content is null)
        {
            return 0;
        }

        var mine = await SHA256.HashDataAsync(read.Content, cancellationToken).ConfigureAwait(false);
        if (mine.AsSpan().SequenceEqual(claim.Digest.Span))
        {
            Log.ObjectResumed(log, entry.Key, claim.Staged);
            return claim.Staged;
        }

        Log.ObjectResumeRefused(log, entry.Key, claim.Staged);
        return 0;
    }

    private static async Task<(HashSet<string> Held, ulong? Headroom)> ReadInventoryAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        ulong? headroom = null;
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

            if (!page.More)
            {
                return (held, headroom);
            }
        }
    }

    /// <summary>
    /// Sends one object and answers the offset it began at — zero today, and
    /// the resume point once the destination can say it holds a prefix
    /// ([ADR-0057](../../docs/adr/0057-resumable-object-transfer.md)).
    /// </summary>
    /// <param name="source">The store holding the object.</param>
    /// <param name="entry">Its listing entry — the length comes from here.</param>
    /// <param name="stream">The session stream.</param>
    /// <param name="partials">What the destination declared it part holds.</param>
    /// <param name="log">Where a resumed or refused prefix is reported.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    private static async Task<ulong> SendObjectAsync(
        IObjectStore source,
        ObjectEntry entry,
        Stream stream,
        IReadOnlyDictionary<string, (ulong Staged, ReadOnlyMemory<byte> Digest)> partials,
        ILogger log,
        CancellationToken cancellationToken)
    {
        var from = await ResumePointAsync(source, entry, partials, log, cancellationToken).ConfigureAwait(false);

        // A ranged read of exactly the tail. The range cannot say "to the end"
        // (its length is required), which is no obstacle: the listing already
        // said how long the object is.
        using var read = from > 0
            ? await source.OpenReadAsync(
                entry.Key, new ObjectRange((long)from, entry.Length - (long)from), cancellationToken)
                .ConfigureAwait(false)
            : await source.OpenReadAsync(entry.Key, range: null, cancellationToken).ConfigureAwait(false);
        if (read.Outcome != OpenReadOutcome.Found || read.Content is null)
        {
            // Immutable objects are never deleted in this build, so a key that
            // just listed must still read; treating the impossible as a fault is
            // honest rather than silently shipping a short object.
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"Object {entry.Key.Value} listed but could not be read to send.");
        }

        await PeerFrame.WriteAsync(
            stream, new ReplicationObject(entry.Key.Value, (ulong)entry.Length, from), cancellationToken)
            .ConfigureAwait(false);

        var buffer = new byte[ReplicationChunk.MaximumBytes];
        var offset = from;
        int got;
        while ((got = await read.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await PeerFrame.WriteAsync(
                stream, new ReplicationChunk(offset, buffer.AsMemory(0, got)), cancellationToken).ConfigureAwait(false);
            offset += (ulong)got;
        }

        return from;
    }
}
