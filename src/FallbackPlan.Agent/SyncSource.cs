using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Protocol;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Agent;

/// <summary>
/// What a sync copies from (ADR-0198): a store that lists every blob the set's
/// newest backup needs, or, where none can be read, the reason the copy does
/// not start.
/// </summary>
/// <remarks>
/// <para>
/// The copy carries blobs before the snapshots that name them, so a snapshot
/// it carries is complete at the destination only where its source lists the
/// snapshot's whole closure. A staging set's archive does. A direct-ship set's
/// archive is the union of the destinations it can read outside a run: its
/// local paths, and its stores where they are all it has, and never a peer. So
/// the union is whole only while one of those holds the newest backup whole.
/// </para>
/// <para>
/// The ledger is the evidence: a run records each destination it shipped to as
/// holding everything up to the backup it published. The union serves when the
/// destination is itself whole, when a destination it reads is, or when the
/// ledger names none at all, as for a set adopted from a store, where the
/// union is all there is. Otherwise a whole peer is read over the retrieval
/// session for the blobs the union lacks, paced at that peer's own limit for
/// background work. Where no whole destination answers, the copy does not
/// start, and the reason names where the backup is held.
/// </para>
/// <para>
/// Unless the destination holds every blob already. Its own listing is asked
/// whether it holds every blob the set's index locates: if it does, it holds
/// every backup's closure, and the snapshots and the index it lacks are on
/// this machine (Amendment 1). The archive then serves, and the pass proves
/// nothing, since nothing it reads here is a copy of the destination's blobs.
/// </para>
/// </remarks>
internal sealed class SyncSource : IAsyncDisposable
{
    private readonly PeerRetrievalClient? _peer;

    private SyncSource(IObjectStore? store, string? refusal, PeerRetrievalClient? peer, bool provesContent = true)
    {
        Store = store;
        Refusal = refusal;
        _peer = peer;
        ProvesContent = provesContent;
    }

    /// <summary>The store the copy reads; null where <see cref="Refusal"/> says why there is none.</summary>
    public IObjectStore? Store { get; }

    /// <summary>Why the copy may not start, for the ledger; null when <see cref="Store"/> serves.</summary>
    public string? Refusal { get; }

    /// <summary>
    /// Whether what <see cref="Store"/> reads can stand as the ground truth a
    /// verification compares the destination against. False where the
    /// destination is brought current from this machine's metadata alone, its
    /// blobs being its own: a pass then samples nothing rather than claim a
    /// proof drawn from the metadata plane.
    /// </summary>
    public bool ProvesContent { get; }

    /// <summary>
    /// The source for a copy of <paramref name="set"/> to <paramref name="destination"/>
    /// that will claim everything published up to <paramref name="syncedSequence"/>.
    /// </summary>
    /// <param name="runtime">The service runtime.</param>
    /// <param name="set">The set being copied.</param>
    /// <param name="destination">Where the copy goes.</param>
    /// <param name="archive">The set's archive.</param>
    /// <param name="syncedSequence">The newest publication the copy will claim, read before it starts.</param>
    /// <param name="userInitiated">Whether a person is waiting: a person's read is not paced.</param>
    /// <param name="cancellationToken">Cancels the dial and the listing.</param>
    /// <param name="replica">
    /// The destination's replica, where this side can list it; null for a peer,
    /// whose replica is read over the session the push holds.
    /// </param>
    internal static async ValueTask<SyncSource> ComposeAsync(
        ServiceRuntime runtime, BackupSetConfiguration set, DestinationConfiguration destination,
        ArchiveHandle archive, ulong syncedSequence, bool userInitiated, CancellationToken cancellationToken,
        IObjectStore? replica = null)
    {
        ThrowHelper.ThrowIfNull(runtime);
        ThrowHelper.ThrowIfNull(set);
        ThrowHelper.ThrowIfNull(destination);
        ThrowHelper.ThrowIfNull(archive);

        // A migrating set's staging archive is part of the union and holds the
        // history its runs did not ship (ADR-0046 §3).
        if (archive.ShipSink is not { } sink || syncedSequence == 0 || FanOut.StagingRemains(runtime, set))
        {
            return new SyncSource(archive.Store, refusal: null, peer: null);
        }

        var ledger = runtime.DestinationSync;
        bool Whole(string name) => ledger.Find(set.Id, name)?.SyncedSequence >= syncedSequence;
        if (Whole(destination.Name))
        {
            return new SyncSource(archive.Store, refusal: null, peer: null);
        }

        var configuration = runtime.Configuration;
        var whole = set.Destinations
            .Select(reference => (Reference: reference, Declared: configuration.FindDestination(reference.Ref)))
            .Where(pair => pair.Declared is { } declared
                && !string.Equals(declared.Name, destination.Name, StringComparison.Ordinal)
                && Whole(declared.Name))
            .OrderByDescending(pair => SetDestinationReference.EffectivePriority(pair.Reference, pair.Declared!))
            .Select(pair => pair.Declared!)
            .ToList();
        var readable = sink.ReadableHolders();
        if (whole.Count == 0 || whole.Exists(holder => readable.Contains(holder.Name)))
        {
            return new SyncSource(archive.Store, refusal: null, peer: null);
        }

        var unanswered = new List<string>();
        foreach (var peer in whole.Where(holder => holder is { Kind: DestinationKind.Peer, AddressDefect: null }))
        {
            PeerRetrievalClient client;
            try
            {
                client = await PeerRetrievalClient.DialAsync(
                    runtime, peer, archive.Repository.RepositoryId.ToArray(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (PeerFilledObjectStore.IsPeerFailure(exception))
            {
                unanswered.Add($"'{peer.Name}': {exception.Message}");
                continue;
            }

            var log = runtime.LoggerFor<SyncSource>();
            Log.SyncReadsFromPeer(log, new LogLabel(set.Name), new LogLabel(destination.Name), new LogLabel(peer.Name));
            var read = PacedObjectStore.Over(
                new PeerRetrievalObjectStore(client), userInitiated ? null : runtime.Pacing.ForDestination(peer));
            return new SyncSource(new PeerFilledObjectStore(archive.Store, read, peer.Name), refusal: null, client);
        }

        // Nothing whole answers. The destination may hold every blob itself,
        // sent as the run went (ADR-0196 Amendment 8): then the metadata here
        // is all it lacks.
        if (replica is not null && await HoldsEveryLocatedBlobAsync(archive, replica, cancellationToken).ConfigureAwait(false))
        {
            var log = runtime.LoggerFor<SyncSource>();
            Log.SyncHoldsEveryBlob(log, new LogLabel(set.Name), new LogLabel(destination.Name));
            return new SyncSource(archive.Store, refusal: null, peer: null, provesContent: false);
        }

        var held = string.Join(" and ", whole.Select(holder => $"'{holder.Name}'"));
        var why = unanswered.Count > 0 ? $" ({string.Join("; ", unanswered)})" : string.Empty;
        return new SyncSource(
            store: null,
            $"the set's newest backup is held whole only at {held}, which could not be read{why}; "
            + "this destination is brought current from there once it answers",
            peer: null);
    }

    /// <summary>
    /// Whether <paramref name="replica"/> holds every blob the set's index
    /// locates, by its own listing: every blob some object is located in,
    /// under the key this repository files it at.
    /// </summary>
    private static async ValueTask<bool> HoldsEveryLocatedBlobAsync(
        ArchiveHandle archive, IObjectStore replica, CancellationToken cancellationToken)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in replica.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            var key = entry.Key.Value;
            held.Add(key[(key.LastIndexOf('/') + 1)..]);
        }

        using var catalogue = archive.OpenReadCatalogue();
        using var derive = new StoreBlobKeyDeriver(archive.Repository.Keys.KeyIdKey);
        return catalogue.LocatedBlobs().All(blob => held.Contains(derive.Derive(blob).ToBase32()));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_peer is not null)
        {
            await _peer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
